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
public class SynonymService : ISynonymService
{
    private readonly IDatabaseService _database;

    public SynonymService(IDatabaseService database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
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
    public async Task<IEnumerable<Synonyms>> GetAllSynonyms()
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

    /// <summary>
    /// Learns a new mapping by inserting a new record with a strictly higher priority
    /// than any previous mappings for the same raw synonym.
    /// </summary>
    public async Task LearnFromCorrectionAsync(string rawSynonym, string fieldType)
    {
        try
        {
            // 1. Find the current highest priority for this specific raw synonym
            const string maxPrioritySql = "SELECT MAX(Priority) FROM Synonyms WHERE Synonym = @Synonym;";

            var currentMaxPriority = await _database.ExecuteWithRetryAsync(async connection =>
            {
                return await connection.QuerySingleOrDefaultAsync<int?>(maxPrioritySql, new { Synonym = rawSynonym });
            });

            // 2. Increment the priority so this new rule beats all older rules.
            int newPriority = (currentMaxPriority ?? 0) + 1;

            // 3. Create the new historical record
            var newSynonym = new Synonyms
            {
                FieldType = fieldType,
                Synonym = rawSynonym,
                Priority = newPriority
            };

            // 4. Save the new mapping
            await AddSynonymAsync(newSynonym);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to learn from correction: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Inserts a new synonym record. Because we rely on Priority to resolve duplicates,
    /// this is a standard INSERT, appending to the history.
    /// </summary>
    public async Task AddSynonymAsync(Synonyms synonym)
    {
        try
        {
            const string sql = @"
            INSERT INTO Synonyms (FieldType, Synonym, Priority)
            VALUES (@FieldType, @Synonym, @Priority);";

            await _database.ExecuteWithRetryAsync(async connection =>
            {
                await connection.ExecuteAsync(sql, synonym);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to add synonym: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Updates an existing synonym record. Used by the manual management UI
    /// to correct typos or change mappings for a specific entry.
    /// </summary>
    public async Task UpdateSynonymAsync(Synonyms synonym)
    {
        try
        {
            const string sql = @"
            UPDATE Synonyms
            SET FieldType = @FieldType,
                Synonym = @Synonym,
                Priority = @Priority
            WHERE Id = @Id;";

            await _database.ExecuteWithRetryAsync(async connection =>
            {
                await connection.ExecuteAsync(sql, synonym);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to update synonym: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Removes a specific synonym record by its primary key, utilized by the manual management UI.
    /// </summary>
    public async Task DeleteSynonymAsync(int id)
    {
        try
        {
            const string sql = "DELETE FROM Synonyms WHERE Id = @Id;";

            await _database.ExecuteWithRetryAsync(async connection =>
            {
                await connection.ExecuteAsync(sql, new { Id = id });
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to delete synonym: {ex.Message}");
            throw;
        }
    }
}