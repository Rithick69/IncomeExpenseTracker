using System;
using Dapper;
using System.Linq;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
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
public class TransactionService : ITransactionService, IDisposable
{
    private readonly IDatabaseService _database;

    private readonly ILogger<TransactionService> _logger;

    private readonly IApplicationBroker _broker;

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Transaction>>>> _transactionsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _transactionCountCache = new(StringComparer.OrdinalIgnoreCase);

    public TransactionService(IDatabaseService database, ILogger<TransactionService> logger, IApplicationBroker broker)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));

        // -------------------------------------------------------------------------
        // ARCHITECTURAL GUARDRAIL: CACHE ANNIHILATION
        // -------------------------------------------------------------------------
        // When the database swaps, we MUST wipe the ConcurrentDictionary
        // to prevent Profile A's data from appearing in Profile B's UI.
        // -------------------------------------------------------------------------
        _broker.Register<ProfileSwappedMessage>(this, (message) => InvalidateCache());
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
                    Date, AccountId, Description, Source,
                    Credit, Debit, TransactionType, ImportBatchId,
                    TagId, PayeeId, TransactionHash, CreatedDate,
                    ReviewStatus, RawAmountText
                )
                VALUES
                (
                    @Date, @AccountId, @Description, @Source,
                    @Credit, @Debit, @TransactionType, @ImportBatchId,
                    @TagId, @PayeeId, @TransactionHash, @CreatedDate,
                    @ReviewStatus, @RawAmountText
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
            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute bulk transaction insert for {Count} records.", transactions.Count);
            throw;
        }
    }

    /// <summary>
    /// Retrieves a paginated list of transactions based on UI filters.
    /// Utilizes dynamic SQL building to ensure SQLite leverages B-Tree indexes
    /// (e.g., idx_transactions_accountid) instead of performing full table scans.
    /// </summary>
    public async Task<List<Transaction>> GetFilteredTransactionsAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var cacheKey = GetCacheKey(args);

        try
        {
            // 1. Transaction Safety: Bypass cache completely
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction) =>
                    await BuildAndExecuteFilterQueryAsync(connection, transaction, args), conn, tx);
            }

            // 2. Cache Stampede Protection
            var cachedLazy = _transactionsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Transaction>>>(async () =>
            {
                try
                {
                    return await ExecuteDbActionAsync(async (connection, transaction) =>
                        await BuildAndExecuteFilterQueryAsync(connection, transaction, args), null, null);
                }
                catch
                {
                    // 3. Fault Eviction
                    _transactionsCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch filtered transactions.");
            throw; // Bubble-Up Principle: Let the ViewModel catch and display the error
        }
    }

    /// <summary>
    /// Retrieves the total count of transactions matching the UI filters for pagination logic.
    /// Strips out ORDER BY and LIMIT/OFFSET for maximum execution speed.
    /// </summary>
    public async Task<int> GetFilteredTransactionCountAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        var cacheKey = GetCacheKey(args) + "_COUNT";

        try
        {
            // 1. Transaction Safety: Bypass cache completely
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction) =>
                    await BuildAndExecuteFilterCountQueryAsync(connection, transaction, args), conn, tx);
            }

            // 2. Cache Stampede Protection
            var cachedLazy = _transactionCountCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    return await ExecuteDbActionAsync(async (connection, transaction) =>
                        await BuildAndExecuteFilterCountQueryAsync(connection, transaction, args), null, null);
                }
                catch
                {
                    // 3. Fault Eviction
                    _transactionCountCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch filtered transaction count.");
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

            InvalidateCache();
            _logger.LogInformation("Deleted all transaction records for Batch ID {BatchId}.", batchId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TransactionService] Failed to delete batch transactions");
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // DASHBOARD RETRIEVAL IMPLEMENTATIONS
    // -------------------------------------------------------------------------

    public async Task UpdateTransactionsBulkAsync(IEnumerable<TransactionCorrectionDTO> corrections, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            // Dapper automatically iterates over the IEnumerable when passed to ExecuteAsync.
            const string sql = @"
                UPDATE Transactions
                SET TagId = @TargetTagId,
                    PayeeId = @PayeeId,
                    Date = @Date,
                    Source = @Source,
                    Debit = @Debit,
                    Credit = @Credit,
                    ReviewStatus = @ReviewStatus
                WHERE Id = @TransactionId;";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, corrections, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute bulk transaction update for {Count} records.", corrections.Count());
            throw;
        }
    }

    public async Task ReassignTransactionsToFallbackTagAsync(int oldTagId, int fallbackTagId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = @"
                UPDATE Transactions
                SET TagId = @FallbackTagId
                WHERE TagId = @OldTagId;";

            if (conn != null)
            {
                await conn.ExecuteAsync(sql, new { OldTagId = oldTagId, FallbackTagId = fallbackTagId }, transaction: tx);
            }
            else
            {
                await _database.ExecuteWithRetryAsync(async (c) =>
                {
                    await c.ExecuteAsync(sql, new { OldTagId = oldTagId, FallbackTagId = fallbackTagId });
                });
            }

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reassign transactions from Tag ID {OldTagId} to fallback Tag ID {FallbackTagId}.", oldTagId, fallbackTagId);
            throw;
        }
    }

    public async Task ExecuteRetroactiveSweepAsync(string source, int tagId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source cannot be empty.", nameof(source));

        const string sql = @"
        UPDATE Transactions
        SET TagId = @TagId,
            ReviewStatus = (ReviewStatus & ~8)
        WHERE Source = @Source AND TagId IS NULL;";

        try
        {
            _logger.LogDebug("Executing retroactive sweep for Source '{Source}' to TagId {TagId}.", source, tagId);
            if (conn != null && tx != null)
            {
                await conn.ExecuteAsync(sql, new { Source = source, TagId = tagId }, transaction: tx);
            }
            else
            {
                await _database.ExecuteWithRetryAsync(async (c) =>
                {
                    await c.ExecuteAsync(sql, new { Source = source, TagId = tagId });
                });
            }

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute retroactive sweep for Source '{Source}' to TagId {TagId}.", source, tagId);
            throw;
        }
    }

    private async Task<T> ExecuteDbActionAsync<T>(Func<IDbConnection, IDbTransaction?, Task<T>> action, IDbConnection? existingConn, IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            return await action(existingConn, existingTx);
        }

        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
    }


    private static string GetCacheKey(TransactionFilterArgs args)
    {
        return $"FILTER:{args.BatchId}_{args.AccountId}_{args.Source}_{args.SearchText}_{args.IsTriageMode}_{(int?)args.SpecificError}_{args.Limit}_{args.Offset}";
    }


    private void InvalidateCache()
    {
        _transactionsCache.Clear();
        _transactionCountCache.Clear();
        _logger.LogInformation("Evicted TransactionService RAM cache due to data mutation.");
    }

    private (List<string>, DynamicParameters) generateFilterParameters(TransactionFilterArgs args)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (args.BatchId.HasValue)
        {
            conditions.Add("ImportBatchId = @BatchId");
            parameters.Add("@BatchId", args.BatchId.Value);
        }

        if (args.AccountId.HasValue)
        {
            conditions.Add("AccountId = @AccountId");
            parameters.Add("@AccountId", args.AccountId.Value);
        }

        if (!string.IsNullOrWhiteSpace(args.Source))
        {
            // Exact match for the B-Tree index (idx_transactions_source)
            conditions.Add("Source = @Source");
            parameters.Add("@Source", args.Source);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            conditions.Add("Description LIKE @SearchText");
            parameters.Add("@SearchText", $"%{args.SearchText}%");
        }

        // 1. Specific Error Filter (e.g., UI dropdown selects "Missing Payee")
        if (args.SpecificError.HasValue && args.SpecificError.Value != ReviewFlags.None)
        {
            // We use a bitwise AND (&) operation instead of standard equality (ReviewStatus = @ErrorFlag).
            // Because errors are stacked using [Flags], a row might hold multiple errors simultaneously
            // (e.g., InvalidAmount [1] + MissingPayee [4] = ReviewStatus of 5).
            // The bitwise AND evaluates if the specific error bit is flipped ON inside the compound integer
            // (e.g., 5 & 4 = 4), ensuring we find the row regardless of what other errors are stacked on it.
            conditions.Add("(ReviewStatus & @ErrorFlag) = @ErrorFlag");
            parameters.Add("@ErrorFlag", (int)args.SpecificError.Value);
        }
        // 2. General Triage Mode (UI toggle "Show All Errors")
        else if (args.IsTriageMode.HasValue && args.IsTriageMode.Value)
        {
            conditions.Add("ReviewStatus > 0");
        }

        return (conditions, parameters);
    }

    // Helper method to keep the DB logic clean and reusable
    private async Task<int> BuildAndExecuteFilterCountQueryAsync(IDbConnection connection, IDbTransaction? transaction, TransactionFilterArgs args)
    {
        var (conditions, parameters) = generateFilterParameters(args);

        var sql = "SELECT COUNT(1) FROM Transactions";
        if (conditions.Any())
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        return await connection.ExecuteScalarAsync<int>(
            sql,
            parameters,
            transaction: transaction);
    }

    // Helper method to keep the DB logic clean and reusable
    private async Task<List<Transaction>> BuildAndExecuteFilterQueryAsync(IDbConnection connection, IDbTransaction? transaction, TransactionFilterArgs args)
    {
        var (conditions, parameters) = generateFilterParameters(args);

        var sql = "SELECT * FROM Transactions";
        if (conditions.Any())
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        sql += " ORDER BY Date DESC";

        if (args.Limit.HasValue)
        {
            sql += " LIMIT @Limit";
            parameters.Add("@Limit", args.Limit.Value);

            if (args.Offset.HasValue)
            {
                sql += " OFFSET @Offset";
                parameters.Add("@Offset", args.Offset.Value);
            }
        }

        var transactions = await connection.QueryAsync<Transaction>(sql, parameters, transaction: transaction);
        return transactions.ToList();
    }

    public void Dispose()
    {
        _broker.Unregister<ProfileSwappedMessage>(this);
        GC.SuppressFinalize(this);
    }
}