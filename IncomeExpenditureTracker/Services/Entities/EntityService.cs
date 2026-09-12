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
// ENTITY SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for Entities.
//
// Entities represent financial institutions such as:
// • Banks
// • Credit card providers
// • Wallet services
//-------------------------------------------------------------
public class EntityService : IEntityService, IDisposable
{
    private readonly IDatabaseService _database;
    private readonly ILogger<EntityService> _logger;

    private readonly IApplicationBroker _broker;

    private readonly ConcurrentDictionary<string, Lazy<Task<int>>> _entityIdCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, Lazy<Task<List<Entity>>>> _entityListCache = new();

    public EntityService(IDatabaseService database, ILogger<EntityService> logger, IApplicationBroker broker)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _broker = broker ?? throw new ArgumentNullException(nameof(broker));

        // -------------------------------------------------------------------------
        // ARCHITECTURAL GUARDRAIL: CACHE ANNIHILATION
        // -------------------------------------------------------------------------
        // When the database swaps, we MUST wipe the ConcurrentDictionary
        // to prevent Profile A's data from appearing in Profile B's UI.
        // -------------------------------------------------------------------------
        _broker.Register<ProfileSwappedMessage>(this, (message) => InvalidateCache());
    }

    // ------------------------------------------------------------
    // FIND OR CREATE ENTITY
    // ------------------------------------------------------------
    /// <summary>
    /// Resolves an existing Entity ID or atomically creates a new one in O(1) memory or a single SQL execution.
    /// Accepts optional transaction boundaries for all-or-nothing batch imports.
    /// </summary>
    public async Task<int> GetOrCreateEntity(string name, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Entity name cannot be empty.", nameof(name));

        var cacheKey = name.Trim().ToUpperInvariant();

        try
        {
            // -------------------------------------------------------------------------
            // TRANSACTION ROLLBACK PROTECTION GUARDRAIL
            // -------------------------------------------------------------------------
            if (conn != null && tx != null)
            {
                // Read from cache if it exists (safe reference data reuse)
                if (_entityIdCache.TryGetValue(cacheKey, out var existingLazy) && !existingLazy.Value.IsFaulted && !existingLazy.Value.IsCanceled)
                {
                    return await existingLazy.Value;
                }

                // Cache MISS inside a transaction: Execute directly, DO NOT cache the result.
                // Bypass the retry wrapper entirely, as transactions cannot be retried mid-flight.
                return await ExecuteUpsertInternalAsync(name, conn, tx, ct);
            }

            // -------------------------------------------------------------------------
            // STANDALONE EXECUTION (Safe for caching and retries)
            // -------------------------------------------------------------------------
            var lazyId = _entityIdCache.GetOrAdd(cacheKey, _ => new Lazy<Task<int>>(async () =>
            {
                try
                {
                    // Execute using the retry policy wrapper
                    var id = await _database.ExecuteWithRetryAsync((retryConn, cancelToken) =>
                        ExecuteUpsertInternalAsync(name, retryConn, null, cancelToken), ct);

                    // ONLY clear the list caches on a cache miss when we actually hit the database
                    _entityListCache.Clear();

                    return id;
                }
                catch
                {
                    // Fault Eviction: Remove from cache inside the factory if DB fails
                    _entityIdCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the task (concurrent requests will await this same task)
            return await lazyId.Value;

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fallback catch just in case something throws outside the Lazy block
            _logger.LogError(ex, "Failed to resolve or create entity '{EntityName}'. Evicting cache key.", cacheKey);
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL ENTITIES
    // ------------------------------------------------------------

    public async Task<List<Entity>> GetAllEntities(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        const string cacheKey = "ALL_ENTITIES";
        try
        {
            // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
            var cachedLazy = _entityListCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<Entity>>>(async () =>
            {
                try
                {
                    // Execute using the standard retry wrapper (no external conn/tx needed)
                    return await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
                    {
                        const string sql = "SELECT Id, Name, Country, CreatedDate FROM Entities ORDER BY Name ASC";
                        var cmd = new CommandDefinition(sql, cancellationToken: cancelToken);
                        var entities = await c.QueryAsync<Entity>(cmd);
                        return entities.ToList();
                    }, ct);
                }
                catch
                {
                    // Fault Eviction: Remove the broken task from cache if the DB fails
                    _entityListCache.TryRemove(cacheKey, out var _);
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication));

            // Await the Lazy task. All concurrent threads will await this same exact task instance.
            return await cachedLazy.Value;
        }
        catch (OperationCanceledException)
        {
            throw;
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
    public async Task UpdateEntity(Entity entity, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

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

            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                var cmd = new CommandDefinition(sql, entity, transaction: transaction, cancellationToken: cancelToken);
                await connection.ExecuteAsync(cmd);
                return true;
            }, conn, tx, ct);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (OperationCanceledException)
        {
            throw;
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
    public async Task DeleteEntity(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
            {
                // Check if entity is used by accounts
                var usageCount = await HasChildAccountsAsync(entityId, connection, transaction, cancelToken);

                if (usageCount)
                {
                    const string sql = "UPDATE Accounts SET EntityId = NULL WHERE EntityId = @EntityId;";
                    var cmd = new CommandDefinition(sql, new { EntityId = entityId }, transaction: transaction, cancellationToken: cancelToken);
                    await connection.ExecuteScalarAsync(cmd);
                }

                var deletecmd = new CommandDefinition(
                    @"DELETE FROM Entities WHERE Id = @EntityId",
                    new { EntityId = entityId },
                    transaction: transaction,
                    cancellationToken: cancelToken
                );
                await connection.ExecuteAsync(deletecmd);

                return true;
            }, conn, tx, ct);

            InvalidateCache(); // Evict cache after mutation to ensure consistency
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete entity with ID {EntityId}.", entityId);
            throw;
        }
    }

    public async Task<bool> HasChildAccountsAsync(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
    {
        const string sql = "SELECT 1 FROM Accounts WHERE EntityId = @EntityId LIMIT 1;";

        if (conn != null)
        {
            var cmd = new CommandDefinition(
                sql,
                new { EntityId = entityId },
                transaction: tx,
                cancellationToken: ct
            );
            return await conn.ExecuteScalarAsync<bool>(cmd);
        }

        return await _database.ExecuteWithRetryAsync(async (c, cancelToken) =>
        {
            var cmd = new CommandDefinition(
                sql,
                new { EntityId = entityId },
                cancellationToken: cancelToken
            );
            return await c.ExecuteScalarAsync<bool>(cmd);
        }, ct);
    }

    /// <summary>
    /// Executes an atomic SQLite upsert. Eliminates read-then-write race conditions by attempting
    /// an INSERT OR IGNORE and immediately querying the canonical Id in a single execution block.
    /// </summary>
    private async Task<int> ExecuteUpsertInternalAsync(string name, IDbConnection? conn, IDbTransaction? tx, CancellationToken ct)
    {
        return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
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

            var cmd = new CommandDefinition
            (
                sql,
                new
                {
                    Name = name.Trim(),
                    Country = string.Empty,
                    CreatedDate = DateTime.UtcNow
                },
                transaction: transaction,
                cancellationToken: cancelToken
            );

            var id = await connection.ExecuteScalarAsync<long>(cmd);

            _logger.LogDebug("Resolved Entity '{EntityName}' to ID {Id}.", name, id);
            return (int)id;
        }, conn, tx, ct);
    }

    /// <summary>
    /// Unified execution helper. Routes queries through the resilient ExecuteWithRetryAsync wrapper
    /// unless an active connection and transaction are passed from a parent orchestrator.
    /// </summary>
    private async Task<T> ExecuteDbActionAsync<T>(
        Func<IDbConnection, IDbTransaction?, CancellationToken, Task<T>> action,
        IDbConnection? existingConn,
        IDbTransaction? existingTx,
        CancellationToken ct)
    {
        if (existingConn != null)
        {
            // Execute directly within the parent transaction boundary (e.g., StatementImportService)
            return await action(existingConn, existingTx, ct);
        }

        // Execute as a standalone, retry-protected UI operation
        return await _database.ExecuteWithRetryAsync(async (connection, cancelToken) => await action(connection, null, cancelToken), ct);
    }

    private void InvalidateCache()
    {
        _entityIdCache.Clear();
        _entityListCache.Clear();
        _logger.LogInformation("Evicted EntityService RAM cache due to data mutation.");
    }

    public void Dispose()
    {
        _broker.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}