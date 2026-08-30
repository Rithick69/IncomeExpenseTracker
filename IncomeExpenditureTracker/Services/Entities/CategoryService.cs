using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// CATEGORY SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for Categories.
//
// Categories represent broad financial classifications such as:
// • Income
// • Expenses
//-------------------------------------------------------------
public class CategoryService : ICategoryService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<CategoryService> _logger;

    private readonly IApplicationBroker _broker;

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _categoryIdCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Category>>>> _categoryListCache = new(StringComparer.OrdinalIgnoreCase);

    public CategoryService(IDatabaseService database, ILogger<CategoryService> logger, IApplicationBroker broker)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker;

        // -------------------------------------------------------------------------
        // ARCHITECTURAL GUARDRAIL: CACHE ANNIHILATION
        // -------------------------------------------------------------------------
        // When the database swaps, we MUST wipe the ConcurrentDictionary
        // to prevent Profile A's data from appearing in Profile B's UI.
        // -------------------------------------------------------------------------
        _broker.Register<ProfileSwappedMessage>(this, (message) => InvalidateCache());
    }

    // ------------------------------------------------------------
    // FIND OR CREATE Category
    // ------------------------------------------------------------
    /// <summary>
    /// Resolves an existing Category ID or atomically creates a new one in O(1) memory or a single SQL execution.
    /// Accepts optional transaction boundaries for all-or-nothing batch imports.
    /// </summary>
    public async Task<int> GetOrCreateCategory(string name, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Category name cannot be empty.", nameof(name));

        var cacheKey = name.Trim().ToUpperInvariant();

        try
        {
            // -------------------------------------------------------------------------
            // TRANSACTION ROLLBACK PROTECTION GUARDRAIL
            // -------------------------------------------------------------------------
            if (conn != null && tx != null)
            {
                // Read from cache if it exists (safe reference data reuse)
                if (_categoryIdCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted)
                {
                    return await existingLazy.Value;
                }

                // Cache MISS inside a transaction: Execute directly, DO NOT cache the result.
                // Bypass the retry wrapper entirely, as transactions cannot be retried mid-flight.
                return await ExecuteUpsertInternalAsync(name, conn, tx);
            }

            // -------------------------------------------------------------------------
            // STANDALONE EXECUTION (Safe for caching and retries)
            // -------------------------------------------------------------------------
            var lazyId = _categoryIdCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    // Execute using the retry policy wrapper
                    var id = await _database.ExecuteWithRetryAsync(retryConn =>
                        ExecuteUpsertInternalAsync(name, retryConn, null));

                    // ONLY clear the list caches on a cache miss when we actually hit the database
                    _categoryListCache.Clear();

                    return id;
                }
                catch
                {
                    // Fault Eviction: Remove from cache inside the factory if DB fails
                    _categoryIdCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the task (concurrent requests will await this same task)
            return await lazyId.Value;

        }
        catch (Exception ex)
        {
            // Fallback catch just in case something throws outside the Lazy block
            _logger.LogError(ex, "Failed to resolve or create category '{CategoryName}'. Evicting cache key.", cacheKey);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL CATEGORIES
    // ------------------------------------------------------------
    public async Task<List<Category>> GetAllCategories()
    {
        const string cacheKey = "ALL_CATEGORIES";
        try
        {
            // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
            var cachedLazy = _categoryListCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Category>>>(async () =>
            {
                try
                {
                    // Execute using the standard retry wrapper (no external conn/tx needed)
                    return await _database.ExecuteWithRetryAsync(async c =>
                    {
                        const string sql = "SELECT * FROM Categories ORDER BY Name ASC";
                        var entities = await c.QueryAsync<Category>(sql);
                        return entities.ToList();
                    });
                }
                catch
                {
                    // Fault Eviction: Remove the broken task from cache if the DB fails
                    _categoryListCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the Lazy task. All concurrent threads will await this same exact task instance.
            return await cachedLazy.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch categories.");
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE CATEGORY
    // ------------------------------------------------------------
    public async Task UpdateCategory(Category category, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            var updates = new List<string>();

            if (!string.IsNullOrWhiteSpace(category.Name))
                updates.Add("Name = @Name");

            if (updates.Count == 0)
                return;

            var sql = $@"
                UPDATE Entities
                SET {string.Join(", ", updates)}
                WHERE Id = @Id
            ";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, category, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category with ID {CategoryId}.", category.Id);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE CATEGORY
    // ------------------------------------------------------------
    public async Task DeleteCategory(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Check if category is used by subcategories
                var usageCount = await connection.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*)
                      FROM SubCategories
                      WHERE CategoryId = @CategoryId",
                    new { CategoryId = categoryId }, transaction: transaction);

                if (usageCount > 0)
                    throw new InvalidOperationException("Cannot delete category because subcategories reference it.");

                await connection.ExecuteAsync(
                    @"DELETE FROM Categories WHERE Id = @CategoryId",
                    new { CategoryId = categoryId }, transaction: transaction);

                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete category with ID {CategoryId}.", categoryId);
            throw;
        }
    }

    /// <summary>
    /// Executes an atomic SQLite upsert. Eliminates read-then-write race conditions by attempting
    /// an INSERT OR IGNORE and immediately querying the canonical Id in a single execution block.
    /// </summary>
    private async Task<int> ExecuteUpsertInternalAsync(string name, IDbConnection? conn, IDbTransaction? tx)
    {
        return await ExecuteDbActionAsync(async (connection, transaction) =>
        {
            // -------------------------------------------------------------------------
            // ATOMIC UPSERT SQL
            // -------------------------------------------------------------------------
            // 1. INSERT OR IGNORE attempts creation without throwing on UNIQUE(Name) collisions.
            // 2. SELECT Id immediately fetches the ID whether it was just created or already existed.
            // This guarantees race-condition free execution across concurrent threads.
            // -------------------------------------------------------------------------
            var sql = @"
                INSERT OR IGNORE INTO Categories (Name, CreatedDate)
                VALUES (@Name, @CreatedDate);

                SELECT Id FROM Categories WHERE Name = @Name;";

            var id = await connection.ExecuteScalarAsync<long>(sql, new
            {
                Name = name.Trim(),
                CreatedDate = DateTime.UtcNow.ToString("o")
            }, transaction: transaction);

            _logger.LogDebug("Resolved Category '{CategoryName}' to ID {Id}.", name, id);
            return (int)id;
        }, conn, tx);
    }

    /// <summary>
    /// Unified execution helper. Routes queries through the resilient ExecuteWithRetryAsync wrapper
    /// unless an active connection and transaction are passed from a parent orchestrator.
    /// </summary>
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx)
    {
        if (existingConn != null)
        {
            // Execute directly within the parent transaction boundary (e.g., StatementImportService)
            return await action(existingConn, existingTx);
        }

        // Execute as a standalone, retry-protected UI operation
        return await _database.ExecuteWithRetryAsync(async connection => await action(connection, null));
    }

    private void InvalidateCache()
    {
        _categoryIdCache.Clear();
        _categoryListCache.Clear();
        _logger.LogInformation("Evicted CategoryService RAM cache due to data mutation.");
    }
}