using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Models;
using System.Threading;

namespace IncomeExpenditureTracker.Services.Entities;

public class TagService : ITagService
{
    private readonly IDatabaseService _databaseService;
    private readonly IDescriptionParser _descriptionParser;
    private readonly ILogger<TagService> _logger;

    // Thread-safe cache registry for stampede defense during multi-file staging
    private readonly ConcurrentDictionary<string, Lazy<Task<RuleBookSnapshot>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Tag>>>> _allTagscache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, int> _tagIdByNameCache = new(StringComparer.OrdinalIgnoreCase);
    private const string RULE_CACHE_KEY = "MasterRuleBookSnapshot";

    private const string RULES_SQL = "SELECT Keyword, TagId, Priority FROM TagRules ORDER BY Priority DESC, Id DESC;";
    private const string MISC_SQL = "SELECT Id FROM Tags WHERE Name = 'Misc' LIMIT 1;";

    public TagService(
        IDatabaseService databaseService,
        IDescriptionParser descriptionParser,
        ILogger<TagService> logger)
    {
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _descriptionParser = descriptionParser ?? throw new ArgumentNullException(nameof(descriptionParser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // =========================================================================
    // TAG MANAGEMENT
    // =========================================================================

    #region Tag Management

    public async Task<int> GetOrCreateTagAsync(string name, int? subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        // 1. Check cache first (bypass if inside a transaction)
        if (conn == null && _tagIdByNameCache.TryGetValue(name, out var cachedId))
        {
            return cachedId;
        }

        const string sql = @"
            INSERT OR IGNORE INTO Tags (Name, SubCategoryId) VALUES (@Name, @SubCategoryId);
            SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

        try
        {
            int tagId;

            // If inside a master import transaction, execute directly on the transactional connection
            if (conn != null && tx != null)
            {
                _logger.LogDebug("Executing transactional GetOrCreateTagAsync for tag: {TagName}", name);
                tagId = await conn.ExecuteScalarAsync<int>(sql, new { Name = name, SubCategoryId = subCategoryId }, tx);
                return tagId;
            }

            _logger.LogDebug("Executing standalone GetOrCreateTagAsync for tag: {TagName}", name);


            tagId = await _databaseService.ExecuteWithRetryAsync(c =>
                c.ExecuteScalarAsync<int>(sql, new { Name = name, SubCategoryId = subCategoryId }));

            // 4. Update the name -> ID cache
            _tagIdByNameCache.TryAdd(name, tagId);

            _allTagscache.Clear();

            return tagId;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to GetOrCreateTagAsync for tag '{TagName}' under SubCategoryId '{SubCatId}'.", name, subCategoryId);
            throw;
        }
    }

    public async Task<int> GetTagIdByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        // 1. Check cache first (No more connection check needed)
        if (_tagIdByNameCache.TryGetValue(name, out var cachedId))
        {
            return cachedId;
        }

        const string sql = "SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

        try
        {
            _logger.LogDebug("Executing standalone GetTagIdByName for tag: {TagName}", name);

            // Execute cleanly using the retry policy
            var tagId = await _databaseService.ExecuteWithRetryAsync(c =>
                c.ExecuteScalarAsync<int>(sql, new { Name = name }));

            // Update the cache if a valid ID was returned
            if (tagId > 0)
            {
                _tagIdByNameCache.TryAdd(name, tagId);
            }

            return tagId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to GetTagIdByName for tag '{TagName}'.", name);
            throw;
        }
    }

    public async Task<List<Tag>> GetAllTags()
    {
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
                    var result = await _databaseService.ExecuteWithRetryAsync(c => c.QueryAsync<Tag>(sql));
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute GetAllTags");
            throw;
        }
    }

    public async Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        const string sql = "UPDATE Tags SET Name = @Name, SubCategoryId = @SubCategoryId WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Updating TagId {TagId}: New Name='{Name}', SubCategoryId={SubCatId}", tagId, name, subCategoryId);

            await _databaseService.ExecuteWithRetryAsync(conn =>
                conn.ExecuteAsync(sql, new { Id = tagId, Name = name, SubCategoryId = subCategoryId }));

            InvalidateTagCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to UpdateTagAsync for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteTagAsync(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            _logger.LogInformation("Attempting deletion for TagId {TagId}.", tagId);

            if (conn != null && tx != null)
            {
                _logger.LogDebug("Executing transactional DeleteTagAsync for TagId {TagId}.", tagId);
                await conn.ExecuteAsync("DELETE FROM TagRules WHERE TagId = @Id;", new { Id = tagId }, tx);
                await conn.ExecuteAsync("DELETE FROM Tags WHERE Id = @Id;", new { Id = tagId }, tx);
            }
            else
            {
                _logger.LogDebug("Executing standalone DeleteTagAsync for TagId {TagId}.", tagId);
                await _databaseService.ExecuteWithRetryAsync(async c =>
                {
                    await c.ExecuteAsync("DELETE FROM TagRules WHERE TagId = @Id;", new { Id = tagId });
                    await c.ExecuteAsync("DELETE FROM Tags WHERE Id = @Id;", new { Id = tagId });
                });
            }

            InvalidateTagCache();
            InvalidateCache();
            _logger.LogInformation("Successfully deleted TagId {TagId} and its associated rules.", tagId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to DeleteTagAsync for TagId {TagId}. Ensure no historical transactions reference this tag.", tagId);
            throw;
        }
    }

