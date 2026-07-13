using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// ENTITY SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for Entities.
//
// Entities represent financial institutions such as:
// • Banks
// • Credit card providers
// • Wallet services
//-------------------------------------------------------------
public class EntityService : IEntityService
{
    private readonly IDatabaseService _database;
    private readonly ILogger<EntityService> _logger;

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _entityIdCache = new(StringComparer.OrdinalIgnoreCase);

    public EntityService(IDatabaseService database, ILogger<EntityService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // ------------------------------------------------------------
    // FIND OR CREATE ENTITY
    // ------------------------------------------------------------
    /// <summary>
    /// Resolves an existing Entity ID or atomically creates a new one in O(1) memory or a single SQL execution [source: 4].
    /// Accepts optional transaction boundaries for all-or-nothing batch imports.
    /// </summary>
    public async Task<int> GetOrCreateEntity(string name, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Entity name cannot be empty.", nameof(name));

        var normalizedName = name.Trim().ToUpperInvariant();

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
                // Entity ID vanishes from SQLite. If we cached it in RAM, subsequent tasks would crash with FK violations!
                // -------------------------------------------------------------------------
                if (tx != null)
                {
                    if (_entityIdCache.TryGetValue(normalizedName, out var existingLazy) && !existingLazy.Value.IsFaulted)
                    {
                        return await existingLazy.Value;
                    }

                    return await ExecuteUpsertInternalAsync(name, conn, tx);
                }

                // Standard autocommit execution: safe to use GetOrAdd stampede protection
                var lazyId = _entityIdCache.GetOrAdd(normalizedName, key =>
                    new Lazy<Task<int>>(() => ExecuteUpsertInternalAsync(name, conn, tx)));

                return await lazyId.Value;
            });
        }
        catch (Exception ex)
        {
            // Fault Eviction: Remove poisoned keys so subsequent requests can retry cleanly
            _logger.LogError(ex, "Failed to resolve or create entity '{EntityName}'. Evicting cache key.", normalizedName);
            _entityIdCache.TryRemove(normalizedName, out _);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL ENTITIES
    // ------------------------------------------------------------
    public async Task<List<Entity>> GetAllEntities(IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            return await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                var entities = await connection.QueryAsync<Entity>(
                    "SELECT Id, Name, Country, CreatedDate FROM Entities ORDER BY Name ASC",
                    transaction: transaction);

                return entities.ToList();
            }, conn, tx);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch entities.");
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE ENTITY
    // ------------------------------------------------------------
    public async Task UpdateEntity(Entity entity, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            var updates = new List<string>();

            if (!string.IsNullOrWhiteSpace(entity.Name))
                updates.Add("Name = @Name");

            if (!string.IsNullOrWhiteSpace(entity.Country))
                updates.Add("Country = @Country");

            if (updates.Count == 0)
                return;

            var sql = $@"
                UPDATE Entities
                SET {string.Join(", ", updates)}
                WHERE Id = @Id
            ";

            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                await connection.ExecuteAsync(sql, entity, transaction: transaction);
                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update entity with ID {EntityId}.", entity.Id);
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE ENTITY
    // ------------------------------------------------------------
    public async Task DeleteEntity(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null)
    {
        try
        {
            await ExecuteDbActionAsync(async (connection, transaction) =>
            {
                // Check if entity is used by accounts
                var usageCount = await connection.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*)
                      FROM Accounts
                      WHERE EntityId = @EntityId",
                    new { EntityId = entityId }, transaction: transaction);

                if (usageCount > 0)
                    throw new InvalidOperationException("Cannot delete entity because accounts reference it.");

                await connection.ExecuteAsync(
                    @"DELETE FROM Entities WHERE Id = @EntityId",
                    new { EntityId = entityId }, transaction: transaction);

                return true;
            }, conn, tx);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete entity with ID {EntityId}.", entityId);
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
                INSERT OR IGNORE INTO Entities (Name, Country, CreatedDate)
                VALUES (@Name, @Country, @CreatedDate);

                SELECT Id FROM Entities WHERE Name = @Name;";

            var id = await connection.ExecuteScalarAsync<long>(sql, new
            {
                Name = name.Trim(),
                Country = string.Empty,
                CreatedDate = DateTime.UtcNow.ToString("o")
            }, transaction: transaction);

            _logger.LogDebug("Resolved Entity '{EntityName}' to ID {Id}.", name, id);
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
        _entityIdCache.Clear();
        _logger.LogInformation("Evicted EntityService RAM cache due to data mutation.");
    }
}