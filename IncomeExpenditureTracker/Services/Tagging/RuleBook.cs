using System;
using Dapper;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Tagging;

// ------------------------------------------------------------
// RULEBOOK
// ------------------------------------------------------------
// This class is responsible for maintaining the in-memory
// rule index used by the TagEngine.
//
// Why this exists:
// Building the rule lookup every time the TagEngine runs
// would be expensive and unnecessary.
//
// Instead:
// - Rules are loaded once when the app starts
// - Stored in memory as a dictionary
// - TagEngine reads from this dictionary
//
// When rules change (user adds/edits rules):
// RuleBook.Refresh() is called to rebuild the index.
//
// Data stored here:
// Keyword → (TagId, Priority)
// ------------------------------------------------------------
public class RuleBook : IRuleBook
{

    // ------------------------------------------------------------
    // RULE INDEX
    // ------------------------------------------------------------
    // Dictionary used for fast rule lookup.
    // Key:
    // Keyword extracted from description tokens
    // Value:
    // Tuple containing:
    // - TagId
    // - Priority
    // Priority determines which rule wins when multiple
    // tokens match.
    // ------------------------------------------------------------

    // ------------------------------------------------------------
    // DEFAULT FALLBACK TAG
    // ------------------------------------------------------------
    // If no rule matches a transaction, we assign this tag.
    // Example:
    // "Misc"
    // ------------------------------------------------------------

    private readonly IDatabaseService _database;
    private readonly ILogger<RuleBook> _logger;

    private readonly object _lock = new();
    private Lazy<Task<RuleBookSnapshot>>? _lazySnapshot;

    public RuleBook(IDatabaseService database, ILogger<RuleBook> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------
    // LOAD RULES FROM DATABASE
    // ------------------------------------------------------------
    // Called during application startup.
    // This method:
    // 1. Reads TagRules table
    // 2. Builds the in-memory rule dictionary
    // 3. Fetches fallback "Misc" tag
    // ------------------------------------------------------------

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<TagRule>>> GetRuleIndexAsync()
    {
        var snapshot = await GetSnapshotWithStampedeDefenseAsync();
        return snapshot.RuleIndex;
    }

    public async Task<int> GetMiscTagIdAsync()
    {
        var snapshot = await GetSnapshotWithStampedeDefenseAsync();
        return snapshot.MiscTagId;
    }

    /// <summary>
    /// Replaces the legacy Refresh() method. Atomically drops the cached snapshot pointer so subsequent queries
    /// rebuild the synchronized state from SQLite[cite: 1, 8].
    /// </summary>
    public void InvalidateCache()
    {
        lock (_lock)
        {
            _lazySnapshot = null;
            _logger.LogInformation("Evicted RuleBook RAM cache due to rule or tag modification.");
        }
    }

    /// <summary>
    /// Core Async Lazy stampede defense. Guarantees that if multiple extraction threads hit an empty cache
    /// simultaneously during StageFilesAsync, only 1 thread executes the SQLite queries[cite: 1].
    /// </summary>
    private async Task<RuleBookSnapshot> GetSnapshotWithStampedeDefenseAsync()
    {
        Lazy<Task<RuleBookSnapshot>> targetLazy;

        lock (_lock)
        {
            if (_lazySnapshot == null)
            {
                _lazySnapshot = new Lazy<Task<RuleBookSnapshot>>(() => LoadSnapshotFromDbAsync());
            }
            targetLazy = _lazySnapshot;
        }

        try
        {
            return await targetLazy.Value;
        }
        catch (Exception ex)
        {
            // Fault Eviction: Remove poisoned tasks so subsequent extraction loops retry cleanly[cite: 1]
            _logger.LogError(ex, "Failed to load RuleBook snapshot from database. Evicting faulted cache pointer.");
            lock (_lock)
            {
                if (_lazySnapshot == targetLazy)
                {
                    _lazySnapshot = null;
                }
            }
            throw; // Bubble up to abort extraction and trigger Phase 4 UI notifications[cite: 1]
        }
    }
    /// <summary>
    /// Reads TagRules and fallback classifications from SQLite via resilient retry wrappers[cite: 1, 8].
    /// </summary>
    private async Task<RuleBookSnapshot> LoadSnapshotFromDbAsync()
    {
        _logger.LogInformation("RuleBook cache miss. Querying SQLite to build immutable tagging index...");

        return await _database.ExecuteWithRetryAsync(async connection =>
        {
            // 1. Fetch all rules and the fallback classification in an asynchronous batch
            const string rulesSql = "SELECT Keyword, TagId, Priority FROM TagRules;";
            const string miscSql = "SELECT Id FROM Tags WHERE Name = 'Misc' LIMIT 1;";

            var rules = await connection.QueryAsync<TagRule>(rulesSql);
            var miscTag = await connection.QueryFirstOrDefaultAsync<int?>(miscSql);

            if (miscTag == null)
            {
                throw new InvalidOperationException("Critical Schema Failure: Fallback tag 'Misc' is missing from Tags table.");
            }

            // 2. Build the O(1) case-insensitive dictionary
            // We group by normalized uppercase keyword and sort inner lists by Priority DESC.
            // This guarantees TagEngine evaluates highest priority rules first without runtime sorting.
            var ruleIndex = rules
                .Where(r => !string.IsNullOrWhiteSpace(r.Keyword))
                .GroupBy(r => r.Keyword.Trim().ToUpperInvariant())
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyList<TagRule>)g.OrderByDescending(r => r.Priority).ToList().AsReadOnly(),
                        StringComparer.OrdinalIgnoreCase
                    );

            _logger.LogDebug("Successfully built RuleBook RAM snapshot containing {RuleCount} unique keywords (Misc Tag ID: {MiscId}).", ruleIndex.Count, miscTag.Value);

            return new RuleBookSnapshot(ruleIndex.AsReadOnly(), miscTag.Value);
        });
    }
}

// ------------------------------------------------------------
// REFRESH RULEBOOK
// ------------------------------------------------------------
// Called when user modifies rules in UI.
// This simply reloads the rule index from database.
// -------------------------------------------------------------
//     public void Refresh()
// {
//     try
//     {
//         Load();
//     }
//     catch (Exception ex)
//     {
//         Console.WriteLine($"[RuleBook] Failed to refresh rules: {ex.Message}");
//         throw;
//     }
// }