    public async Task FloatTagsBySubCategoryAsync(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = "UPDATE Tags SET SubCategoryId = NULL WHERE SubCategoryId = @SubCategoryId;";

            if (conn != null && tx != null)
                await conn.ExecuteAsync(sql, new { SubCategoryId = subCategoryId }, tx);
            else
                await _databaseService.ExecuteWithRetryAsync(async (c) => await c.ExecuteAsync(sql, new { SubCategoryId = subCategoryId }));

            InvalidateTagCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float tags for SubCategoryId {SubCategoryId}.", subCategoryId);
            throw;
        }
    }

    public async Task FloatTagsByCategoryAsync(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = @"
                UPDATE Tags
                SET SubCategoryId = NULL
                WHERE SubCategoryId IN (SELECT Id FROM SubCategories WHERE CategoryId = @CategoryId);";

            if (conn != null && tx != null)
                await conn.ExecuteAsync(sql, new { CategoryId = categoryId }, tx);
            else
                await _databaseService.ExecuteWithRetryAsync(async (c) => await c.ExecuteAsync(sql, new { CategoryId = categoryId }));

            InvalidateTagCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to float tags for CategoryId {CategoryId}.", categoryId);
            throw;
        }
    }

    #endregion

    // =========================================================================
    // TAG RULE MANAGEMENT
    // =========================================================================

    #region Tag Rule Management

    public Task<RuleBookSnapshot> GetRuleBookSnapshotAsync()
    {
        _logger.LogDebug("Requesting RuleBookSnapshot from cache or database.");

        return _cache.GetOrAdd(RULE_CACHE_KEY, _ => new Lazy<Task<RuleBookSnapshot>>(async () =>
        {
            try
            {
                return await _databaseService.ExecuteWithRetryAsync(async conn =>
                {
                    _logger.LogInformation("Cache miss. Executing SQLite read to build RuleBookSnapshot.");

                    var miscId = await conn.ExecuteScalarAsync<int?>(MISC_SQL) ?? 0;
                    var rawRules = await conn.QueryAsync<TagRuleDTO>(RULES_SQL);

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
                });
            }
            catch (Exception ex)
            {
                // Fault Eviction: Never allow an exception to remain cached in server RAM
                _logger.LogError(ex, "Critical failure while building RuleBookSnapshot from SQLite. Evicting cache.");
                _cache.TryRemove(RULE_CACHE_KEY, out var _);
                throw;
            }
        })).Value;
    }

    public async Task<int> AddRuleAsync(string keyword, int tagId, int priority = 10)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Rule keyword cannot be empty.", nameof(keyword));

        const string sql = "INSERT INTO TagRules (Keyword, TagId, Priority) VALUES (@Keyword, @TagId, @Priority); SELECT last_insert_rowid();";

        try
        {
            _logger.LogDebug("Adding new TagRule: Keyword='{Keyword}', TagId={TagId}, Priority={Priority}", keyword, tagId, priority);

            var id = await _databaseService.ExecuteWithRetryAsync(conn =>
                conn.ExecuteScalarAsync<int>(sql, new { Keyword = keyword.ToUpperInvariant(), TagId = tagId, Priority = priority }));

            InvalidateCache(); // Drop RAM pointer so subsequent reads load the new rule
            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to AddRuleAsync for Keyword='{Keyword}' pointing to TagId={TagId}.", keyword, tagId);
            throw;
        }
    }

    public async Task UpdateRuleAsync(int ruleId, string keyword, int tagId, int priority)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            throw new ArgumentException("Rule keyword cannot be empty.", nameof(keyword));

        const string sql = "UPDATE TagRules SET Keyword = @Keyword, TagId = @TagId, Priority = @Priority WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Updating RuleId {RuleId}: Keyword='{Keyword}', TagId={TagId}, Priority={Priority}", ruleId, keyword, tagId, priority);

            await _databaseService.ExecuteWithRetryAsync(conn =>
                conn.ExecuteAsync(sql, new { Id = ruleId, Keyword = keyword.ToUpperInvariant(), TagId = tagId, Priority = priority }));

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to UpdateRuleAsync for RuleId {RuleId}.", ruleId);
            throw;
        }
    }

    public async Task DeleteRuleKeywordsAsync(IEnumerable<string> keywords, int tagId)
    {
        if (keywords == null || !keywords.Any())
            return;

        const string sql = "DELETE FROM TagRules WHERE Keyword IN @Keywords AND TagId = @TagId;";

        try
        {
            _logger.LogDebug("Deleting keywords from TagRules where TagId={TagId}", tagId);

            await _databaseService.ExecuteWithRetryAsync(conn =>
                conn.ExecuteAsync(sql, new { Keywords = keywords, TagId = tagId }));

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete keywords for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteRulesByTagId(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        const string sql = "DELETE FROM TagRules WHERE TagId = @TagId;";

        try
        {
            _logger.LogDebug("Deleting all rules for TagId {TagId}.", tagId);

            if (conn != null && tx != null)
            {
                await conn.ExecuteAsync(sql, new { TagId = tagId }, tx);
            }
            else
            {
                await _databaseService.ExecuteWithRetryAsync(c =>
                    c.ExecuteAsync(sql, new { TagId = tagId }));
            }

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete rules for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteRuleAsync(int ruleId)
    {
        const string sql = "DELETE FROM TagRules WHERE Id = @Id;";

        try
        {
            _logger.LogDebug("Deleting RuleId {RuleId}.", ruleId);

            await _databaseService.ExecuteWithRetryAsync(conn =>
                conn.ExecuteAsync(sql, new { Id = ruleId }));

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to DeleteRuleAsync for RuleId {RuleId}.", ruleId);
            throw;
        }
    }

    public async Task LearnRuleFromOverrideAsync(string rawDescription, int targetTagId)
    {
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

            await _databaseService.ExecuteWithRetryAsync(async conn =>
            {
                // Execute priority math inside an isolated retry wrapper
                var maxPriority = await conn.ExecuteScalarAsync<int>(maxPriSql, new { TagId = targetTagId });
                int newPriority = maxPriority + 1;

                _logger.LogDebug("Learned best keyword '{Keyword}' for TagId {TagId}. Assigning Priority {Priority}", bestKeyword, targetTagId, newPriority);

                await conn.ExecuteAsync(insertSql, new { Keyword = bestKeyword, TagId = targetTagId, Priority = newPriority });
            });

            InvalidateCache(); // Ensure next statement import utilizes this newly learned rule
            _logger.LogInformation("Successfully completed self-learning sequence for TagId {TagId}.", targetTagId);
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
        _cache.TryRemove(RULE_CACHE_KEY, out _);
    }
}