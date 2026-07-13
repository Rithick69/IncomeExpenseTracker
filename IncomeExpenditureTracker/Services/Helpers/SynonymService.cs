using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using System.Collections.Concurrent;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using Microsoft.Extensions.Logging;

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

/// <summary>
/// Thread-safe, self-updating state manager for column synonyms .
/// Implements Immutable Snapshot Swapping, Async Lazy stampede defense, event-driven eviction,
/// and atomic transactional self-learning to eliminate redundant SQLite I/O during concurrent staging .
/// </summary>
public class SynonymService : ISynonymService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<SynonymService> _logger;

    // -------------------------------------------------------------------------
    // ASYNC LAZY STAMPEDE DEFENSE
    // -------------------------------------------------------------------------
    // Stores immutable RAM snapshots keyed by normalized Category ("TRANSACTION" vs "METADATA") .
    // Wrapping the Task in a Lazy ensures that if multiple extraction threads hit an empty cache
    // simultaneously during StageFilesAsync, only 1 thread executes the SQLite query .
    // -------------------------------------------------------------------------
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, Synonyms>>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SynonymService(IDatabaseService database, ILogger<SynonymService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Seeds baseline domain enum field types for a specific category without overwriting existing data .
    /// Executes within an exponential backoff retry loop and invalidates the RAM cache upon completion .
    /// </summary>
    public async Task SeedDefaultFieldTypesAsync(IEnumerable<string> standardFieldTypes, string category)
    {
        var normalizedCategory = category.ToUpperInvariant();

        await _database.ExecuteWithRetryAsync(async connection =>
        {
            // 1. Get all distinct field types currently in the DB
            var existingTypesQuery = "SELECT DISTINCT FieldType FROM Synonyms WHERE Category = @Category;";
            var existingTypes = (await connection.QueryAsync<string>(existingTypesQuery, new { Category = normalizedCategory }))
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
                    Category = normalizedCategory
                });
            }

            // Evict cached snapshot to ensure subsequent extraction tasks see the new baseline seeds
            InvalidateCache(normalizedCategory);
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
            _logger.LogError(ex, "Failed to load all synonyms from database.");
            Console.WriteLine($"[SynonymService] Failed to load synonyms: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Serves category-scoped synonyms from an immutable in-memory RAM snapshot in O(1) time .
    /// Eliminates SQLite disk I/O during high-volume parsing and concurrent workbook staging .
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategory(string category)
    {
        var normalizedCategory = category.ToUpperInvariant();

        try
        {
            return await _database.ExecuteWithRetryAsync(async connection =>
            {
                // GetOrAdd guarantees exact-once execution of the async factory during cache misses
                var lazySnapshot = _cache.GetOrAdd(normalizedCategory, key =>
                    new Lazy<Task<IReadOnlyDictionary<string, Synonyms>>>(() => LoadSynonymsFromDbAsync(key)));

                return await lazySnapshot.Value;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to load synonyms by category: {ex.Message}");
            _logger.LogError(ex, "Failed to retrieve synonym snapshot for category '{Category}'. Evicting faulted cache key.", normalizedCategory);
            _cache.TryRemove(normalizedCategory, out _);
            throw;
        }
    }

    /// <summary>
    /// Internal factory method that queries SQLite and builds the deduplicated O(1) lookup dictionary .
    /// </summary>
    private async Task<IReadOnlyDictionary<string, Synonyms>> LoadSynonymsFromDbAsync(string normalizedCategory)
    {
        _logger.LogInformation("Cache miss for category '{Category}'. Querying SQLite to build RAM snapshot...", normalizedCategory);

        return await _database.ExecuteWithRetryAsync(async connection =>
        {
            // Ordering by Priority DESC is the mathematical foundation for automatic duplicate conflict resolution
            const string sql = @"
                SELECT Synonym, FieldType, Priority, Category
                FROM Synonyms
                WHERE Category = @Category
                ORDER BY Priority DESC;";

            var rows = await connection.QueryAsync<Synonyms>(sql, new { Category = normalizedCategory });

            // Build an O(1) case-insensitive dictionary mapping Normalized Synonym -> Full Entity.
            // GroupBy + First() automatically resolves conflicts by claiming the highest Priority.

            var synonymMap = rows
                .GroupBy(s => Normalize(s.Synonym))
                .ToDictionary(s => s.Key, s => s.First(), StringComparer.OrdinalIgnoreCase);

            _logger.LogDebug("Successfully built in-memory snapshot for '{Category}' containing {Count} mappings.", normalizedCategory, synonymMap.Count);
            return synonymMap.AsReadOnly();
        });
    }

    /// <summary>
    /// Learns a new mapping by inserting a new record with a strictly higher priority
    /// than any previous mappings for the same raw synonym.
    /// Wrapped in an explicit SQLite transaction to guarantee atomicity and prevent race conditions .
    /// Dispatched onto background threads by StatementManager after user edit confirmation .
    /// </summary>
    public async Task LearnFromCorrectionAsync(string rawSynonym, string fieldType, string category)
    {
        var normalizedCategory = category.ToUpperInvariant();
        try
        {
            // Defensive string parsing: Strip namespace prefixes (e.g., "Col:DATE" -> "DATE") safely
            var rawFieldType = fieldType.Contains(':') ? fieldType.Split(':')[1].Trim() : fieldType.Trim();

            // -------------------------------------------------------------------------
            // EXPLICIT ATOMIC TRANSACTION
            // -------------------------------------------------------------------------
            // We lock the read-modify-write priority math inside a single SQLite transaction
            // to guarantee two concurrent learning tasks cannot calculate the same Priority number .
            // -------------------------------------------------------------------------

            await _database.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
            {
                const string maxPrioritySql = "SELECT MAX(Priority) FROM Synonyms WHERE Synonym = @Synonym AND Category = @Category;";

                var currentMaxPriority = await connection.QuerySingleOrDefaultAsync<int?>(
                    maxPrioritySql,
                    new { Synonym = rawSynonym, Category = normalizedCategory },
                    transaction: transaction);

                int newPriority = (currentMaxPriority ?? 0) + 1;

                var newSynonym = new Synonyms
                {
                    FieldType = rawFieldType,
                    Synonym = rawSynonym,
                    Priority = newPriority,
                    Category = normalizedCategory
                };

                const string insertSql = @"
                    INSERT INTO Synonyms (FieldType, Synonym, Priority, Category)
                    VALUES (@FieldType, @Synonym, @Priority, @Category);";

                await connection.ExecuteAsync(insertSql, newSynonym, transaction: transaction);
            });

            _logger.LogInformation("Learned new mapping for category '{Category}': '{RawSynonym}' -> '{FieldType}'.", normalizedCategory, rawSynonym, rawFieldType);

            // Evict the RAM snapshot for this category so the next extraction task sees the new mapping
            InvalidateCache(normalizedCategory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to learn from correction: {ex.Message}");
            _logger.LogError(ex, "Failed to execute atomic self-learning for synonym '{RawSynonym}' in category '{Category}'.", rawSynonym, normalizedCategory);
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
            InvalidateCache(synonym.Category);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to add synonym: {ex.Message}");
            _logger.LogError(ex, "Failed to add synonym '{Synonym}' for field '{FieldType}'.", synonym.Synonym, synonym.FieldType);
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
            InvalidateCache(synonym.Category);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to update synonym: {ex.Message}");
            _logger.LogError(ex, "Failed to update synonym '{Synonym}' for field '{FieldType}'.", synonym.Synonym, synonym.FieldType);
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
            InvalidateCache(null); // Evict all categories since we don't know which one was affected
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SynonymService] Failed to delete synonym: {ex.Message}");
            _logger.LogError(ex, "Failed to delete synonym with Id '{Id}'.", id);
            throw;
        }
    }

    /// <summary>
    /// Removes a targeted category snapshot from RAM, forcing the next read request to rebuild from SQLite.
    /// If category is null, clears the entire dictionary.
    /// </summary>
    private void InvalidateCache(string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            _cache.Clear();
            _logger.LogInformation("Evicted all category snapshots from SynonymService RAM cache.");
        }
        else
        {
            var normalized = category.ToUpperInvariant();
            if (_cache.TryRemove(normalized, out _))
            {
                _logger.LogInformation("Evicted RAM cache snapshot for category '{Category}'.", normalized);
            }
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