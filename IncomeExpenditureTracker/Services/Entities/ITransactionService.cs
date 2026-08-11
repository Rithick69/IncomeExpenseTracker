using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// ITransactionService
// ------------------------------------------------------------
// Interface for TransactionService to define the contract for database operations related to transactions.
// This allows for better separation of concerns and makes it easier to mock the service for testing.
// ------------------------------------------------------------

public interface ITransactionService
{
    Task InsertTransactionsAsync(
        List<Transaction> transactions,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    Task DeleteByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    // =========================================================================
    // BATCH UPDATES & ORCHESTRATION SUPPORT
    // =========================================================================

    /// <summary>
    /// Executes a high-speed Dapper bulk update to apply UI corrections.
    /// (Tags, Dates, Amounts, Entities). Clears the NeedsReview flag automatically.
    /// </summary>
    Task UpdateTransactionsBulkAsync(
        IEnumerable<TransactionCorrectionDTO> corrections,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    /// <summary>
    /// Re-parents all transactions associated with a deleted Tag to a fallback Tag.
    /// </summary>
    Task ReassignTransactionsToFallbackTagAsync(
        int oldTagId,
        int fallbackTagId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    /// <summary>
    /// Retrieves transactions based on optional filters: BatchId, AccountId, and SearchText.
    /// Supports optional SQL limit and offset for UI grid pagination.
    /// Executes via B-tree indexes for performance.
    /// </summary>
    Task<List<Transaction>> GetFilteredTransactionsAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    /// <summary>
    /// Retrieves the count of transactions based on optional filters: BatchId, AccountId, and SearchText.
    /// Executes via B-tree indexes for performance.
    /// </summary>
    Task<int> GetFilteredTransactionCountAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);
}