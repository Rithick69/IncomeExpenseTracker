using System;
using Dapper;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

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

    public AccountService(IDatabaseService database)
    {
        _database = database;
    }

    // ------------------------------------------------------------
    // FIND OR CREATE ACCOUNT
    // ------------------------------------------------------------
    // Used during statement import.
    // If the account exists, return its Id.
    // Otherwise create a new record.
    // ------------------------------------------------------------
    public async Task<int> GetOrCreateAccount(Account account)
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // Check if account already exists using Async Dapper
                var existing = await connection.QueryFirstOrDefaultAsync<Account>(
                    @"SELECT Id
                      FROM Accounts
                      WHERE (AccountNumber IS NOT NULL AND AccountNumber = @AccountNumber) OR (CardNumber IS NOT NULL AND CardNumber = @CardNumber)",
                    new { account.AccountNumber, account.CardNumber });

                if (existing != null)
                    return existing.Id;

                var sql = @"
                    INSERT INTO Accounts
                    (
                        AccountNumber,
                        CardNumber,
                        EntityId,
                        EntityName,
                        AccountType,
                        Currency,
                        CreatedDate,
                        CreditLimit
                    )
                    VALUES
                    (
                        @AccountNumber,
                        @CardNumber,
                        @EntityId,
                        @EntityName,
                        @AccountType,
                        @Currency,
                        @CreatedDate,
                        @CreditLimit
                    );

                    SELECT last_insert_rowid();
                ";

                return (int)await connection.ExecuteScalarAsync<long>(sql, account);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AccountService] Failed to create/find account: {ex.Message}");
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
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var sql = @"SELECT * FROM Accounts ORDER BY AccountName";

                return (await connection.QueryAsync<Account>(sql)).ToList();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AccountService] Failed to fetch accounts: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE ACCOUNT
    // ------------------------------------------------------------
    // Updates account metadata such as name or bank.
    // ------------------------------------------------------------
    public async Task UpdateAccount(Account account)
    {
        try
        {
            // typeof(Account) looks at the "blueprint" of the Account class itself. 
            // .GetProperties() returns a list of all the public properties defined in that class (e.g., AccountNumber, Currency, EntityName, etc.).

            var properties = typeof(Account)
                .GetProperties()
                .Where(p =>
                    p.Name != "Id"
                    && p.Name != "CreatedDate" // When you run an UPDATE query in SQL,
                                               //  you almost never want to change the primary key (Id) or 
                                               // the timestamp of when the record was originally created (CreatedDate).
                );

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

            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                await connection.ExecuteAsync(sql, account);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AccountService] Failed to update account: {ex.Message}");
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
    public async Task DeleteAccount(int accountId)
    {
        try
        {
            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // Check if account is used in imports
                var usageCount = await connection.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*)
                    FROM ImportBatches
                    WHERE AccountId = @AccountId",
                    new { AccountId = accountId });

                if (usageCount > 0)
                {
                    throw new Exception("Cannot delete account because imports exist for it.");
                }

                var sql = @"DELETE FROM Accounts WHERE Id = @AccountId";

                await connection.ExecuteAsync(sql, new { AccountId = accountId });
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AccountService] Failed to delete account: {ex.Message}");
            throw;
        }
    }
}