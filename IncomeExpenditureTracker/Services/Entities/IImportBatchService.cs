// interface IImportBatchService.cs
// This file defines the IImportBatchService interface, which provides a method for creating an import batch.
// The ImportBatchService class implements this interface to track Excel imports by creating ImportBatch records in the database.
// This allows grouping transactions by import file, filtering transactions in the UI, deleting a full import if needed, and preventing duplicate imports later.
using System.Data;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Entities;

public interface IImportBatchService
{
    Task<int> CreateBatch(
        string fileName,
        string source,
        int? accountId = null,
        IDbConnection? conn = null,
        IDbTransaction? tx = null);
}