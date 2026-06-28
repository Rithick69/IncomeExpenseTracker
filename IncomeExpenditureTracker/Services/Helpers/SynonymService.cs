using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Helpers;



// ------------------------------------------------------------
// SYNONYM SERVICE
// ------------------------------------------------------------
// This service loads column synonyms used by the Excel importer.
//
// Why this exists:
// Different banks export Excel statements with different
// column names.
//
// Example:
//
// SBI:
// "Txn Date", "Description", "Debit", "Credit"
//
// HDFC:
// "Date", "Narration", "Withdrawal", "Deposit"
//
// ICICI:
// "Transaction Date", "Remarks", "Debit", "Credit"
//
// Instead of hardcoding these variations in the importer,
// we store them in the database and load them dynamically.
//
// This allows:
// - Supporting new banks easily
// - Supporting different languages
// - Letting users customize column detection
//
// Example database row:
//
// ColumnType   Synonym
// DATE         TXN DATE
// DESCRIPTION  NARRATION
// DEBIT        WITHDRAWAL
// CREDIT       DEPOSIT
// ------------------------------------------------------------
public class SynonymService : ISynonymnService
{
    private readonly IDatabaseService _database;

    public SynonymService(IDatabaseService database)
    {
        _database = database;
    }

    // ------------------------------------------------------------
    // GET ALL COLUMN SYNONYMS
    // ------------------------------------------------------------
    // Loads all synonyms from the database.
    //
    // This is used by the FieldMapper during Excel import
    // to automatically detect which columns represent:
    //
    // Date
    // Description
    // Debit
    // Credit
    //
    // Returns:
    // List<Synonyms>
    // ------------------------------------------------------------
    public async Task<List<Synonyms>> GetAllSynonyms()
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async connection =>
            {
                var synonyms = await connection.QueryAsync<Synonyms>(
                    "SELECT * FROM Synonyms"
                );

                return synonyms.ToList();
            });
        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // ERROR HANDLING
            // ------------------------------------------------------------
            // If loading synonyms fails, the importer cannot detect
            // Excel columns correctly.
            //
            // We log the error and rethrow so the calling service
            // can stop the import process safely.
            // ------------------------------------------------------------
            Console.WriteLine($"[SynonymService] Failed to load synonyms: {ex.Message}");
            throw;
        }
    }
}