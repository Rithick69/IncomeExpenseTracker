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
public class AccountService : IAccountService
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
        _broker = broker;

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
    public async Task<int> GetOrCreateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null)
    {

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
                if (_accountIdCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted)
                {
                    return await existingLazy.Value;
                }

                // Cache MISS inside a transaction: Execute directly, DO NOT cache the result.
                // Bypass the retry wrapper entirely, as transactions cannot be retried mid-flight.
                return await ExecuteUpsertInternalAsync(account, conn, tx);
            }

            // -------------------------------------------------------------------------
            // STANDALONE EXECUTION (Safe for caching and retries)
            // -------------------------------------------------------------------------
            var lazyId = _accountIdCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    // Execute using the retry policy wrapper
                    var id = await _database.ExecuteWithRetryAsync(retryConn =>
                        ExecuteUpsertInternalAsync(account, retryConn, null));

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

    public async Task<List<Account>> GetAllAccounts()
    {
        const string cacheKey = "ALL_ACCOUNTS";
        try
        {
            // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
            var cachedLazy = _accountListCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Account>>>(async () =>
            {
                try
                {
                    // Execute using the standard retry wrapper (no external conn/tx needed)
                    return await _database.ExecuteWithRetryAsync(async c =>
                    {
                        const string sql = "SELECT * FROM Accounts ORDER BY EntityName ASC, AccountNumber ASC";
                        var entities = await c.QueryAsync<Account>(sql);
                        return entities.ToList();
                    });
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
        catch (Exception ex)
        {
            _logger.LogError($"[AccountService] Failed to fetch account details: {ex.Message}");
            throw;
        }
    }

    public async Task<List<Account>> GetAccountsByEntityId(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        var cacheKey = $"ENTITY_ACCOUNTS_{entityId}";

        try
        {
            // 1. Transaction Safety: Bypass cache completely if part of an active transaction
            // We do not want to read stale cached data, nor do we want to cache uncommitted data.
            if (conn != null && tx != null)
            {
                return await ExecuteDbActionAsync(async (connection, transaction) =>
                {
                    const string sql = "SELECT * FROM Accounts WHERE EntityId = @EntityId ORDER BY AccountNumber ASC;";
                    var accounts = await connection.QueryAsync<Account>(sql, new { EntityId = entityId }, transaction: transaction);
                    return accounts.ToList();
                }, conn, tx);
            }

            // 2. Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the factory method
            var cachedLazy = _entityAccountsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Account>>>(async () =>
            {
                try
                {
                    // Only one thread will ever run this block per cache miss for this specific EntityId
                    return await ExecuteDbActionAsync(async (connection, transaction) =>
                    {
                        const string sql = "SELECT * FROM Accounts WHERE EntityId = @EntityId ORDER BY AccountNumber ASC;";
                        var accounts = await connection.QueryAsync<Account>(sql, new { EntityId = entityId }, transaction: transaction);
                        return accounts.ToList();
                    }, null, null);
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
    public async Task UpdateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
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

            await ExecuteDbActionAsync(async (connection, transaction) =>
             {
                 await connection.ExecuteAsync(sql, account, transaction: transaction);
                 return true;
             }, conn, tx);

            InvalidateCache();
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
    public async Task DeleteAccount(int accountId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Checked BOTH ImportBatches and Transactions to prevent foreign key violations
                const string checkSql = @"
                    SELECT
                        (SELECT COUNT(*) FROM ImportBatches WHERE AccountId = @AccountId) +
                        (SELECT COUNT(*) FROM Transactions WHERE AccountId = @AccountId);";

                var usageCount = await connection.ExecuteScalarAsync<int>(checkSql, new { AccountId = accountId }, transaction: transaction);

                if (usageCount > 0)
                {
                    throw new InvalidOperationException("Cannot delete account because existing imports or transactions reference it.");
                }

                const string deleteSql = "DELETE FROM Accounts WHERE Id = @AccountId;";
                await connection.ExecuteAsync(deleteSql, new { AccountId = accountId }, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete account ID {Id}.", accountId);
            throw;
        }
    }

    public async Task<bool> HasTransactionsAsync(int accountId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            // High-speed lookup relying on idx_transactions_accountid
            const string sql = "SELECT 1 FROM Transactions WHERE AccountId = @AccountId LIMIT 1;";

            if (conn != null)
            {
                return await conn.ExecuteScalarAsync<bool>(sql, new { AccountId = accountId }, transaction: tx);
            }

            return await _database.ExecuteWithRetryAsync(async (c) =>
            {
                return await c.ExecuteScalarAsync<bool>(sql, new { AccountId = accountId });
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check transactions for account ID {Id}.", accountId);
            throw;
        }
    }

    public async Task ReassignAccountsAsync(int oldEntityId, int targetEntityId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = @"
                UPDATE Accounts
                SET EntityId = @targetEntityId
                WHERE EntityId = @OldEntityId;";

            if (conn != null)
            {
                await conn.ExecuteAsync(sql, new { OldEntityId = oldEntityId, targetEntityId }, transaction: tx);
            }
            else
            {
                await _database.ExecuteWithRetryAsync(async (c) =>
                {
                    await c.ExecuteAsync(sql, new { OldEntityId = oldEntityId, targetEntityId });
                });
            }
            InvalidateCache();
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
    private async Task<int> ExecuteUpsertInternalAsync(Account account, IDbConnection? conn, IDbTransaction? tx)
    {
        return await ExecuteDbActionAsync(async (connection, transaction) =>
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

            var id = await connection.ExecuteScalarAsync<long>(sql, account, transaction: transaction);
            _logger.LogDebug("Resolved Account '{CacheKey}' to ID {Id}.", GetCacheKey(account), id);
            return (int)id;
        }, conn, tx);
    }

    /// <summary>
    /// Unified execution helper. Routes queries through the resilient ExecuteWithRetryAsync wrapper
    /// unless an active connection and transaction are passed from a parent orchestrator.
    /// </summary>
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            // Execute directly within the parent transaction boundary (e.g., StatementImportService)
            return await action(existingConn, existingTx);
        }

        // Execute as a standalone, retry-protected UI operation
        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
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
}