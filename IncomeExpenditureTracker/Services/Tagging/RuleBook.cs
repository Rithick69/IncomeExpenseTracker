using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
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
public class RuleBook
{
    private readonly IDatabaseService _database;

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
    public Dictionary<string, List<TagRule>> RuleIndex { get; private set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------
    // DEFAULT FALLBACK TAG
    // ------------------------------------------------------------
    // If no rule matches a transaction, we assign this tag.
    // Example:
    // "Misc"
    // ------------------------------------------------------------

    public int MiscTagId { get; private set; }

    public RuleBook(IDatabaseService database)
    {
        _database = database;
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
    public void Load()
    {
        try
        {
            using var connection = _database.GetConnection();

            // Fetch all rules from database
            var rules = connection.Query<TagRule>(
                "SELECT Keyword, TagId, Priority FROM TagRules"
            ).ToList();

            // Temporary dictionary used while rebuilding the index
            var index = new Dictionary<string, List<TagRule>>(
                StringComparer.OrdinalIgnoreCase
            );

            foreach (var rule in rules)
            {
                // Guard against bad data
                if (string.IsNullOrWhiteSpace(rule.Keyword))
                    continue;

                var key = rule.Keyword.Trim().ToUpper();

                // Add rule to index
                // If multiple rules have the same keyword, we store them in a list
                // This allows us to support multiple rules for the same token, and we can
                // determine the best one based on priority during tagging.

                if (!index.TryGetValue(key, out var ruleList))
                {
                    ruleList = new List<TagRule>();
                    index[key] = ruleList;
                }
                ruleList.Add(new TagRule
                {
                    Keyword = key,
                    TagId = rule.TagId,
                    Priority = rule.Priority
                });
            }

            // Fetch fallback tag (Misc)
            var miscTag = connection.QueryFirstOrDefault<int?>(
                "SELECT Id FROM Tags WHERE Name='Misc'"
            );

            // Guard against missing fallback tag

            if (miscTag == null)
                throw new Exception("Fallback tag 'Misc' is missing from Tags table.");

            // Replace current index
            RuleIndex = index;
            MiscTagId = miscTag.Value;
        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // CRITICAL FAILURE
            // ------------------------------------------------------------
            // If RuleBook fails to load, the TagEngine cannot work.
            // So we log the error and rethrow to stop application startup.
            // ------------------------------------------------------------
            Console.WriteLine($"[RuleBook] Failed to load rules: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // REFRESH RULEBOOK
    // ------------------------------------------------------------
    // Called when user modifies rules in UI.
    // This simply reloads the rule index from database.
    // -------------------------------------------------------------
    public void Refresh()
    {
        try
        {
            Load();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RuleBook] Failed to refresh rules: {ex.Message}");
            throw;
        }
    }
}