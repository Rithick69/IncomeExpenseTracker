using System;
using Dapper;
using System.Linq;
using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// TRANSACTION SERVICE
// ------------------------------------------------------------
// Handles database operations related to Transactions.
//
// Why this service exists:
// StatementImportService should only handle the import
// workflow (Excel → Parser → Tagging).
//
// All database operations for transactions are centralized here.
//
// Responsibilities:
// • Batch insert transactions
// • Query transactions
// • Delete transactions by import batch
// ------------------------------------------------------------
public class TransactionService : ITransactionService
{
    private readonly IDatabaseService _database;

    private readonly ILogger<TransactionService> _logger;

    public TransactionService(IDatabaseService database, ILogger<TransactionService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------
    // INSERT TRANSACTIONS (BATCH INSERT)
    // ------------------------------------------------------------
    // Inserts a list of transactions into the database.
    //
    // This uses batch execution via Dapper which is much faster
    // than inserting rows individually.
    //
    // Performance example:
    //
    // 1000 rows
    // Single inserts → ~2 seconds
    // Batch insert   → ~0.1 seconds
    //
    // A database transaction is used to ensure atomicity:
    //
    // If any insert fails → entire batch rolls back.
    // ------------------------------------------------------------
    public async Task InsertTransactionsAsync(
        List<Transaction> transactions,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        if (transactions == null || transactions.Count == 0)
            return;

        try
        {
            var now = DateTime.UtcNow;
            foreach (var txn in transactions)
            {
                // Ensure CreatedDate is set. Transaction.CreatedDate is a DateTime.
                if (txn.CreatedDate == default)
                {
                    txn.CreatedDate = now;
                }
            }

            const string sql = @"
                INSERT INTO Transactions
                (
                    Date, AccountId, Description, Entity,
                    Credit, Debit, TransactionType, ImportBatchId,
                    TagId, TransactionHash, CreatedDate
                )
                VALUES
                (
                    @Date, @AccountId, @Description, @Entity,
                    @Credit, @Debit, @TransactionType, @ImportBatchId,
                    @TagId, @TransactionHash, @CreatedDate
                );";

            if (conn != null && tx != null)
            {
                await conn.ExecuteAsync(sql, transactions, transaction: tx);
                _logger.LogDebug("Bulk inserted {Count} transactions within parent transaction boundary.", transactions.Count);
            }
            else
            {
                await _database.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
                {
                    await connection.ExecuteAsync(sql, transactions, transaction: transaction);
                });
                _logger.LogInformation("Successfully completed standalone bulk insert of {Count} transactions.", transactions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute bulk transaction insert for {Count} records.", transactions.Count);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET TRANSACTIONS BY IMPORT BATCH
    // ------------------------------------------------------------
    // Returns all transactions belonging to a specific import.
    //
    // Useful for:
    // • Viewing imported statement
    // • Debugging import results
    // ------------------------------------------------------------
    public async Task<List<Transaction>> GetByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                const string sql = @"
                    SELECT * FROM Transactions
                    WHERE ImportBatchId = @BatchId
                    ORDER BY Date ASC;";

                var result = await connection.QueryAsync<Transaction>(
                    sql,
                    new { BatchId = batchId },
                    transaction: transaction);

                return result.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch transactions for Batch ID {BatchId}.", batchId);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE TRANSACTIONS BY IMPORT BATCH
    // ------------------------------------------------------------
    // Removes all transactions belonging to a specific import.
    //
    // Useful if:
    // • User imported wrong file
    // • Duplicate import occurred
    // ------------------------------------------------------------
    public async Task DeleteByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                const string sql = "DELETE FROM Transactions WHERE ImportBatchId = @BatchId;";
                await connection.ExecuteAsync(sql, new { BatchId = batchId }, transaction: transaction);
                return true;
            }, conn, tx);

            _logger.LogInformation("Deleted all transaction records for Batch ID {BatchId}.", batchId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransactionService] Failed to delete batch transactions: {ex.Message}");
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // DASHBOARD RETRIEVAL IMPLEMENTATIONS
    // -------------------------------------------------------------------------

    public async Task<List<Transaction>> GetAllTransactionsAsync(
        int? limit = null,
        int? offset = null,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                var sql = "SELECT * FROM Transactions ORDER BY Date DESC";
                if (limit.HasValue)
                {
                    sql += " LIMIT @Limit";
                    if (offset.HasValue)
                    {
                        sql += " OFFSET @Offset";
                    }
                }
                sql += ";";

                var result = await connection.QueryAsync<Transaction>(
                    sql,
                    new { Limit = limit, Offset = offset ?? 0 },
                    transaction: transaction);

                return result.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch paginated transaction history.");
            throw;
        }
    }

    public async Task<List<Transaction>> GetByAccountIdAsync(
        int accountId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                const string sql = @"
                    SELECT * FROM Transactions
                    WHERE AccountId = @AccountId
                    ORDER BY Date DESC;";

                var result = await connection.QueryAsync<Transaction>(
                    sql,
                    new { AccountId = accountId },
                    transaction: transaction);

                return result.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch transactions for Account ID {AccountId}.", accountId);
            throw;
        }
    }

    public async Task<List<Transaction>> GetByEntityNameAsync(
        string entityName,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(entityName))
            return new List<Transaction>();

        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                const string sql = @"
                    SELECT * FROM Transactions
                    WHERE Entity = @EntityName
                    ORDER BY Date DESC;";

                var result = await connection.QueryAsync<Transaction>(
                    sql,
                    new { EntityName = entityName.Trim() },
                    transaction: transaction);

                return result.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch transactions for Entity '{EntityName}'.", entityName);
            throw;
        }
    }

    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            return await action(existingConn, existingTx);
        }

        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
    }
}