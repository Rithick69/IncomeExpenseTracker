using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Orchestration;

/// <summary>
/// Coordinates post-persistence transaction review, batch updates, and background learning.
/// Acts as the single source of truth for the Avalonia Grid.
/// </summary>
public interface ITransactionReviewOrchestrator
{
    /// <summary>
    /// Retrieves a paginated slice of transactions without holding the full table in memory.
    /// </summary>
    Task<PagedResult<Transaction>> GetTransactionsAsync(TransactionFilterArgs args, CancellationToken ct = default);

    /// <summary>
    /// Executes an atomic Dapper bulk UPDATE for user corrections.
    /// Triggers asynchronous tag/synonym learning in a background thread upon commit.
    /// </summary>
    Task ApplyCorrectionsAsync(IReadOnlyCollection<TransactionCorrectionDTO> corrections, CancellationToken ct = default);

    /// <summary>
    /// Atomically wipes all transactions associated with an ImportBatchId and removes the batch record.
    /// </summary>
    Task RevertImportBatchAsync(int batchId, CancellationToken ct = default);
}