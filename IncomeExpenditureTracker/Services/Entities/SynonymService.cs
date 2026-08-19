using System;
using System.Collections.Generic;
using System.Linq;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using System.Collections.Concurrent;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using Microsoft.Extensions.Logging;

namespace IncomeExpenditureTracker.Services.Entities;

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
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyDictionary<string, Synonyms>>>> _categoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<IEnumerable<Synonyms>>>> _allSynonymsCache = new(StringComparer.OrdinalIgnoreCase);

    public SynonymService(IDatabaseService database, ILogger<SynonymService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Seeds baseline domain enum field types for a specific category without overwriting existing data .
    /// Executes within an exponential backoff retry loop and invalidates the RAM cache upon completion .
    /// </summary>
    public async Task SeedDefaultFieldTypesAsync(IEnumerable<string> standardFieldTypes, string category, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        var normalizedCategory = category.ToUpperInvariant();

        await ExecuteDbActionAsync(async (connection, transaction) =>
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
            return true;
        }, conn, tx);
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
        const string cacheKey = "ALL_SYNONYMS";

        try
        {
            var cachedLazy = _allSynonymsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<IEnumerable<Synonyms>>>(async () =>
            {
                try
                {
                    return await _database.ExecuteWithRetryAsync(async connection =>
                    {
                        var synonyms = await connection.QueryAsync<Synonyms>("SELECT * FROM Synonyms");
                        return synonyms.ToList();
                    });
                }
                catch
                {
                    // Fault Eviction
                    _allSynonymsCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await cachedLazy.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load all synonyms from database.");
            throw;
        }
    }

    /// <summary>
    /// Serves category-scoped synonyms from an immutable in-memory RAM snapshot in O(1) time .
    /// Eliminates SQLite disk I/O during high-volume parsing and concurrent workbook staging .
    /// Here Category stands for TRANSACTION/META categorisation of header fields.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategory(string category)
    {
        var normalizedCategory = category.ToUpperInvariant();

        try
        {
            var lazySnapshot = _categoryCache.GetOrAdd(normalizedCategory, _ => new Lazy<Task<IReadOnlyDictionary<string, Synonyms>>>(async () =>
            {
                try
                {
                    return await LoadSynonymsFromDbAsync(normalizedCategory);
                }
                catch
                {
                    // Fault Eviction
                    _categoryCache.TryRemove(normalizedCategory, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            return await lazySnapshot.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve synonym snapshot for category '{Category}'.", normalizedCategory);
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

                /*
                 * WHY MAX(Priority) + 1 INSTEAD OF 0?
                 * 1. Overrides: This is a user correction, so the synonym might already exist
                 *    with a wrong FieldType. A higher priority ensures this new entry wins.
                 * 2. Audit Trail: By doing an INSERT instead of an UPDATE, we preserve the
                 *    historical record of previous mistakes and corrections.
                 */

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

    // ------------------------------------------------------------
    // ADD SYNONYM
    // ------------------------------------------------------------
    public async Task AddSynonymAsync(Synonyms synonym, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = @"
            INSERT INTO Synonyms (FieldType, Synonym, Priority, Category)
            VALUES (@FieldType, @Synonym, @Priority, @Category);";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, synonym, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(synonym.Category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add synonym '{Synonym}' for field '{FieldType}'.", synonym.Synonym, synonym.FieldType);
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE SYNONYM
    // ------------------------------------------------------------
    public async Task UpdateSynonymAsync(Synonyms synonym, IDbConnection? conn = null, IDbTransaction? tx = null)
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

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, synonym, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(synonym.Category);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update synonym '{Synonym}' for field '{FieldType}'.", synonym.Synonym, synonym.FieldType);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE SYNONYM
    // ------------------------------------------------------------
    public async Task DeleteSynonymAsync(int id, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            const string sql = "DELETE FROM Synonyms WHERE Id = @Id;";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, new { Id = id }, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete synonym with Id '{Id}'.", id);
            throw;
        }
    }


    // ------------------------------------------------------------
    // HELPERS
    // ------------------------------------------------------------
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            return await action(existingConn, existingTx);
        }

        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
    }

    /// <summary>
    /// Removes a targeted category snapshot from RAM, forcing the next read request to rebuild from SQLite.
    /// If category is null, clears the entire dictionary.
    /// </summary>
    private void InvalidateCache(string? category)
    {
        _allSynonymsCache.Clear();

        if (string.IsNullOrWhiteSpace(category))
        {
            _categoryCache.Clear();
            _logger.LogInformation("Evicted all category snapshots from SynonymService RAM cache.");
        }
        else
        {
            var normalized = category.ToUpperInvariant();
            if (_categoryCache.TryRemove(normalized, out _))
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