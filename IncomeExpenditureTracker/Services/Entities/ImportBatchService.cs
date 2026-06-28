using System;
using Dapper;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// IMPORT BATCH SERVICE
// ------------------------------------------------------------
// This service is responsible for tracking Excel imports.
//
// Each imported statement file creates one ImportBatch record.
// All transactions imported from that file will reference
// this batch using ImportBatchId.
//
// This allows:
//
// • Grouping transactions by import file
// • Filtering transactions in the UI
// • Deleting a full import if needed
// • Preventing duplicate imports later
//
// Example:
//
// ImportBatches table:
//
// Id | FileName          | Source       | ImportDate
// ------------------------------------------------------
// 1  | sbi_jan.xlsx      | SBI Savings  | 2026-03-08
//
// Transactions:
//
// Id | Date | Description | Debit | ImportBatchId
// ------------------------------------------------------
// 1  | ...  | AMAZON      | 500   | 1
// 2  | ...  | SWIGGY      | 300   | 1
// ------------------------------------------------------------
public class ImportBatchService : IImportBatchService
{
    private readonly IDatabaseService _database;

    public ImportBatchService(IDatabaseService database)
    {
        _database = database;
    }

    // ------------------------------------------------------------
    // CREATE IMPORT BATCH
    // ------------------------------------------------------------
    // Creates a new ImportBatch record when a user imports
    // an Excel statement.
    //
    // Parameters:
    // fileName → Name of imported Excel file
    // source   → Bank / account name
    //
    // Returns:
    // ImportBatchId (primary key)
    // ------------------------------------------------------------
    public async Task<int> CreateBatch(string fileName, string source)
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // SQL query inserts a new batch record and returns
                // the auto-generated ID using SQLite's last_insert_rowid()
                var sql = @"
                    INSERT INTO ImportBatches (FileName, Source, ImportDate)
                    VALUES (@FileName, @Source, @ImportDate);

                    SELECT last_insert_rowid();
                ";

                var batchId = await connection.ExecuteScalarAsync<int>(sql, new
                {
                    FileName = fileName,
                    Source = source,
                    ImportDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                });

                return batchId;
            });
        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // ERROR HANDLING
            // ------------------------------------------------------------
            // If batch creation fails, the import process should stop
            // because transactions would not have a valid ImportBatchId.
            //
            // We log the error and rethrow it so the calling service
            // (StatementImportService) can handle it.
            // ------------------------------------------------------------
            Console.WriteLine($"[ImportBatchService] Failed to create import batch: {ex.Message}");
            throw;
        }
    }
}