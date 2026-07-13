using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

// Interface for AccountService, defining the contract for account-related operations.
// This allows for better separation of concerns and makes it easier to mock the service for testing.
// Responsibilities:
// • Find or create account during statement import
// • Update account metadata
// • Delete account
// • Retrieve accounts for dashboard views

namespace IncomeExpenditureTracker.Services.Entities;

public interface IAccountService
{
    Task<int> GetOrCreateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task<List<Account>> GetAllAccounts(IDbConnection? conn = null, IDbTransaction? tx = null);
    Task UpdateAccount(Account account, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteAccount(int accountId, IDbConnection? conn = null, IDbTransaction? tx = null);
}