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

    public async Task SeedDefaultFieldTypesAsync(IEnumerable<string> standardFieldTypes, string category)
    {
        await _database.ExecuteWithRetryAsync(async connection =>
        {
            // 1. Get all distinct field types currently in the DB
            var existingTypesQuery = "SELECT DISTINCT FieldType FROM Synonyms WHERE Category = @Category;";
            var existingTypes = (await connection.QueryAsync<string>(existingTypesQuery, new { Category = category }))
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 2. Find which standard types are missing from the DB
            var missingTypes = standardFieldTypes.Where(t => !existingTypes.Contains(t));

            // 3. Insert a default baseline record for each missing field type
            // This ensures the table is populated even without custom synonyms
            var insertQuery = @"
            INSERT INTO Synonyms (FieldType, Synonym, Priority, Category)
            VALUES (@FieldType, @Synonym, @Priority, @Category);";

            foreach (var fieldType in missingTypes)
            {
                await connection.ExecuteAsync(insertQuery, new
                {
                    FieldType = fieldType.ToUpperInvariant(),
                    Synonym = fieldType, // Self-referencing default (e.g., "Date" -> "Date")
                    Priority = 1,        // Baseline priority
                    Category = category.ToUpperInvariant()
                });
            }

            return true; // ExecuteWithRetryAsync expects a return value
        });
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

    public async Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategory(string category)
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async connection =>
            {
                // 1. Query SQLite for synonyms in this specific category, ordered by Priority descending.
                // Ordering by Priority DESC is the secret to automatic conflict resolution!
                const string sql = @"
                    SELECT Synonym, FieldType, Priority, Category
                    FROM Synonyms
                    WHERE Category = @Category
                    ORDER BY Priority DESC;";

                var rows = await connection.QueryAsync<Synonyms>(sql, new { Category = category });

                // 2. Build an O(1) case-insensitive dictionary for the FieldMapper to consume
                var synonymMap = new Dictionary<string, Synonyms>(StringComparer.OrdinalIgnoreCase);

                // Build an O(1) case-insensitive dictionary mapping Normalized Synonym -> Full Entity.
                // GroupBy + First() automatically resolves conflicts by claiming the highest Priority.

                synonymMap = rows
                    .GroupBy(s => Normalize(s.Synonym)) // Normalize to handle variations like "Txn Date" vs "TXN DATE"
                    .ToDictionary(s => s.Key, s => s.First(), StringComparer.OrdinalIgnoreCase); // Take the highest priority mapping for each synonym

                return synonymMap.AsReadOnly();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to load synonyms by category: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Learns a new mapping by inserting a new record with a strictly higher priority
    /// than any previous mappings for the same raw synonym.
    /// </summary>
    public async Task LearnFromCorrectionAsync(string rawSynonym, string fieldType, string category)
    {
        try
        {
            // 1. Find the current highest priority for this specific raw synonym
            const string maxPrioritySql = "SELECT MAX(Priority) FROM Synonyms WHERE Synonym = @Synonym AND Category = @Category;";

            var currentMaxPriority = await _database.ExecuteWithRetryAsync(async connection =>
            {
                return await connection.QuerySingleOrDefaultAsync<int?>(maxPrioritySql, new { Synonym = rawSynonym, Category = category });
            });

            // 2. Increment the priority so this new rule beats all older rules.
            int newPriority = (currentMaxPriority ?? 0) + 1;

            var rawFieldType = fieldType.Split(':')[1].Trim(); // Extract the actual field type from the namespaced key (e.g., "Col:Date" -> "Date")

            // 3. Create the new historical record
            var newSynonym = new Synonyms
            {
                FieldType = rawFieldType,
                Synonym = rawSynonym,
                Priority = newPriority,
                Category = category.ToUpperInvariant()
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
            INSERT INTO Synonyms (FieldType, Synonym, Priority, Category)
            VALUES (@FieldType, @Synonym, @Priority, @Category);";

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
                Priority = @Priority,
                Category = @Category
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

    private static string Normalize(string text)
    {
        return text
            .ToUpper()
            .Replace("_", " ")
            .Replace("-", " ")
            .Trim();
    }
}