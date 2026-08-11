using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Tagging;

namespace IncomeExpenditureTracker.Services.Orchestration;

/// <summary>
/// Coordinates post-persistence transaction review, batch updates, and background learning.
/// Acts as the single source of truth for the Avalonia Data Grid.
/// </summary>
public class TransactionReviewOrchestrator : ITransactionReviewOrchestrator
{
    private readonly IDatabaseService _database;
    private readonly ITransactionService _transactionService;
    // Assuming an interface exists for managing the ImportBatches table
    private readonly IImportBatchService _importBatchService;
    private readonly ITagService _tagService;

    public TransactionReviewOrchestrator(
        IDatabaseService database,
        ITransactionService transactionService,
        IImportBatchService importBatchService,
        ITagService tagService)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _importBatchService = importBatchService ?? throw new ArgumentNullException(nameof(importBatchService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
    }

    public async Task<PagedResult<Transaction>> GetTransactionsAsync(TransactionFilterArgs args, CancellationToken ct = default)
    {
        // Delegates to the TransactionService which utilizes B-Tree indexes and SQLite LIMIT/OFFSET.
        // Assuming your TransactionService has been updated to accept these filter arguments.
        var items = await _transactionService.GetFilteredTransactionsAsync(
            args,
            conn: null,
            tx: null);

        // A separate fast count query for the UI pagination controls
        int totalCount = await _transactionService.GetFilteredTransactionCountAsync(
            args,
            conn: null,
            tx: null);

        return new PagedResult<Transaction>
        {
            Items = items,
            TotalCount = totalCount
        };
    }

    public async Task ApplyCorrectionsAsync(IReadOnlyCollection<TransactionCorrectionDTO> corrections, CancellationToken ct = default)
    {
        if (corrections == null || !corrections.Any()) return;

        // 1. Execute the Batch Update Atomically
        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            await _transactionService.UpdateTransactionsBulkAsync(corrections, conn, tx);
        });

        // 2. Dispatch Background Learning (The Ripple Effect)
        // We use Task.Run so the method returns immediately, freeing the UI thread.
        // We pass the data into the closure to avoid evaluating an disposed enumerator.
        var learningPayload = corrections.ToList();

        _ = Task.Run(async () =>
        {
            foreach (var correction in learningPayload)
            {
                // Only learn if the user actually assigned a tag
                if (correction.TargetTagId.HasValue)
                {
                    try
                    {
                        await _tagService.LearnRuleFromOverrideAsync(correction.RawDescription, correction.TargetTagId.Value);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Background learning failed for Tx {correction.TransactionId}: {ex.Message}");
                    }
                }
            }
        }, CancellationToken.None); // Use CancellationToken.None so learning completes even if the UI cancels the request
    }

    public async Task RevertImportBatchAsync(int batchId, CancellationToken ct = default)
    {
        // 100% All-or-Nothing Revert
        // If the transaction wipe succeeds but the batch record wipe fails, it all rolls back.
        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            // First, delete all child transactions tied to this batch
            await _transactionService.DeleteByBatchIdAsync(batchId, conn, tx);

            // Second, delete the master batch record
            await _importBatchService.DeleteBatchAsync(batchId, conn, tx);
        });
    }
}