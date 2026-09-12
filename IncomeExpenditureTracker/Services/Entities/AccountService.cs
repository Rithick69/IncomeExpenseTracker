using System;
using Dapper;
using System.Linq;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// ACCOUNT SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for Accounts.
//
// Accounts represent bank accounts or credit cards and are used
// for dashboard grouping and analytics.
//
// Responsibilities:
// • Find or create account during statement import
// • Update account metadata
// • Delete account
// • Retrieve accounts for dashboard views
// ------------------------------------------------------------
public class AccountService : IAccountService, IDisposable
{
    private readonly IDatabaseService _database;
    private readonly ILogger<AccountService> _logger;

    private readonly IApplicationBroker _broker;

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _accountIdCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Account>>>> _entityAccountsCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Account>>>> _accountListCache = new(StringComparer.OrdinalIgnoreCase);

    public AccountService(IDatabaseService database, ILogger<AccountService> logger, IApplicationBroker broker)
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
    // FIND OR CREATE ACCOUNT
    // ------------------------------------------------------------
    // Used during statement import.
    // If the account exists, return its Id.
    // Otherwise create a new record.
    // ------------------------------------------------------------
    public async Task<int> GetOrCreateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (account == null)
            throw new ArgumentNullException(nameof(account));

        var cacheKey = GetCacheKey(account);
        if (string.IsNullOrEmpty(cacheKey))
            throw new ArgumentException("Account must have either a valid AccountNumber or CardNumber.");

