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
// • Query transactions (with running balance)
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
    public async Task InsertTransactionsAsync(
        List<Transaction> transactions,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (transactions == null || transactions.Count == 0)
            return;

        try
        {
            var now = DateTime.UtcNow;
            foreach (var txn in transactions)
            {
                if (txn.CreatedDate == default)
                {
                    txn.CreatedDate = now;
                }
            }

            const string sql = @"
                INSERT INTO Transactions
                (
                    Date, AccountId, Description, Source,
                    Credit, Debit, ImportBatchId,
                    TagId, PayeeId, TransactionHash, CreatedDate,
                    ReviewStatus, RawAmountText, Memo, ParentTransactionId, LinkedTransactionId
                )
                VALUES
                (
                    @Date, @AccountId, @Description, @Source,
                    @Credit, @Debit, @ImportBatchId,
                    @TagId, @PayeeId, @TransactionHash, @CreatedDate,
                    @ReviewStatus, @RawAmountText, @Memo, @ParentTransactionId, @LinkedTransactionId
                );";

            if (conn != null && tx != null)
            {
                var cmd = new CommandDefinition(
                    sql,
                    transactions,
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(cmd);
                _logger.LogDebug("Bulk inserted {Count} transactions within parent transaction boundary.", transactions.Count);
            }
            else
            {
                await _database.ExecuteInTransactionWithRetryAsync(async (connection, transaction, cancelToken) =>
                {
                    var cmd = new CommandDefinition(
                        sql,
                        transactions,
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );
                    await connection.ExecuteAsync(cmd);
                }, ct);
                _logger.LogInformation("Successfully completed standalone bulk insert of {Count} transactions.", transactions.Count);
            }
            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute bulk transaction insert for {Count} records.", transactions.Count);
            throw;
        }
    }

    /// <summary>
    /// Retrieves a paginated list of transactions based on UI filters.
    /// Utilizes dynamic SQL building and a correlated subquery to dynamically
    /// calculate the RunningBalance for each row without fetching the entire table.
    /// </summary>
    public async Task<List<Transaction>> GetFilteredTransactionsAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(args);
        var cacheKey = GetCacheKey(args);

        try
        {
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                    await BuildAndExecuteFilterQueryAsync(connection, transaction, args, cancelToken), conn, tx, ct);
            }

            var cachedLazy = _transactionsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Transaction>>>(async () =>
            {
                try
                {
                    return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                        await BuildAndExecuteFilterQueryAsync(connection, transaction, args, cancelToken), null, null, ct);
                }
                catch
                {
                    _transactionsCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch filtered transactions.");
            throw;
        }
    }

    /// <summary>
    /// Retrieves the total count of transactions matching the UI filters for pagination logic.
    /// Strips out ORDER BY and LIMIT/OFFSET for maximum execution speed.
    /// </summary>
    public async Task<int> GetFilteredTransactionCountAsync(
        TransactionFilterArgs args,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(args);
        var cacheKey = GetCacheKey(args) + "_COUNT";

        try
        {
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                    await BuildAndExecuteFilterCountQueryAsync(connection, transaction, args, cancelToken), conn, tx, ct);
            }

            var cachedLazy = _transactionCountCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                        await BuildAndExecuteFilterCountQueryAsync(connection, transaction, args, cancelToken), null, null, ct);
                }
                catch
                {
                    _transactionCountCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
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
    public async Task DeleteByBatchIdAsync(
        int batchId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                const string sql = "DELETE FROM Transactions WHERE ImportBatchId = @BatchId;";
                var cmd = new CommandDefinition(
                    sql,
                    new { BatchId = batchId },
                    transaction: transaction,
                    cancellationToken: cancelToken
                );
                await connection.ExecuteAsync(cmd);
                return true;
            }, conn, tx, ct);

            InvalidateCache();
            _logger.LogInformation("Deleted all transaction records for Batch ID {BatchId}.", batchId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TransactionService] Failed to delete batch transactions");
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // BULK UPDATE / CORRECTIONS (PHASE 9)
    // -------------------------------------------------------------------------

    public async Task UpdateTransactionsBulkAsync(
        IEnumerable<TransactionCorrectionDTO> corrections,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            const string sql = @"
                UPDATE Transactions
                SET TagId = @TargetTagId,
                    PayeeId = @PayeeId,
                    Date = @Date,
                    Debit = @Debit,
                    Credit = @Credit,
                    ReviewStatus = @ReviewStatus,
                    Memo = @Memo
                WHERE Id = @TransactionId;";

            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                var cmd = new CommandDefinition(
                    sql,
                    corrections,
                    transaction: transaction,
                    cancellationToken: cancelToken
                );
                await connection.ExecuteAsync(cmd);
                return true;
            }, conn, tx, ct);

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute bulk transaction update for {Count} records.", corrections.Count());
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // PHASE 9: SPLITS & TRANSFERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Splits a single transaction into multiple child transactions atomically.
    /// Enforces a strict math guardrail to ensure the split amounts exactly match the parent.
    /// </summary>
    public async Task SplitTransactionAsync(
        int originalId,
        List<Transaction> children,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (children == null || children.Count == 0) return;

        await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
        {
            // 1. Fetch the parent to validate math
            var parent = await connection.QuerySingleOrDefaultAsync<Transaction>(
                "SELECT Debit, Credit, AccountId, ImportBatchId, Date, Source, TransactionHash, CreatedDate FROM Transactions WHERE Id = @Id;",
                new { Id = originalId }, transaction);

            if (parent == null) throw new InvalidOperationException($"Parent transaction {originalId} not found.");

            // 2. Math Guardrail
            var totalChildDebit = children.Sum(c => c.Debit);
            var totalChildCredit = children.Sum(c => c.Credit);

            if (parent.Debit != totalChildDebit || parent.Credit != totalChildCredit)
            {
                throw new InvalidOperationException("Split math mismatch. The sum of the children must exactly equal the parent's Debit/Credit.");
            }

            // 3. Flag the parent as a split (Assuming 16 is ReviewFlags.IsSplitParent)
            await connection.ExecuteAsync(
                "UPDATE Transactions SET ReviewStatus = (ReviewStatus | 16) WHERE Id = @Id;",
                new { Id = originalId }, transaction);

            // 4. Prepare children to inherit critical parent data
            var now = DateTime.UtcNow;
            foreach (var child in children)
            {
                child.ParentTransactionId = originalId;
                child.AccountId = parent.AccountId;
                child.ImportBatchId = parent.ImportBatchId;
                child.Date = parent.Date;
                child.Source = parent.Source;
                child.TransactionHash = $"{parent.TransactionHash}_split_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
                child.CreatedDate = now;
            }

            // 5. Insert children
            const string insertSql = @"
                INSERT INTO Transactions (
                    Date, AccountId, Description, Source, Credit, Debit, ImportBatchId,
                    TagId, PayeeId, TransactionHash, CreatedDate, ReviewStatus, RawAmountText, Memo, ParentTransactionId
                ) VALUES (
                    @Date, @AccountId, @Description, @Source, @Credit, @Debit, @ImportBatchId,
                    @TagId, @PayeeId, @TransactionHash, @CreatedDate, @ReviewStatus, @RawAmountText, @Memo, @ParentTransactionId
                );";

            await connection.ExecuteAsync(insertSql, children, transaction);
            return true;
        }, conn, tx, ct);

        InvalidateCache();
    }

    /// <summary>
    /// Links a Debit transaction to a Credit transaction to form a transfer,
    /// applying a fallback Transfer tag and mapping their LinkedTransactionId.
    /// </summary>
    public async Task LinkTransferAsync(
        int debitTransactionId,
        int creditTransactionId,
        int systemTransferTagId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
        {
            var debitTx = await connection.QuerySingleOrDefaultAsync<Transaction>(
                "SELECT Debit FROM Transactions WHERE Id = @Id;", new { Id = debitTransactionId }, transaction);
            var creditTx = await connection.QuerySingleOrDefaultAsync<Transaction>(
                "SELECT Credit FROM Transactions WHERE Id = @Id;", new { Id = creditTransactionId }, transaction);

            if (debitTx == null || creditTx == null) throw new InvalidOperationException("One or both transactions not found.");

            // Math validation: Debit must exactly match Credit
            if (debitTx.Debit != creditTx.Credit)
            {
                throw new InvalidOperationException($"Transfer mismatch: Debit ({debitTx.Debit}) does not match Credit ({creditTx.Credit}).");
            }

            const string updateSql = @"
                UPDATE Transactions
                SET LinkedTransactionId = @LinkedId, TagId = @TagId
                WHERE Id = @TargetId;";

            // Update Debit row to point to Credit row
            await connection.ExecuteAsync(updateSql, new { LinkedId = creditTransactionId, TagId = systemTransferTagId, TargetId = debitTransactionId }, transaction);

            // Update Credit row to point to Debit row
            await connection.ExecuteAsync(updateSql, new { LinkedId = debitTransactionId, TagId = systemTransferTagId, TargetId = creditTransactionId }, transaction);

            return true;
        }, conn, tx, ct);

        InvalidateCache();
    }

    public async Task ReassignTransactionsToFallbackTagAsync(
        int oldTagId,
        int fallbackTagId,
        IDbConnection? conn = null,
        IDbTransaction? tx = null,
        CancellationToken ct = default)
    {
        try
        {
            const string sql = @"
                UPDATE Transactions
                SET TagId = @FallbackTagId
                WHERE TagId = @OldTagId;";

            if (conn != null)
            {
                var cmd = new CommandDefinition(
                    sql,
                    new { OldTagId = oldTagId, FallbackTagId = fallbackTagId },
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(cmd);
            }
            else
            {
                await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
                {
                    var cmd = new CommandDefinition(
                        sql,
                        new { OldTagId = oldTagId, FallbackTagId = fallbackTagId },
                        cancellationToken: cancelToken
                    );
                    await c.ExecuteAsync(cmd);
                }, ct);
            }

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reassign transactions from Tag ID {OldTagId} to fallback Tag ID {FallbackTagId}.", oldTagId, fallbackTagId);
            throw;
        }
    }

    public async Task ExecuteRetroactiveSweepAsync(string source, int tagId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
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
                var cmd = new CommandDefinition(
                    sql,
                    new { Source = source, TagId = tagId },
                    transaction: tx,
                    cancellationToken: ct
                );
                await conn.ExecuteAsync(cmd);
            }
            else
            {
                await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
                {
                    var cmd = new CommandDefinition(
                        sql,
                        new { Source = source, TagId = tagId },
                        cancellationToken: cancelToken
                    );
                    await c.ExecuteAsync(cmd);
                }, ct);
            }

            InvalidateCache();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute retroactive sweep for Source '{Source}' to TagId {TagId}.", source, tagId);
            throw;
        }
    }

    // -------------------------------------------------------------------------
    // PRIVATE HELPER METHODS
    // -------------------------------------------------------------------------

    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, CancellationToken, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx,
        CancellationToken ct)
    {
        if (existingConn != null)
        {
            return await action(existingConn, existingTx, ct);
        }

        return await _database.ExecuteWithRetryAsync(async (connection, cancelToken) => await action(connection, null, cancelToken), ct);
    }

    private static string GetCacheKey(TransactionFilterArgs args)
    {
        return $"FILTER:{args.BatchId}_{args.AccountId}_{args.Source}_{args.SearchText}_{args.IsTriageMode}_{(int?)args.SpecificError}_{args.DateFrom:yyyyMMdd}_{args.DateTo:yyyyMMdd}_{args.Limit}_{args.Offset}";
    }

    private void InvalidateCache()
    {
        _transactionsCache.Clear();
        _transactionCountCache.Clear();
        _logger.LogInformation("Evicted TransactionService RAM cache due to data mutation.");
    }

    private (List<string>, DynamicParameters) GenerateFilterParameters(TransactionFilterArgs args)
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
            conditions.Add("Source = @Source");
            parameters.Add("@Source", args.Source);
        }

        if (!string.IsNullOrWhiteSpace(args.SearchText))
        {
            conditions.Add("Description LIKE @SearchText");
            parameters.Add("@SearchText", $"%{args.SearchText}%");
        }

        // Bitwise evaluation for specific triage banners
        if (args.SpecificError.HasValue && args.SpecificError.Value != ReviewFlags.None)
        {
            conditions.Add("(ReviewStatus & @ErrorFlag) = @ErrorFlag");
            parameters.Add("@ErrorFlag", (int)args.SpecificError.Value);
        }
        else if (args.IsTriageMode.HasValue && args.IsTriageMode.Value)
        {
            conditions.Add("ReviewStatus > 0");
        }

        // Date Range Filters
        if (args.DateFrom.HasValue)
        {
            conditions.Add("Date >= @DateFrom");
            parameters.Add("@DateFrom", args.DateFrom.Value.ToString("yyyy-MM-dd"));
        }

        if (args.DateTo.HasValue)
        {
            conditions.Add("Date <= @DateTo");
            parameters.Add("@DateTo", args.DateTo.Value.ToString("yyyy-MM-dd"));
        }

        return (conditions, parameters);
    }

    private async Task<int> BuildAndExecuteFilterCountQueryAsync(IDbConnection connection, IDbTransaction? transaction, TransactionFilterArgs args, CancellationToken ct)
    {
        var (conditions, parameters) = GenerateFilterParameters(args);

        var sql = "SELECT COUNT(1) FROM Transactions";
        if (conditions.Any())
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        var cmd = new CommandDefinition(
            sql,
            parameters,
            transaction: transaction,
            cancellationToken: ct
        );

        return await connection.ExecuteScalarAsync<int>(cmd);
    }

    private async Task<List<Transaction>> BuildAndExecuteFilterQueryAsync(IDbConnection connection, IDbTransaction? transaction, TransactionFilterArgs args, CancellationToken ct)
    {
        var (conditions, parameters) = GenerateFilterParameters(args);

        // Core query pulling transaction fields and the dynamic running balance using a correlated subquery
        var sql = @"
            SELECT
                t.*,
                (
                    SELECT SUM(sub.Credit - sub.Debit)
                    FROM Transactions sub
                    WHERE sub.AccountId = t.AccountId
                      AND (sub.Date < t.Date OR (sub.Date = t.Date AND sub.Id <= t.Id))
                ) AS RunningBalance
            FROM Transactions t";

        if (conditions.Any())
        {
            sql += " WHERE " + string.Join(" AND ", conditions);
        }

        sql += " ORDER BY t.Date DESC, t.Id DESC";

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

        var cmd = new CommandDefinition(
            sql,
            parameters,
            transaction: transaction,
            cancellationToken: ct
        );

        var transactions = await connection.QueryAsync<Transaction>(cmd);
        return transactions.ToList();
    }

    public void Dispose()
    {
        _broker.Unregister<ProfileSwappedMessage>(this);
        GC.SuppressFinalize(this);
    }
}