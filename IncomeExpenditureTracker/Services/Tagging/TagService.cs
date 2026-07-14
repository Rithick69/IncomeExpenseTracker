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

namespace IncomeExpenditureTracker.Services.Tagging;

public class TagService : ITagService
{
    private readonly IDatabaseService _databaseService;
    private readonly DescriptionParser _descriptionParser;
    private readonly ILogger<TagService> _logger;

    // Thread-safe cache registry for stampede defense during multi-file staging
    private readonly ConcurrentDictionary<string, Lazy<Task<RuleBookSnapshot>>> _cache = new();
    private const string CACHE_KEY = "MasterRuleBookSnapshot";

    private const string RULES_SQL = "SELECT Keyword, TagId, Priority FROM TagRules ORDER BY Priority DESC, Id DESC;";
    private const string MISC_SQL = "SELECT Id FROM Tags WHERE Name = 'Misc' LIMIT 1;";

    public TagService(
        IDatabaseService databaseService,
        DescriptionParser descriptionParser,
        ILogger<TagService> logger)
    {
        _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
        _descriptionParser = descriptionParser ?? throw new ArgumentNullException(nameof(descriptionParser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<RuleBookSnapshot> GetRuleBookSnapshotAsync()
    {
        _logger.LogDebug("Requesting RuleBookSnapshot from cache or database.");

        return _cache.GetOrAdd(CACHE_KEY, _ => new Lazy<Task<RuleBookSnapshot>>(async () =>
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
                _cache.TryRemove(CACHE_KEY, out var _);
                throw;
            }
        })).Value;
    }

    public void InvalidateCache()
    {
        _logger.LogInformation("Invalidating RuleBookSnapshot RAM cache.");
        _cache.TryRemove(CACHE_KEY, out _);
    }

    public async Task<int> GetOrCreateTagAsync(string name, int? subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tag name cannot be empty.", nameof(name));

        const string sql = @"
            INSERT OR IGNORE INTO Tags (Name, SubCategoryId) VALUES (@Name, @SubCategoryId);
            SELECT Id FROM Tags WHERE Name = @Name LIMIT 1;";

        try
        {
            // If inside a master import transaction, execute directly on the transactional connection
            if (conn != null && tx != null)
            {
                _logger.LogDebug("Executing transactional GetOrCreateTagAsync for tag: {TagName}", name);
                return await conn.ExecuteScalarAsync<int>(sql, new { Name = name, SubCategoryId = subCategoryId }, tx);
            }

            _logger.LogDebug("Executing standalone GetOrCreateTagAsync for tag: {TagName}", name);
            return await _databaseService.ExecuteWithRetryAsync(c =>
                c.ExecuteScalarAsync<int>(sql, new { Name = name, SubCategoryId = subCategoryId }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to GetOrCreateTagAsync for tag '{TagName}' under SubCategoryId '{SubCatId}'.", name, subCategoryId);
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

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to UpdateTagAsync for TagId {TagId}.", tagId);
            throw;
        }
    }

    public async Task DeleteTagAsync(int tagId)
    {
        try
        {
            _logger.LogInformation("Attempting deletion for TagId {TagId}.", tagId);

            await _databaseService.ExecuteWithRetryAsync(async conn =>
            {
                // 1. Delete associated rules first to keep schema clean
                await conn.ExecuteAsync("DELETE FROM TagRules WHERE TagId = @Id;", new { Id = tagId });

                // 2. Delete tag (Will throw SQLite FK Exception if historical transactions reference this TagId!)
                await conn.ExecuteAsync("DELETE FROM Tags WHERE Id = @Id;", new { Id = tagId });
            });

            InvalidateCache();
            _logger.LogInformation("Successfully deleted TagId {TagId} and its associated rules.", tagId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to DeleteTagAsync for TagId {TagId}. Ensure no historical transactions reference this tag.", tagId);
            throw;
        }
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
        }
    }
}