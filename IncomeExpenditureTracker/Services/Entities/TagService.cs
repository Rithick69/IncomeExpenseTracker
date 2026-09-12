using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Models;
using System.Threading;

namespace IncomeExpenditureTracker.Services.Entities;

public class TagService : ITagService, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly IDescriptionParser _descriptionParser;
    private readonly ILogger<TagService> _logger;

    private readonly IApplicationBroker _broker;

    // Thread-safe cache registry for stampede defense during multi-file staging
    private readonly ConcurrentDictionary<string, Lazy<Task<RuleBookSnapshot>>> _ruleCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Tag>>>> _allTagscache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _tagIdByNameCache = new(StringComparer.OrdinalIgnoreCase);
    private const string RULE_CACHE_KEY = "MasterRuleBookSnapshot";

    private const string RULES_SQL = "SELECT Keyword, TagId, Priority FROM TagRules ORDER BY Priority DESC, Id DESC;";
    private const string MISC_SQL = "SELECT Id FROM Tags WHERE Name = 'Misc' LIMIT 1;";

    public TagService(
        IDatabaseService databaseService,
        IDescriptionParser descriptionParser,
        ILogger<TagService> logger,
        IApplicationBroker broker)
    {
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _descriptionParser = descriptionParser ?? throw new ArgumentNullException(nameof(descriptionParser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));

        // -------------------------------------------------------------------------
        // ARCHITECTURAL GUARDRAIL: CACHE ANNIHILATION
        // -------------------------------------------------------------------------
        // When the database swaps, we MUST wipe the ConcurrentDictionary and RuleBook
        // to prevent Profile A's tags from appearing in Profile B's UI.
        // -------------------------------------------------------------------------
        _broker.Register<ProfileSwappedMessage>(this, (message) => ClearCache());
    }

    // =========================================================================
    // TAG MANAGEMENT
    // =========================================================================

    #region Tag Management

    public async Task<int> GetOrCreateTagAsync(string name, int? subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        var cacheKey = name.Trim();

        // const string sql = @"
        //     INSERT OR IGNORE INTO Tags (Name, SubCategoryId) VALUES (@Name, @SubCategoryId);
        //     SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

        try
        {
            // If inside a master import transaction, execute directly on the transactional connection
            if (conn != null && tx != null)
            {
                // Safe read from cache if it already exists and isn't faulted/cancelled
                if (_tagIdByNameCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted && !existingLazy.Value.IsCanceled)
                {
                    return await existingLazy.Value;
                }

                _logger.LogDebug("Executing transactional GetOrCreateTagAsync for tag: {TagName}", cacheKey);

                // Execute directly, do NOT cache the result (to protect against rollbacks)
                return await ExecuteUpsertInternalAsync(cacheKey, subCategoryId, conn, tx, ct);
            }

            // -------------------------------------------------------------------------
            // STANDALONE PATH (Cache Stampede Protected)
            // -------------------------------------------------------------------------
            var lazyTagId = _tagIdByNameCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    _logger.LogDebug("Executing standalone GetOrCreateTagAsync for tag: {TagName}", cacheKey);

                    // Execute cleanly using the retry policy wrapper
                    var tagId = await _databaseService.ExecuteWithRetryAsync(async (retryConn, cancelToken) =>
                        await ExecuteUpsertInternalAsync(cacheKey, subCategoryId, retryConn, null, cancelToken), ct);

                    // Invalidate the "All Tags" list cache since we potentially inserted a new tag
                    _allTagscache.Clear();

                    return tagId;
                }
                catch
                {
                    // Fault Eviction: If DB crashes or is cancelled, remove the poisoned task
                    _tagIdByNameCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await lazyTagId.Value;

        }
        catch (OperationCanceledException)
        {
            throw; // Bubble up silently for the UI router
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to GetOrCreateTagAsync for tag '{TagName}' under SubCategoryId '{SubCatId}'.", name, subCategoryId);
            throw;
        }
    }

    public async Task<int> GetTagIdByName(string name, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        var cacheKey = name.Trim();

        try
        {
            // GetOrAdd guarantees that the Lazy factory executes exactly once per unique tag name,
            // even if 50 threads hit this line concurrently.
            var lazyTagId = _tagIdByNameCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    _logger.LogDebug("Cache miss. Executing SQLite read for tag: {TagName}", cacheKey);

                    return await _databaseService.ExecuteWithRetryAsync(async (c, cancelToken) =>
                    {
                        const string sql = "SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

                        // Use CommandDefinition to pass the cancellation token
                        var cmd = new CommandDefinition(sql, new { Name = cacheKey }, cancellationToken: cancelToken);
                        var id = await c.ExecuteScalarAsync<int?>(cmd); // Use int? in case it doesn't exist

                        return id ?? 0; // Return 0 if not found
                    }, ct);
                }
                catch
                {
                    // Fault Eviction: If the DB read fails or is cancelled, remove it so it can be retried later
                    _tagIdByNameCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await lazyTagId.Value;
        }
        catch (OperationCanceledException)
        {
            throw; // Bubble up silently
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to GetTagIdByName for tag '{TagName}'.", cacheKey);
            throw;
        }
    }

    public async Task<List<Tag>> GetAllTags(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const string ALL_TAGS_KEY = "ALL_TAGS";
        const string sql = "SELECT * FROM Tags;";

        try
        {
            // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
            var cachedLazy = _allTagscache.GetOrAdd(ALL_TAGS_KEY, _ => new Lazy<Task<List<Tag>>>(async () =>
            {
                try
                {
                    _logger.LogInformation("Cache miss. Executing standalone GetAllTags from database.");
                    var result = await _databaseService.ExecuteWithRetryAsync(async (c, cancelToken) =>
                    {
                        var cmd = new CommandDefinition(
                            sql,
                            cancellationToken: cancelToken
                        );
                        return await c.QueryAsync<Tag>(cmd);
                    }, ct);
                    return result.ToList();
                }
                catch (Exception ex)
                {
                    // Fault Eviction: Remove the broken task from cache if the DB fails
                    _logger.LogError(ex, "Critical failure while building GetAllTags cache. Evicting.");
                    _allTagscache.TryRemove(ALL_TAGS_KEY, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute GetAllTags");
            throw;
        }
    }

    public async Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        const string sql = "UPDATE Tags SET Name = @Name, SubCategoryId = @SubCategoryId WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Updating TagId {TagId}: New Name='{Name}', SubCategoryId={SubCatId}", tagId, name, subCategoryId);

            await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                var cmd = new CommandDefinition(
                    sql,
                    new
                    {
                        Id = tagId,
                        Name = name,
                        SubCategoryId = subCategoryId
                    },
                    cancellationToken: cancelToken
                );
                await conn.ExecuteAsync(cmd);
            }, ct);


            InvalidateTagCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to UpdateTagAsync for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteTagAsync(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            _logger.LogInformation("Attempting deletion for TagId {TagId}.", tagId);

            if (conn != null && tx != null)
            {
                _logger.LogDebug("Executing transactional DeleteTagAsync for TagId {TagId}.", tagId);
                var tagrulecmd = new CommandDefinition(
                    "DELETE FROM TagRules WHERE TagId = @Id;",
                    new { Id = tagId },
                    transaction: tx,
                    cancellationToken: ct
                );

                var tagcmd = new CommandDefinition(
                    "DELETE FROM Tags WHERE Id = @Id;",
                    new { Id = tagId },
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(tagrulecmd);
                await conn.ExecuteAsync(tagcmd);
            }
            else
            {
                _logger.LogDebug("Executing standalone DeleteTagAsync for TagId {TagId}.", tagId);
                await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
                {
                    var tagrulecmd = new CommandDefinition(
                    "DELETE FROM TagRules WHERE TagId = @Id;",
                    new { Id = tagId },
                    cancellationToken: cancelToken
                );

                    var tagcmd = new CommandDefinition(
                        "DELETE FROM Tags WHERE Id = @Id;",
                        new { Id = tagId },
                        cancellationToken: cancelToken
                    );
                    await conn.ExecuteAsync(tagrulecmd);
                    await conn.ExecuteAsync(tagcmd);
                }, ct);
            }

            InvalidateTagCache();
            InvalidateCache();
            _logger.LogInformation("Successfully deleted TagId {TagId} and its associated rules.", tagId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to DeleteTagAsync for TagId {TagId}. Ensure no historical transactions reference this tag.", tagId);
            throw;
        }
    }

    public async Task FloatTagsBySubCategoryAsync(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            const string sql = "UPDATE Tags SET SubCategoryId = NULL WHERE SubCategoryId = @SubCategoryId;";

            if (conn != null && tx != null)
            {
                var cmd = new CommandDefinition(
                    sql,
                    new { SubCategoryId = subCategoryId },
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(cmd);
            }
            else
            {
                await _databaseService.ExecuteWithRetryAsync(async (c, cancelToken) =>
                {
                    var cmd = new CommandDefinition(
                        sql,
                        new { SubCategoryId = subCategoryId },
                        cancellationToken: cancelToken
                    );
                    await c.ExecuteAsync(cmd);
                }, ct);
            }

            InvalidateTagCache(); // Evict cache after mutation to ensure consistency
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float tags for SubCategoryId {SubCategoryId}.", subCategoryId);
            throw;
        }
    }

    public async Task FloatTagsByCategoryAsync(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            const string sql = @"
                UPDATE Tags
                SET SubCategoryId = NULL
                WHERE SubCategoryId IN (SELECT Id FROM SubCategories WHERE CategoryId = @CategoryId);";

            if (conn != null && tx != null)
            {
                var cmd = new CommandDefinition(
                    sql,
                    new { CategoryId = categoryId },
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(cmd);
            }
            else
            {
                await _databaseService.ExecuteWithRetryAsync(async (c, cancelToken) =>
                {
                    var cmd = new CommandDefinition(
                        sql,
                        new { CategoryId = categoryId },
                        cancellationToken: cancelToken
                    );
                    await c.ExecuteAsync(cmd);
                }, ct);
            }

            InvalidateTagCache(); // Evict cache after mutation to ensure consistency
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float tags for CategoryId {CategoryId}.", categoryId);
            throw;
        }
    }

    /// <summary>
    /// Extracted helper to keep Dapper CommandDefinition and SQL execution DRY.
    /// </summary>
    private async Task<int> ExecuteUpsertInternalAsync(string name, int? subCategoryId, IDbConnection conn, IDbTransaction? tx, CancellationToken ct)
    {
        const string sql = @"
            INSERT OR IGNORE INTO Tags (Name, SubCategoryId) VALUES (@Name, @SubCategoryId);
            SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

        var cmd = new CommandDefinition(
            sql,
            new { Name = name, SubCategoryId = subCategoryId },
            transaction: tx,
            cancellationToken: ct
        );

        return await conn.ExecuteScalarAsync<int>(cmd);
    }

    #endregion

    // =========================================================================
    // TAG RULE MANAGEMENT
    // =========================================================================

    #region Tag Rule Management

    public Task<RuleBookSnapshot> GetRuleBookSnapshotAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _logger.LogDebug("Requesting RuleBookSnapshot from cache or database.");

        return _ruleCache.GetOrAdd(RULE_CACHE_KEY, _ => new Lazy<Task<RuleBookSnapshot>>(async () =>
        {
            try
            {
                return await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
                {
                    _logger.LogInformation("Cache miss. Executing SQLite read to build RuleBookSnapshot.");

                    var misccmd = new CommandDefinition(
                        MISC_SQL,
                        cancellationToken: cancelToken
                    );

                    var rawRulecmd = new CommandDefinition(
                        RULES_SQL,
                        cancellationToken: cancelToken
                    );

                    var miscId = await conn.ExecuteScalarAsync<int?>(misccmd) ?? 0;
                    var rawRules = await conn.QueryAsync<TagRuleDTO>(rawRulecmd);

                    // Group rules by uppercase keyword into memory-efficient arrays
                    var ruleIndex = rawRules
                        .GroupBy(r => r.Keyword.ToUpperInvariant())
                        .ToDictionary(
                            g => g.Key,
                            g => g.ToArray(),
                            StringComparer.OrdinalIgnoreCase
                        );

                    _logger.LogInformation("Successfully built snapshot with {RuleCount} unique keywords. MiscTagId: {MiscId}",
                        ruleIndex.Count, miscId);

                    return new RuleBookSnapshot(ruleIndex, miscId);
                }, ct);
            }
            catch (OperationCanceledException)
            {
                _ruleCache.TryRemove(RULE_CACHE_KEY, out var _);
                throw;
            }
            catch (Exception ex)
            {
                // Fault Eviction: Never allow an exception to remain cached in server RAM
                _logger.LogError(ex, "Critical failure while building RuleBookSnapshot from SQLite. Evicting cache.");
                _ruleCache.TryRemove(RULE_CACHE_KEY, out var _);
                throw;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public async Task<int> AddRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Rule keyword cannot be empty.", nameof(keyword));

        const string sql = "INSERT INTO TagRules (Keyword, TagId, Priority) VALUES (@Keyword, @TagId, @Priority); SELECT last_insert_rowid();";

        try
        {
            _logger.LogDebug("Adding new TagRule: Keyword='{Keyword}', TagId={TagId}, Priority={Priority}", keyword, tagId, priority);

            var id = await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                var cmd = new CommandDefinition(
                    sql,
                    new
                    {
                        Keyword = keyword.ToUpperInvariant(),
                        TagId = tagId,
                        Priority = priority
                    },
                    cancellationToken: cancelToken
                );
                return await conn.ExecuteScalarAsync<int>(cmd);
            }, ct);

            InvalidateCache(); // Drop RAM pointer so subsequent reads load the new rule
            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to AddRuleAsync for Keyword='{Keyword}' pointing to TagId={TagId}.", keyword, tagId);
            throw;
        }
    }

    public async Task UpdateRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Rule keyword cannot be empty.", nameof(keyword));

        const string sql = "UPDATE TagRules SET Keyword = @Keyword, TagId = @TagId, Priority = @Priority WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Updating RuleId {RuleId}: Keyword='{Keyword}', TagId={TagId}, Priority={Priority}", ruleId, keyword, tagId, priority);

            await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                var cmd = new CommandDefinition(
                    sql,
                    new
                    {
                        Id = ruleId,
                        Keyword = keyword.ToUpperInvariant(),
                        TagId = tagId,
                        Priority = priority
                    },
                    cancellationToken: cancelToken
                );
                await conn.ExecuteAsync(cmd);
            }, ct);

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to UpdateRuleAsync for RuleId {RuleId}.", ruleId);
            throw;
        }
    }

    public async Task DeleteRuleKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (keywords == null || !keywords.Any())
            return;

        const string sql = "DELETE FROM TagRules WHERE Keyword IN @Keywords AND TagId = @TagId;";

        try
        {
            _logger.LogDebug("Deleting keywords from TagRules where TagId={TagId}", tagId);

            await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                var cmd = new CommandDefinition(sql, new { Keywords = keywords, TagId = tagId }, cancellationToken: cancelToken);
                await conn.ExecuteAsync(cmd);
            }, ct);

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete keywords for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteRulesByTagId(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        const string sql = "DELETE FROM TagRules WHERE TagId = @TagId;";

        try
        {
            _logger.LogDebug("Deleting all rules for TagId {TagId}.", tagId);

            if (conn != null && tx != null)
            {
                var cmd = new CommandDefinition(sql, new { TagId = tagId }, transaction: tx, cancellationToken: ct);
                await conn.ExecuteAsync(cmd);
            }
            else
            {
                await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
                {
                    var cmd = new CommandDefinition(sql, new { TagId = tagId }, cancellationToken: cancelToken);
                    await conn.ExecuteAsync(cmd);
                }, ct);
            }

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete rules for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteRuleAsync(int ruleId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const string sql = "DELETE FROM TagRules WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Deleting RuleId {RuleId}.", ruleId);

            await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                var cmd = new CommandDefinition(sql, new { Id = ruleId }, cancellationToken: cancelToken);
                await conn.ExecuteAsync(cmd);
            }, ct);

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to DeleteRuleAsync for RuleId {RuleId}.", ruleId);
            throw;
        }
    }

    public async Task LearnRuleFromOverrideAsync(string rawDescription, int targetTagId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(rawDescription))
        {
            _logger.LogWarning("LearnRuleFromOverrideAsync called with empty description. Aborting learning sequence.");
            return;
        }

        try
        {
            _logger.LogInformation("Initiating background self-learning for description '{Description}' -> TagId {TagId}", rawDescription, targetTagId);

            // 1. Run raw string through the exact same tokenization parser used during ingestion
            var tokens = _descriptionParser.ExtractTokens(rawDescription);

            // 2. Extract the most specific (longest character length) token generated by the sliding window
            // Example: "POS DEBIT STATE BANK OF INDIA DELHI #99281" -> extracts "STATE BANK OF INDIA"
            var bestKeyword = tokens.OrderByDescending(t => t.Length).FirstOrDefault();

            if (string.IsNullOrWhiteSpace(bestKeyword))
            {
                _logger.LogWarning("DescriptionParser generated 0 valid tokens from '{Description}'. Cannot learn rule.", rawDescription);
                return;
            }

            const string maxPriSql = "SELECT COALESCE(MAX(Priority), 10) FROM TagRules WHERE TagId = @TagId;";
            const string insertSql = "INSERT INTO TagRules (Keyword, TagId, Priority) VALUES (@Keyword, @TagId, @Priority);";

            await _databaseService.ExecuteWithRetryAsync(async (conn, cancelToken) =>
            {
                // Execute priority math inside an isolated retry wrapper
                var maxcmd = new CommandDefinition(
                    maxPriSql,
                    new
                    {
                        TagId = targetTagId
                    },
                    cancellationToken: cancelToken
                );
                var maxPriority = await conn.ExecuteScalarAsync<int>(maxcmd);
                int newPriority = maxPriority + 1;

                _logger.LogDebug("Learned best keyword '{Keyword}' for TagId {TagId}. Assigning Priority {Priority}", bestKeyword, targetTagId, newPriority);

                var insertcmd = new CommandDefinition(
                    insertSql,
                    new { Keyword = bestKeyword, TagId = targetTagId, Priority = newPriority },
                    cancellationToken: cancelToken
                );

                await conn.ExecuteAsync(insertcmd);
            }, ct);

            InvalidateCache(); // Ensure next statement import utilizes this newly learned rule
            _logger.LogInformation("Successfully completed self-learning sequence for TagId {TagId}.", targetTagId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log warning instead of rethrowing to ensure background learning failures never crash the UI
            _logger.LogError(ex, "Background self-learning failed for description '{Description}' and TagId {TagId}.", rawDescription, targetTagId);
            throw;
        }
    }

    #endregion

    public void InvalidateTagCache()
    {
        _allTagscache.Clear();
        _tagIdByNameCache.Clear();
        _logger.LogInformation("Invalidating Tag Ram cache");
    }

    public void InvalidateCache()
    {
        _logger.LogInformation("Invalidating RuleBookSnapshot RAM cache.");
        _ruleCache.TryRemove(RULE_CACHE_KEY, out _);
    }


    /// <summary>
    /// Executes a hard reset of all in-memory tag structures.
    /// Triggered dynamically by the IApplicationBroker when a user switches profiles.
    /// </summary>
    private void ClearCache()
    {
        // Clear the ConcurrentDictionary<string, Lazy<...>>
        _allTagscache.Clear();
        _tagIdByNameCache.Clear();
        // Nullify the RuleBookSnapshot so the next transaction extraction
        // is forced to fetch the new profile's rules from the database.
        _ruleCache.TryRemove(RULE_CACHE_KEY, out _);

        _logger.LogInformation("Profile swap detected. TagService cache successfully annihilated to prevent data bleed.");
    }

    public void Dispose()
    {
        _broker.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}