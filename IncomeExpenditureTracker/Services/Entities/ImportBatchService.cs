using System;
using Dapper;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<ImportBatchService> _logger;

    public ImportBatchService(IDatabaseService database, ILogger<ImportBatchService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    public async Task<int> CreateBatch(
        string fileName,
        string source,
        int? accountId = null,
        IDbConnection? conn = null,
        IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name cannot be empty.", nameof(fileName));
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // SQL query inserts a new batch record and returns the generated ID[cite: 2].
                // Included AccountId to match the DatabaseInitializer relational schema[cite: 2].
                const string sql = @"
                    INSERT INTO ImportBatches (FileName, Source, ImportDate, AccountId)
                    VALUES (@FileName, @Source, @ImportDate, @AccountId);

                    SELECT last_insert_rowid();";

                // Executed as <long> to prevent Dapper InvalidCastExceptions with SQLite 64-bit rowids
                var batchId = await connection.ExecuteScalarAsync<long>(sql, new
                {
                    FileName = fileName,
                    Source = source,
                    ImportDate = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    AccountId = accountId
                }, transaction: transaction);

                _logger.LogInformation("Created new ImportBatch ID {BatchId} for file '{FileName}' (Source: '{Source}').", batchId, fileName, source);

                return (int)batchId;
            }, conn, tx);
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
            _logger.LogError(ex, "Failed to create import batch record for file '{FileName}'.", fileName);
            throw;
        }
    }

    /// <summary>
    /// Routes queries through the resilient ExecuteWithRetryAsync wrapper unless an active
    /// connection and transaction are passed from a parent orchestrator[cite: 1].
    /// </summary>
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            // Execute directly within the parent transaction boundary (e.g., StatementImportService)[cite: 1]
            return await action(existingConn, existingTx);
        }

        // Execute as a standalone, retry-protected UI operation[cite: 1]
        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
    }
}