using System;
using Dapper;
using System.Linq;
using System.Data;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Logging;

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

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _accountIdCache = new(StringComparer.OrdinalIgnoreCase);

    public AccountService(IDatabaseService database, ILogger<AccountService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            // TRANSACTION ROLLBACK PROTECTION
            // -------------------------------------------------------------------------
            // If an explicit transaction (tx) is passed, we bypass saving newly generated IDs
            // back to the global RAM cache. This prevents orphaned IDs from polluting memory
            // if the parent batch import fails and rolls back.
            // -------------------------------------------------------------------------
            if (tx != null)
            {
                if (_accountIdCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted)
                {
                    return await existingLazy.Value;
                }

                return await ExecuteUpsertInternalAsync(account, conn, tx);
            }

            // Standard lock-free stampede protection for autocommit execution
            var lazyId = _accountIdCache.GetOrAdd(cacheKey, key =>
                new Lazy<Task<int>>(() => ExecuteUpsertInternalAsync(account, conn, tx)));

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
    public async Task<List<Account>> GetAllAccounts(IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Corrected Bug: Changed 'ORDER BY AccountName' (non-existent column) to valid schema fields
                const string sql = "SELECT * FROM Accounts ORDER BY EntityName ASC, AccountNumber ASC;";
                var accounts = await connection.QueryAsync<Account>(sql, transaction: transaction);
                return accounts.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError($"[AccountService] Failed to fetch account details: {ex.Message}");
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