using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Messaging;

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

    private readonly ILogger<TransactionReviewOrchestrator> _logger;
    private readonly IApplicationBroker _broker;

    public TransactionReviewOrchestrator(
        IDatabaseService database,
        ITransactionService transactionService,
        IImportBatchService importBatchService,
        ITagService tagService,
        ILogger<TransactionReviewOrchestrator> logger,
        IApplicationBroker broker)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _importBatchService = importBatchService ?? throw new ArgumentNullException(nameof(importBatchService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public async Task<PagedResult<Transaction>> GetTransactionsAsync(TransactionFilterArgs args, CancellationToken ct = default)
    {
        try
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
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Transaction", "Get", $"Could not fetch transactions."));
            throw;
        }
    }

    public async Task ApplyCorrectionsAsync(IReadOnlyCollection<TransactionCorrectionDTO> corrections, CancellationToken ct = default)
    {
        if (corrections == null || !corrections.Any()) return;
        try
        {
            // 1. Execute the Batch Update Atomically
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
            {
                await _transactionService.UpdateTransactionsBulkAsync(corrections, conn, tx);
            });

            _logger.LogInformation("Successfully applied {Count} bulk transaction corrections.", corrections.Count);
            _broker.Send(new BatchUpdateCompletedMessage(corrections.Count));

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
                            _logger.LogWarning(ex, "Background learning failed for Tx {TransactionId}.", correction.TransactionId);
                        }
                    }
                }
            }, CancellationToken.None); // Use CancellationToken.None so learning completes even if the UI cancels the request
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply bulk transaction corrections.");
            _broker.Send(new CrudErrorMessage("Transactions", "Bulk Update", "Failed to apply batch corrections."));
            throw;
        }
    }

    public async Task RevertImportBatchAsync(int batchId, CancellationToken ct = default)
    {
        try
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
            _logger.LogInformation("Successfully reverted and deleted Import Batch ID {BatchId}.", batchId);
            _broker.Send(new EntityDeletedMessage("Import Batch", $"Batch #{batchId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revert Import Batch ID {BatchId}.", batchId);
            _broker.Send(new CrudErrorMessage("Import Batch", "Revert", $"Could not revert batch #{batchId}."));
            throw;
        }
    }
}