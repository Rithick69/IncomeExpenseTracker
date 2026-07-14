using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Tagging;

// -------------------------------------------------------------------------------------------------
// TAG ENGINE
// -------------------------------------------------------------------------------------------------
// Assigns TagIds to transaction records using an in-memory, zero-contention rule snapshot[cite: 1].
//
// Core Features:
// 1. Thread-Local Memory: Eliminates per-row GC allocations during parallel execution.
// 2. Priority -> Match Count Scoring: Resolves multi-keyword category collisions deterministically.
// 3. Ambiguity Guardrail: Ties on Priority AND Match Count fall back to Misc to prevent silent misclassification.
// 4. Row-Level Fault Isolation: A single malformed row never aborts the batch.
// -------------------------------------------------------------------------------------------------
public class TagEngine
{
    private readonly ITagService _tagService;
    private readonly ILogger<TagEngine> _logger;

    public TagEngine(ITagService tagService, ILogger<TagEngine> logger)
    {
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ProcessTransactions(List<Transaction> transactions, List<List<string>> tokenRows)
    {
        // -----------------------------------------------------------------------------------------
        // 1. PRE-FLIGHT VALIDATION
        // -----------------------------------------------------------------------------------------
        if (transactions == null || tokenRows == null)
        {
            _logger.LogError("ProcessTransactions invoked with null transaction or token list.");
            throw new ArgumentNullException("Transactions or tokenRows cannot be null.");
        }

        if (transactions.Count != tokenRows.Count)
        {
            _logger.LogError("Count mismatch: Received {TxnCount} transactions but {TokenCount} token rows.",
                transactions.Count, tokenRows.Count);
            throw new ArgumentException("Transaction count does not match token row count.");
        }

        if (transactions.Count == 0)
        {
            _logger.LogInformation("ProcessTransactions invoked with empty batch. Nothing to process.");
            return;
        }

        _logger.LogInformation("Starting TagEngine processing for {Count} transactions.", transactions.Count);

        // -----------------------------------------------------------------------------------------
        // 2. SNAPSHOT RETRIEVAL
        // -----------------------------------------------------------------------------------------
        // Fetch immutable snapshot from RAM cache (zero SQLite read contention)[cite: 1]
        RuleBookSnapshot snapshot;
        try
        {
            snapshot = await _tagService.GetRuleBookSnapshotAsync();
            if (snapshot.RuleIndex == null || snapshot.RuleIndex.Count == 0)
            {
                _logger.LogWarning("RuleBookSnapshot is empty. All untagged transactions will default to MiscTagId ({MiscId}).",
                    snapshot.MiscTagId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Failed to retrieve RuleBookSnapshot. Aborting TagEngine processing.");
            throw; // Fatal error: Cannot proceed without the rule book
        }

        var ruleIndex = snapshot.RuleIndex;
        var miscTagId = snapshot.MiscTagId;

        if (ruleIndex == null || ruleIndex.Count == 0)
            throw new Exception("RuleBook is empty. Rules must be loaded before tagging.");

        // -----------------------------------------------------------------------------------------
        // 3. PARALLEL ROW EXECUTION
        // -----------------------------------------------------------------------------------------
        try
        {
            // Execute across multiple CPU cores utilizing thread-local dictionary recycling.
            // Each core allocates ONE small dictionary and reuses it for every row assigned to that core.
            Parallel.For(
                0,
                transactions.Count,
                () => new Dictionary<int, (int MaxPriority, int MatchCount)>(8), // Thread-local state factory
                (i, loopState, localScores) =>
                {
                    var transaction = transactions[i];
                    var tokens = tokenRows[i];

                    // -----------------------------------------------------------------------------
                    // ROW-LEVEL FAULT ISOLATION
                    // -----------------------------------------------------------------------------
                    // Traps individual row exceptions so one corrupt string doesn't crash the import.
                    try
                    {
                        // Skip null records or transactions already manually tagged by the user
                        if (transaction == null || transaction.TagId != null || tokens == null || tokens.Count == 0)
                            return localScores;

                        // Reset thread-local dictionary for this row without allocating new heap memory
                        localScores.Clear();

                        // -------------------------------------------------------------------------
                        // STEP A: TOKEN EVALUATION & SCORE AGGREGATION
                        // -------------------------------------------------------------------------
                        // Iterate over all sliding-window tokens generated for this row[cite: 2, 3]
                        for (int t = 0; t < tokens.Count; t++)
                        {
                            var token = tokens[t];
                            if (string.IsNullOrWhiteSpace(token))
                                continue;

                            // O(1) array lookup against pre-sorted priority rules[cite: 1, 3]
                            if (ruleIndex.TryGetValue(token, out var matchingRules))
                            {
                                for (int r = 0; r < matchingRules.Length; r++)
                                {
                                    var rule = matchingRules[r];
                                    if (localScores.TryGetValue(rule.TagId, out var currentScore))
                                    {
                                        // Update tag score: Retain highest priority seen, increment match count
                                        localScores[rule.TagId] = (Math.Max(currentScore.MaxPriority, rule.Priority), currentScore.MatchCount + 1);
                                    }
                                    else
                                    {
                                        localScores[rule.TagId] = (rule.Priority, 1);
                                    }
                                }
                            }
                        }

                        // -------------------------------------------------------------------------
                        // STEP B: SCORING & AMBIGUITY RESOLUTION MATRIX
                        // -------------------------------------------------------------------------
                        int? bestTagId = null;
                        int highestPriority = -1;
                        int highestMatchCount = -1;
                        bool isAmbiguous = false;

                        foreach (var kvp in localScores)
                        {
                            int tagId = kvp.Key;
                            int priority = kvp.Value.MaxPriority;
                            int matchCount = kvp.Value.MatchCount;

                            // Tier 1: Highest Database Priority Wins
                            if (priority > highestPriority)
                            {
                                highestPriority = priority;
                                highestMatchCount = matchCount;
                                bestTagId = tagId;
                                isAmbiguous = false;
                            }
                            else if (priority == highestPriority)
                            {
                                // Tier 2: At equal priority, Most Keyword Matches Wins
                                if (matchCount > highestMatchCount)
                                {
                                    highestMatchCount = matchCount;
                                    bestTagId = tagId;
                                    isAmbiguous = false;
                                }
                                else if (matchCount == highestMatchCount)
                                {
                                    // Tier 3: Tie on Priority AND Match Count -> Ambiguous!
                                    isAmbiguous = true;
                                }
                            }
                        }

                        // -------------------------------------------------------------------------
                        // STEP C: ASSIGNMENT OR MISC FALLBACK
                        // -------------------------------------------------------------------------
                        if (isAmbiguous)
                        {
                            _logger.LogDebug("Row {RowIndex} ('{Desc}') resulted in a tie between multiple tags. Defaulting to MiscTagId.",
                                i, transaction.Description);
                            transaction.TagId = miscTagId;
                        }
                        else if (bestTagId == null)
                        {
                            // No tokens matched any rule in the database
                            transaction.TagId = miscTagId;
                        }
                        else
                        {
                            // Clean win by Priority or Match Count
                            transaction.TagId = bestTagId.Value;
                        }
                    }
                    catch (Exception rowEx)
                    {
                        // Row-level exception trap: Assign Misc and log without breaking Parallel.For
                        _logger.LogWarning(rowEx, "Failed to process tagging for row {RowIndex} ('{Desc}'). Assigning fallback MiscTagId.",
                            i, transaction?.Description ?? "NULL");

                        if (transaction != null)
                            transaction.TagId = miscTagId;
                    }

                    return localScores; // Return recycled dictionary to the CPU core for the next iteration
                },
                localScores => { /* Optional thread-exit cleanup hook */ }
            );

            _logger.LogInformation("Successfully completed TagEngine processing for {Count} transactions.", transactions.Count);
        }
        catch (AggregateException aggEx)
        {
            _logger.LogError(aggEx, "Fatal parallel processing failure occurred during TagEngine execution.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected critical failure during TagEngine execution.");
            throw;
        }
        finally
        {
            // -------------------------------------------------------------------------------------
            // 4. EXPLICIT MEMORY RECLAMATION
            // -------------------------------------------------------------------------------------
            // Clear inner token arrays immediately so the Garbage Collector can reclaim large
            // string payloads without waiting for upstream DI scope teardown.
            _logger.LogDebug("Executing explicit memory cleanup for {Count} token rows.", tokenRows.Count);
            for (int i = 0; i < tokenRows.Count; i++)
            {
                tokenRows[i]?.Clear();
            }
            tokenRows.Clear();
        }
    }
}