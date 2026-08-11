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
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// SUBCATEGORY SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for SubCategories.
//
// SubCategories represent specific financial classifications within a Category.
//-------------------------------------------------------------
public class SubCategoryService : ISubCategoryService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<SubCategoryService> _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _subCategoryIdCache = new(StringComparer.OrdinalIgnoreCase);

    public SubCategoryService(IDatabaseService database, ILogger<SubCategoryService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------
    // FIND OR CREATE SubCategory
    // ------------------------------------------------------------
    /// <summary>
    /// Resolves an existing SubCategory ID or atomically creates a new one in O(1) memory or a single SQL execution.
    /// Accepts optional transaction boundaries for all-or-nothing batch imports.
    /// </summary>
    public async Task<int> GetOrCreateSubCategory(string name, int? categoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("SubCategory name cannot be empty.", nameof(name));

        var normalizedName = name.Trim().ToUpperInvariant();

        // IMPORTANT:
        // Include CategoryId in the cache key if SubCategory names
        // are only unique within a Category.
        var cacheKey = $"{categoryId}:{normalizedName}";


        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // -------------------------------------------------------------------------
                // TRANSACTION ROLLBACK PROTECTION GUARDRAIL
                // -------------------------------------------------------------------------
                // If an explicit transaction (tx) is passed, we are inside a batch import boundary.
                // We read from the RAM cache if available, but if it is a cache MISS, we MUST execute
                // directly against the DB without saving the new ID back to our global RAM cache.
                // Why? If the batch import later throws an exception and rolls back, any newly inserted
                // SubCategory ID vanishes from SQLite. If we cached it in RAM, subsequent tasks would crash with FK violations!
                // -------------------------------------------------------------------------
                if (tx != null)
                {
                    if (_subCategoryIdCache.TryGetValue(cacheKey, out var existingLazy))
                    {
                        if (!existingLazy.Value.IsFaulted)
                            return await existingLazy.Value;
                    }


                    return await ExecuteUpsertInternalAsync(name, categoryId, conn, tx);
                }

                // Standard autocommit execution: safe to use GetOrAdd stampede protection
                var lazyId = _subCategoryIdCache.GetOrAdd(
                    cacheKey,
                    _ => new Lazy<Task<int>>(
                        () => ExecuteUpsertInternalAsync(
                            name,
                            categoryId,
                            conn,
                            tx),
                        LazyThreadSafetyMode.ExecutionAndPublication));


                return await lazyId.Value;
            });
        }
        catch (Exception ex)
        {
            // Fault Eviction: Remove poisoned keys so subsequent requests can retry cleanly
            _logger.LogError(ex, "Failed to resolve or create subcategory '{SubCategoryName}'. Evicting cache key.", normalizedName);
            _subCategoryIdCache.TryRemove(cacheKey, out _);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL SUBCATEGORIES
    // ------------------------------------------------------------
    public async Task<List<SubCategory>> GetAllSubCategories(IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                var subcategories = await connection.QueryAsync<SubCategory>(
                    "SELECT Id, Name, CategoryId FROM SubCategories ORDER BY Name ASC",
                    transaction: transaction);

                return subcategories.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch subcategories.");
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET SUBCATEGORIES BY CATEGORY ID
    // ------------------------------------------------------------
    public async Task<List<SubCategory>> GetSubCategoriesByCategoryId(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                var subcategories = await connection.QueryAsync<SubCategory>(
                    "SELECT Id, Name, CategoryId FROM SubCategories WHERE CategoryId = @CategoryId ORDER BY Name ASC",
                    new { CategoryId = categoryId },
                    transaction: transaction);
                return subcategories.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch subcategories for category ID {CategoryId}.", categoryId);
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE SUBCATEGORY
    // ------------------------------------------------------------
    public async Task UpdateSubCategory(SubCategory subCategory, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            var updates = new List<string>();

            if (!string.IsNullOrWhiteSpace(subCategory.Name))
                updates.Add("Name = @Name");

            if (subCategory.CategoryId > 0)
                updates.Add("CategoryId = @CategoryId");

            if (updates.Count == 0)
                return;

            var sql = $@"
                UPDATE SubCategories
                SET {string.Join(", ", updates)}
                WHERE Id = @Id
            ";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, subCategory, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update subcategory with ID {SubCategoryId}.", subCategory.Id);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE SUBCATEGORY
    // ------------------------------------------------------------
    public async Task DeleteSubCategory(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Check if subcategory is used by tags
                var usageCount = await connection.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*)
                      FROM Tag
                      WHERE SubCategoryId = @SubCategoryId",
                    new { SubCategoryId = subCategoryId }, transaction: transaction);

                if (usageCount > 0)
                    throw new InvalidOperationException("Cannot delete subcategory because it is referenced by other entities.");

                await connection.ExecuteAsync(
                    @"DELETE FROM SubCategories WHERE Id = @SubCategoryId",
                    new { SubCategoryId = subCategoryId }, transaction: transaction);

                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete subcategory with ID {SubCategoryId}.", subCategoryId);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE SUBCATEGORIES BY CATEGORY ID
    // ------------------------------------------------------------
    public async Task DeleteByCategoryId(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Delete all subcategories associated with the specified category ID
                await connection.ExecuteAsync(
                    @"DELETE FROM SubCategories WHERE CategoryId = @CategoryId",
                    new { CategoryId = categoryId }, transaction: transaction);

                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete subcategories for category ID {CategoryId}.", categoryId);
            throw;
        }
    }

    /// <summary>
    /// Executes an atomic SQLite upsert. Eliminates read-then-write race conditions by attempting
    /// an INSERT OR IGNORE and immediately querying the canonical Id in a single execution block.
    /// </summary>
    private async Task<int> ExecuteUpsertInternalAsync(string name, int? categoryId, IDbConnection? conn, IDbTransaction? tx)
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
                INSERT OR IGNORE INTO SubCategories (Name, CategoryId, CreatedDate)
                VALUES (@Name, @CategoryId, @CreatedDate);

                SELECT Id FROM SubCategories WHERE Name = @Name;";

            var id = await connection.ExecuteScalarAsync<long>(sql, new
            {
                Name = name.Trim(),
                CategoryId = categoryId,
                CreatedDate = DateTime.UtcNow.ToString("o")
            }, transaction: transaction);

            _logger.LogDebug("Resolved SubCategory '{SubCategoryName}' to ID {Id}.", name, id);
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
        _subCategoryIdCache.Clear();
        _logger.LogInformation("Evicted SubCategoryService RAM cache due to data mutation.");
    }
}