        try
        {
            // -------------------------------------------------------------------------
            // TRANSACTION ROLLBACK PROTECTION GUARDRAIL
            // -------------------------------------------------------------------------
            if (conn != null && tx != null)
            {
                // Read from cache if it exists (safe reference data reuse)
                if (_accountIdCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted && !existingLazy.Value.IsCanceled)
                {
                    return await existingLazy.Value;
                }

                // Cache MISS inside a transaction: Execute directly, DO NOT cache the result.
                // Bypass the retry wrapper entirely, as transactions cannot be retried mid-flight.
                return await ExecuteUpsertInternalAsync(account, conn, tx, ct);
            }

            // -------------------------------------------------------------------------
            // STANDALONE EXECUTION (Safe for caching and retries)
            // -------------------------------------------------------------------------
            var lazyId = _accountIdCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    // Execute using the retry policy wrapper
                    var id = await _database.ExecuteWithRetryAsync((retryConn, cancelToken) =>
                        ExecuteUpsertInternalAsync(account, retryConn, null, cancelToken), ct);

                    // ONLY clear the list caches on a cache miss when we actually hit the database
                    _accountListCache.Clear();
                    _entityAccountsCache.Clear();

                    return id;
                }
                catch
                {
                    // Fault Eviction: Remove from cache inside the factory if DB fails
                    _accountIdCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the task (concurrent requests will await this same task)
            return await lazyId.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fault Eviction: Remove poisoned keys so subsequent requests can retry cleanly
            _logger.LogError(ex, "Failed to resolve or create account for key '{CacheKey}'. Evicting cache key.", cacheKey);
            _accountIdCache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL ACCOUNTS
    // ------------------------------------------------------------
    // Used by dashboard and account selection UI.
    // ------------------------------------------------------------

    public async Task<List<Account>> GetAllAccounts(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const string cacheKey = "ALL_ACCOUNTS";
        try
        {
            // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
            var cachedLazy = _accountListCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Account>>>(async () =>
            {
                try
                {
                    // Execute using the standard retry wrapper (no external conn/tx needed)
                    return await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
                    {
                        const string sql = "SELECT * FROM Accounts ORDER BY EntityName ASC, AccountNumber ASC";
                        var cmd = new CommandDefinition(sql, cancellationToken: cancelToken);
                        var entities = await c.QueryAsync<Account>(cmd);
                        return entities.ToList();
                    }, ct);
                }
                catch
                {
                    // Fault Eviction: Remove the broken task from cache if the DB fails
                    _accountListCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the Lazy task. All concurrent threads will await this same exact task instance.
            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[AccountService] Failed to fetch account details: {ex.Message}");
            throw;
        }
    }

    public async Task<List<Account>> GetAccountsByEntityId(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var cacheKey = $"ENTITY_ACCOUNTS_{entityId}";

        try
        {
            // 1. Transaction Safety: Bypass cache completely if part of an active transaction
            // We do not want to read stale cached data, nor do we want to cache uncommitted data.
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                {
                    const string sql = "SELECT * FROM Accounts WHERE EntityId = @EntityId ORDER BY AccountNumber ASC;";
                    var cmd = new CommandDefinition(sql, new { EntityId = entityId }, transaction: transaction, cancellationToken: cancelToken);
                    var accounts = await connection.QueryAsync<Account>(cmd);
                    return accounts.ToList();
                }, conn, tx, ct);
            }

            // 2. Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the factory method
            var cachedLazy = _entityAccountsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Account>>>(async () =>
            {
                try
                {
                    // Only one thread will ever run this block per cache miss for this specific EntityId
                    return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                    {
                        const string sql = "SELECT * FROM Accounts WHERE EntityId = @EntityId ORDER BY AccountNumber ASC;";
                        var cmd = new CommandDefinition(sql, new { EntityId = entityId }, transaction: transaction, cancellationToken: cancelToken);
                        var accounts = await connection.QueryAsync<Account>(cmd);
                        return accounts.ToList();
                    }, null, null, ct);
                }
                catch
                {
                    // 3. Fault Eviction: Remove the broken task from cache if the DB fails
                    _entityAccountsCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the Lazy task. All concurrent threads asking for this EntityId will await this same task.
            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch accounts for Entity ID {EntityId}.", entityId);
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE ACCOUNT
    // ------------------------------------------------------------
    // Updates account metadata such as name or bank.
    // ------------------------------------------------------------
    public async Task UpdateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (account == null || account.Id <= 0)
            throw new ArgumentException("Valid account instance with a primary key is required for update.");
        try
        {
            // typeof(Account) looks at the "blueprint" of the Account class itself.
            // .GetProperties() returns a list of all the public properties defined in that class (e.g., AccountNumber, Currency, EntityName, etc.).

            var properties = typeof(Account)
                .GetProperties()
                .Where(p => p.Name != nameof(Account.Id) && p.Name != nameof(Account.CreatedDate));

            var updates = new List<string>();

            foreach (var prop in properties)
            {

                // Get the value of the property for the given account instance.
                var value = prop.GetValue(account);

                // Only include properties that have a non-null value to allow for partial updates.

                if (value != null)
                {
                    // If the property has a value, we add it to the list of updates in the format "PropertyName = @PropertyName".
                    updates.Add($"{prop.Name} = @{prop.Name}");
                }
            }
            // If there are no properties to update, we can skip the database call.
            if (!updates.Any())
                return;

            var sql = $@"
                UPDATE Accounts
                SET {string.Join(", ", updates)}
                WHERE Id = @Id
            ";

            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                var cmd = new CommandDefinition(sql, account, transaction: transaction, cancellationToken: cancelToken);
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
            _logger.LogError(ex, "Failed to update account ID {Id}.", account?.Id);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE ACCOUNT
    // ------------------------------------------------------------
    // Removes an account from the system.
    //
    // IMPORTANT:
    // Should only be allowed if no transactions reference it.
    // Otherwise the deletion may violate foreign key constraints.
    // ------------------------------------------------------------
    public async Task DeleteAccount(int accountId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                // Checked BOTH ImportBatches and Transactions to prevent foreign key violations
                const string checkSql = @"
                    SELECT
                        (SELECT COUNT(*) FROM ImportBatches WHERE AccountId = @AccountId) +
                        (SELECT COUNT(*) FROM Transactions WHERE AccountId = @AccountId);";

                var checkCmd = new CommandDefinition(
                    checkSql,
                    new { AccountId = accountId },
                    transaction: transaction,
                    cancellationToken: cancelToken);

                var usageCount = await connection.ExecuteScalarAsync<int>(checkCmd);

                if (usageCount > 0)
                {
                    throw new InvalidOperationException("Cannot delete account because existing imports or transactions reference it.");
                }

                // const string deleteSql = "DELETE FROM Accounts WHERE Id = @AccountId;";
                // await connection.ExecuteAsync(deleteSql, new { AccountId = accountId }, transaction: transaction);

                var deleteCmd = new CommandDefinition(
                    @"DELETE FROM Accounts WHERE Id = @AccountId;",
                    new { AccountId = accountId },
                    transaction: transaction,
                    cancellationToken: cancelToken);

                await connection.ExecuteAsync(deleteCmd);
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
            _logger.LogError(ex, "Failed to delete account ID {Id}.", accountId);
            throw;
        }
    }

    public async Task<bool> HasTransactionsAsync(int accountId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // High-speed lookup relying on idx_transactions_accountid
            const string sql = "SELECT 1 FROM Transactions WHERE AccountId = @AccountId LIMIT 1;";

            if (conn != null)
            {
                var checkcmd = new CommandDefinition(
                    sql,
                    new { AccountId = accountId },
                    transaction: tx,
                    cancellationToken: ct
                );
                return await conn.ExecuteScalarAsync<bool>(checkcmd);
            }

            return await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
            {
                var checkcmd = new CommandDefinition(
                    sql,
                    new { AccountId = accountId },
                    cancellationToken: cancelToken
                );
                return await c.ExecuteScalarAsync<bool>(checkcmd);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check transactions for account ID {Id}.", accountId);
            throw;
        }
    }

    public async Task ReassignAccountsAsync(int oldEntityId, int targetEntityId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            const string sql = @"
                UPDATE Accounts
                SET EntityId = @targetEntityId
                WHERE EntityId = @OldEntityId;";

            if (conn != null)
            {
                var cmd = new CommandDefinition(
                    sql,
                    new { OldEntityId = oldEntityId, targetEntityId },
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
                    new { OldEntityId = oldEntityId, targetEntityId },
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
            _logger.LogError(ex, "Failed to reassign accounts from Entity ID {OldEntityId} to {TargetEntityId}.", oldEntityId, targetEntityId);
            throw;
        }
    }

    /// <summary>
    /// Executes an atomic SQLite upsert. Eliminates read-then-write race conditions by attempting
    /// an INSERT OR IGNORE and immediately querying the canonical Id in a single execution block.
    /// </summary>
    private async Task<int> ExecuteUpsertInternalAsync(Account account, IDbConnection? conn, IDbTransaction? tx, CancellationToken ct)
    {
        return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
        {
            // -------------------------------------------------------------------------
            // ATOMIC UPSERT SQL (CORRECTED FROM ENTITIES COPY-PASTE)
            // -------------------------------------------------------------------------
            // 1. INSERT OR IGNORE attempts creation without failing if AccountNumber/CardNumber exists.
            // 2. SELECT Id immediately resolves the primary key whether newly created or pre-existing.
            // -------------------------------------------------------------------------
            const string sql = @"
                INSERT OR IGNORE INTO Accounts
                (
                    AccountNumber, CardNumber, EntityId, EntityName,
                    AccountType, Currency, CreatedDate, CreditLimit
                )
                VALUES
                (
                    @AccountNumber, @CardNumber, @EntityId, @EntityName,
                    @AccountType, @Currency, @CreatedDate, @CreditLimit
                );

                SELECT Id FROM Accounts
                WHERE (AccountNumber IS NOT NULL AND AccountNumber = @AccountNumber)
                   OR (CardNumber IS NOT NULL AND CardNumber = @CardNumber)
                LIMIT 1;";

            if (account.CreatedDate == default)
            {
                account.CreatedDate = DateTime.UtcNow;
            }

            var cmd = new CommandDefinition(sql, account, transaction: transaction, cancellationToken: cancelToken);

            var id = await connection.ExecuteScalarAsync<long>(cmd);
            _logger.LogDebug("Resolved Account '{CacheKey}' to ID {Id}.", GetCacheKey(account), id);
            return (int)id;
        }, conn, tx, ct);
    }

    /// <summary>
    /// Unified execution helper. Routes queries through the resilient ExecuteWithRetryAsync wrapper
    /// unless an active connection and transaction are passed from a parent orchestrator.
    /// </summary>
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, CancellationToken, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx,
        CancellationToken ct)
    {
        if (existingConn != null)
        {
            // Execute directly within the parent transaction boundary (e.g., StatementImportService)
            return await action(existingConn, existingTx, ct);
        }

        // Execute as a standalone, retry-protected UI operation
        return await _database.ExecuteWithRetryAsync(async (connection, cancelToken) => await action(connection, null, cancelToken), ct);
    }

    private void InvalidateCache()
    {
        _accountIdCache.Clear();
        _entityAccountsCache.Clear();
        _accountListCache.Clear();
        _logger.LogInformation("Evicted AccountService RAM cache due to data mutation.");
    }

    /// <summary>
    /// Generates a standardized, case-insensitive composite cache key based on AccountNumber and CardNumber.
    /// </summary>
    private static string GetCacheKey(Account account)
    {
        var acc = account.AccountNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        var card = account.CardNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        return $"ACC:{acc}|CARD:{card}";
    }

    public void Dispose()
    {
        _broker.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}