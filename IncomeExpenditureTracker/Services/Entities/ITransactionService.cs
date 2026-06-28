using IncomeExpenditureTracker.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// ITransactionService
// ------------------------------------------------------------
// Interface for TransactionService to define the contract for database operations related to transactions.
// This allows for better separation of concerns and makes it easier to mock the service for testing.
// ------------------------------------------------------------

public interface ITransactionService
{
    public Task InsertTransactions(List<Transaction> transactions);
    public Task<List<Transaction>> GetByBatchId(int batchId);
    // Task<List<Transaction>> GetByBatchIdAsync(int batchId);

    public Task DeleteByBatchId(int batchId);
}