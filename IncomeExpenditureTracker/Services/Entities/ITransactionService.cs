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

    Task<List<Transaction>> GetByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    Task DeleteByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    // -------------------------------------------------------------------------
    // STATELESS DASHBOARD RETRIEVAL METHODS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Retrieves historical transactions ordered by date descending .
    /// Supports optional SQL limit and offset for UI grid pagination.
    /// </summary>
    Task<List<Transaction>> GetAllTransactionsAsync(
        int? limit = null,
        int? offset = null,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    /// <summary>
    /// Retrieves all transactions linked to a specific bank account or credit card [source: 2, 6].
    /// Executes via B-tree index idx_transactions_accountid.
    /// </summary>
    Task<List<Transaction>> GetByAccountIdAsync(
        int accountId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);

    /// <summary>
    /// Retrieves all transactions linked to a specific financial institution or merchant entity [source: 2, 6].
    /// Executes via B-tree index idx_transactions_entity.
    /// </summary>
    Task<List<Transaction>> GetByEntityNameAsync(
        string entityName,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);
}