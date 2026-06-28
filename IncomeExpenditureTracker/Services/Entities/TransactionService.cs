using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using IncomeExpenditureTracker.Models;
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
// • Query transactions
// • Delete transactions by import batch
// ------------------------------------------------------------
public class TransactionService : ITransactionService
{
    private readonly IDatabaseService _database;

    public TransactionService(IDatabaseService database)
    {
        _database = database;
    }

    // ------------------------------------------------------------
    // INSERT TRANSACTIONS (BATCH INSERT)
    // ------------------------------------------------------------
    // Inserts a list of transactions into the database.
    //
    // This uses batch execution via Dapper which is much faster
    // than inserting rows individually.
    //
    // Performance example:
    //
    // 1000 rows
    // Single inserts → ~2 seconds
    // Batch insert   → ~0.1 seconds
    //
    // A database transaction is used to ensure atomicity:
    //
    // If any insert fails → entire batch rolls back.
    // ------------------------------------------------------------
    public async Task InsertTransactions(List<Transaction> transactions)
    {
        if (transactions == null || transactions.Count == 0)
            return;

        try
        {
            // Example usage in your TransactionService

            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                await using var dbTransaction = await connection.BeginTransactionAsync();

                // Execute your batch inserts here using Dapper or commands

                var sql = @"
                INSERT INTO Transactions
                (
                    Date,
                    Account,
                    Description,
                    Entity,
                    Credit,
                    Debit,
                    TransactionType,
                    ImportBatchId,
                    TagId
                )
                VALUES
                (
                    @Date,
                    @Account,
                    @Description,
                    @Entity,
                    @Credit,
                    @Debit,
                    @TransactionType,
                    @ImportBatchId,
                    @TagId
                );";

                // Dapper will iterate over the collection and
                // execute the insert efficiently.
                await connection.ExecuteAsync(sql, transactions, dbTransaction);

                await dbTransaction.CommitAsync();
            });

        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // ERROR HANDLING
            // ------------------------------------------------------------
            // If batch insertion fails we rollback automatically
            // because the transaction will be disposed without commit.
            //
            // We log the error and rethrow it so the caller
            // (StatementImportService) can stop the import process.
            // ------------------------------------------------------------
            Console.WriteLine($"[TransactionService] Batch insert failed: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET TRANSACTIONS BY IMPORT BATCH
    // ------------------------------------------------------------
    // Returns all transactions belonging to a specific import.
    //
    // Useful for:
    // • Viewing imported statement
    // • Debugging import results
    // ------------------------------------------------------------
    public async Task<List<Transaction>> GetByBatchId(int batchId)
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var sql = @"
                    SELECT *
                    FROM Transactions
                    WHERE ImportBatchId = @BatchId
                    ORDER BY Date
                ";

                var result = await connection.QueryAsync<Transaction>(sql, new { BatchId = batchId });
                return result.ToList();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransactionService] Failed to fetch batch transactions: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE TRANSACTIONS BY IMPORT BATCH
    // ------------------------------------------------------------
    // Removes all transactions belonging to a specific import.
    //
    // Useful if:
    // • User imported wrong file
    // • Duplicate import occurred
    // ------------------------------------------------------------
    public async Task DeleteByBatchId(int batchId)
    {
        try
        {
            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var sql = @"
                    DELETE FROM Transactions
                    WHERE ImportBatchId = @BatchId
                ";

                await connection.ExecuteAsync(sql, new { BatchId = batchId });
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransactionService] Failed to delete batch transactions: {ex.Message}");
            throw;
        }
    }
}