using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Data;
using Dapper;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using Microsoft.Extensions.Logging;

namespace IncomeExpenditureTracker.Services.Entities
{
    // ------------------------------------------------------------
    // PAYEE SERVICE
    // ------------------------------------------------------------
    // Handles CRUD operations and mapping resolution for Payees.
    //
    // Responsibilities:
    // • Manage Payee entities and their mappings
    // • Provide O(1) exact-match resolution for statement extraction
    // • Handle payee merging and deduplication
    // ------------------------------------------------------------
    public class PayeeService : IPayeeService, IDisposable
    {
        private readonly IDatabaseService _db;
        private readonly IApplicationBroker _broker;
        private readonly ILogger<PayeeService> _logger;

        // The flat dictionary for O(1) exact-match resolution during extraction
        private readonly ConcurrentDictionary<string, long> _mappingCache = new(StringComparer.OrdinalIgnoreCase);

        // The thread-safe entity cache (keyed by Name for duplicate prevention)
        private readonly ConcurrentDictionary<string, Lazy<Payee>> _entityCache = new(StringComparer.OrdinalIgnoreCase);

        // Lazy loader for the caches, ensuring that they are loaded only once and in a thread-safe manner
        private Lazy<Task>? _cacheLoader;
        private readonly object _cacheLock = new object();

        public PayeeService(IDatabaseService db, IApplicationBroker broker, ILogger<PayeeService> logger)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // -------------------------------------------------------------------------
            // ARCHITECTURAL GUARDRAIL: CACHE ANNIHILATION
            // -------------------------------------------------------------------------
            // When the database swaps, we MUST wipe the caches to prevent
            // Profile A's data from appearing in Profile B's UI.
            // -------------------------------------------------------------------------
            _broker.Register<ProfileSwappedMessage>(this, (message) => InvalidateCache());
        }

        // ------------------------------------------------------------
        // CACHE MANAGEMENT
        // ------------------------------------------------------------

        /// <summary>
        /// Ensures the cache loader is initialized thread-safely, capturing the CancellationToken
        /// of the first request that triggers the DB load.
        /// </summary>
        private Lazy<Task> GetOrInitCacheLoader(CancellationToken ct)
        {
            if (_cacheLoader != null) return _cacheLoader;

            lock (_cacheLock)
            {
                if (_cacheLoader == null)
                {
                    _cacheLoader = CreateCacheLoader(ct);
                }
            }
            return _cacheLoader;
        }

        private Lazy<Task> CreateCacheLoader(CancellationToken ct)
        {
            return new Lazy<Task>(async () =>
            {
                try
                {
                    await _db.ExecuteWithRetryAsync(async (conn, cancelToken) =>
                    {
                        var getcmd = new CommandDefinition(
                            "SELECT * FROM Payees",
                            cancellationToken: cancelToken
                        );
                        var payees = await conn.QueryAsync<Payee>(getcmd);
                        foreach (var p in payees)
                        {
                            _entityCache.TryAdd(p.Name, new Lazy<Payee>(() => p));
                        }

                        var mapcmd = new CommandDefinition(
                            "SELECT CleanedDescription, PayeeId FROM PayeeMappings",
                            cancellationToken: cancelToken
                        );

                        var mappings = await conn.QueryAsync<(string CleanedDescription, long PayeeId)>(mapcmd);

                        foreach (var m in mappings)
                        {
                            _mappingCache.TryAdd(m.CleanedDescription, m.PayeeId);
                        }
                    }, ct);
                }
                catch
                {
                    // Fault Eviction: If the load is cancelled or fails due to a DB lock,
                    // instantly clear the loader so the next request can attempt to load the cache again.
                    lock (_cacheLock)
                    {
                        _cacheLoader = null;
                    }
                    throw;
                }
            }, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ConcurrentDictionary<string, long> GetMappingsCache()
        {
            // Note: In Phase 2, this is called synchronously.
            // Pass CancellationToken.None since a synchronous block cannot be cancelled natively.
            var loader = GetOrInitCacheLoader(CancellationToken.None);

            if (!loader.IsValueCreated || !loader.Value.IsCompleted)
            {
                loader.Value.GetAwaiter().GetResult();
            }
            return _mappingCache;
        }

        // ------------------------------------------------------------
        // READ OPERATIONS
        // ------------------------------------------------------------

        public async Task<IEnumerable<Payee>> GetAllAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var loader = GetOrInitCacheLoader(ct);
                await loader.Value; // Automatically respects the token passed during initialization
                return _entityCache.Values.Select(v => v.Value).ToList();
            }
            catch (OperationCanceledException)
            {
                throw; // Bubble up silently for the UI router
            }
        }

        // ------------------------------------------------------------
        // WRITE OPERATIONS (CRUD)
        // ------------------------------------------------------------

        public async Task<Payee> CreateAsync(Payee payee, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (payee == null) throw new ArgumentNullException(nameof(payee));

            try
            {
                return await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                {
                    const string sql = @"
                        INSERT INTO Payees (Name, IsDefaultIncomeSource, CreatedDate)
                        VALUES (@Name, @IsDefaultIncomeSource, @CreatedDate);
                        SELECT last_insert_rowid();";

                    var cmd = new CommandDefinition(
                        sql,
                        payee,
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );

                    payee.Id = await connection.ExecuteScalarAsync<long>(cmd);

                    // Update cache instantly to prevent immediate read-misses
                    _entityCache.TryAdd(payee.Name, new Lazy<Payee>(() => payee));
                    return payee;
                }, conn, tx, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create payee '{Name}'.", payee.Name);
                throw;
            }
        }

        public async Task UpdateAsync(Payee payee, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (payee == null || payee.Id <= 0)
                throw new ArgumentException("Valid payee instance with a primary key is required for update.");

            try
            {
                await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                {
                    const string sql = "UPDATE Payees SET Name = @Name, IsDefaultIncomeSource = @IsDefaultIncomeSource WHERE Id = @Id";
                    var cmd = new CommandDefinition(
                        sql,
                        payee,
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );
                    await connection.ExecuteAsync(cmd);
                    return true;
                }, conn, tx, ct);

                InvalidateCache();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update payee ID {Id}.", payee.Id);
                throw;
            }
        }

        public async Task DeleteAsync(long id, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                {
                    // 1.Check if the Payee is linked to any historical transactions
                    const string checkSql = "SELECT EXISTS(SELECT 1 FROM Transactions WHERE PayeeId = @PayeeId)";

                    var checkcmd = new CommandDefinition(
                        checkSql,
                         new { PayeeId = id },
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );

                    bool hasTransactions = await connection.ExecuteScalarAsync<bool>(checkcmd);
                    // 2. Enforce the Hard Block to protect AIS Tax Reporting
                    if (hasTransactions)
                    {
                        throw new InvalidOperationException("Hard Block: Cannot delete this Payee because it is linked to historical transactions. Please use the 'Merge' feature to safely consolidate it with another Payee.");
                    }

                    // 3. If no transactions exist, it is safe to delete.
                    // (The PayeeMappings table will automatically clean up its exact-match strings due to ON DELETE CASCADE)
                    const string sql = "DELETE FROM Payees WHERE Id = @Id";
                    var cmd = new CommandDefinition(
                        sql,
                         new { Id = id },
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );
                    await connection.ExecuteAsync(cmd);
                    return true;
                }, conn, tx, ct);

                InvalidateCache();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InvalidOperationException ex)
            {
                // Trap the Hard Block
                _logger.LogWarning(ex.Message, "Failed to delete payee ID {Id}.", id);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete payee ID {Id}.", id);
                throw;
            }
        }

        // ------------------------------------------------------------
        // MAPPINGS & MERGING
        // ------------------------------------------------------------

        public async Task AddMappingAsync(
            string cleanedDescription,
            long payeeId,
            IDbConnection? conn = null,
            IDbTransaction? tx = null,
            CancellationToken ct = default
            )
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await ExecuteDbActionAsync(async (connection, transaction, cancelToken) =>
                {
                    const string sql = @"
                        INSERT INTO PayeeMappings (CleanedDescription, PayeeId)
                        VALUES (@CleanedDescription, @PayeeId)
                        ON CONFLICT(CleanedDescription) DO UPDATE SET PayeeId = @PayeeId;";

                    var cmd = new CommandDefinition(
                        sql,
                        new
                        {
                            CleanedDescription = cleanedDescription,
                            PayeeId = payeeId
                        },
                        transaction: transaction,
                        cancellationToken: cancelToken
                    );

                    await connection.ExecuteAsync(cmd);
                    return true;
                }, conn, tx, ct);

                // Update O(1) Cache instantly
                _mappingCache[cleanedDescription] = payeeId;
                _entityCache.Clear();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add mapping for '{Description}' to Payee ID {Id}.", cleanedDescription, payeeId);
                throw;
            }
        }

        public async Task MergeEntitiesAsync(
            long targetPayeeId,
            List<long> sourcePayeeIds,
            IDbConnection? conn = null,
            IDbTransaction? tx = null,
            CancellationToken ct = default
            )
        {
            ct.ThrowIfCancellationRequested();

            if (sourcePayeeIds == null || !sourcePayeeIds.Any()) return;

            try
            {
                // If a connection/transaction is provided, use it directly. Otherwise, initiate a local transaction.
                if (conn != null && tx != null)
                {
                    await ExecuteMergeLogicAsync(targetPayeeId, sourcePayeeIds, conn, tx, ct);
                }
                else
                {
                    await _db.ExecuteInTransactionWithRetryAsync(async (connection, transaction, cancelToken) =>
                    {
                        await ExecuteMergeLogicAsync(targetPayeeId, sourcePayeeIds, connection, transaction, cancelToken);
                    }, ct);
                }

                InvalidateCache();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to merge payees into Target ID {TargetId}.", targetPayeeId);
                throw;
            }
        }

        private async Task ExecuteMergeLogicAsync(long targetPayeeId, List<long> sourcePayeeIds, IDbConnection conn, IDbTransaction tx, CancellationToken ct)
        {
            // 1. Re-point mappings to the target Payee
            var mapcmd = new CommandDefinition(
                "UPDATE PayeeMappings SET PayeeId = @TargetId WHERE PayeeId IN @SourceIds",
                new { TargetId = targetPayeeId, SourceIds = sourcePayeeIds },
                transaction: tx,
                cancellationToken: ct
            );
            await conn.ExecuteAsync(mapcmd);

            // 2. Re-point existing transactions to the target Payee
            var transactioncmd = new CommandDefinition(
                "UPDATE Transactions SET PayeeId = @TargetId WHERE PayeeId IN @SourceIds",
                new { TargetId = targetPayeeId, SourceIds = sourcePayeeIds },
                transaction: tx,
                cancellationToken: ct
            );
            await conn.ExecuteAsync(transactioncmd);

            // 3. Delete the old source payees
            var payeecmd = new CommandDefinition(
                "DELETE FROM Payees WHERE Id IN @SourceIds",
                new { SourceIds = sourcePayeeIds },
                transaction: tx,
                cancellationToken: ct
            );
            await conn.ExecuteAsync(payeecmd);
        }

        // ------------------------------------------------------------
        // RETROACTIVE SWEEP
        // ------------------------------------------------------------
        // Maps a new description and retroactively sweeps existing
        // unmapped transactions to apply the new PayeeId.
        // ------------------------------------------------------------
        public async Task ExecuteRetroactiveSweepAsync(string source, long payeeId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Source description cannot be null or empty.", nameof(source));

            try
            {
                // Route to the appropriate transactional execution path
                if (conn != null && tx != null)
                {
                    await ExecuteSweepSqlAsync(source, payeeId, conn, tx, ct);
                }
                else
                {
                    await _db.ExecuteInTransactionWithRetryAsync(async (connection, transaction, cancelToken) =>
                    {
                        await ExecuteSweepSqlAsync(source, payeeId, connection, transaction, cancelToken);
                    }, ct);
                }

                // Update O(1) Cache instantly to ensure subsequent extraction logic uses the new mapping
                _mappingCache[source] = payeeId;
                _entityCache.Clear();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute retroactive sweep for source '{Source}' to Payee ID {Id}.", source, payeeId);
                throw;
            }
        }

        private async Task ExecuteSweepSqlAsync(string source, long payeeId, IDbConnection conn, IDbTransaction tx, CancellationToken ct)
        {
            const string sql = @"
                INSERT INTO PayeeMappings (CleanedDescription, PayeeId)
                VALUES (@Source, @PayeeId)
                ON CONFLICT(CleanedDescription) DO UPDATE SET PayeeId = @PayeeId;

                UPDATE Transactions
                SET PayeeId = @PayeeId,
                    ReviewStatus = (ReviewStatus & ~4) -- Removes 'MissingPayee' flag
                WHERE Source = @Source AND PayeeId IS NULL;";

            var cmd = new CommandDefinition(
                sql,
                new
                {
                    Source = source,
                    PayeeId = payeeId
                },
                transaction: tx,
                cancellationToken: ct
            );

            await conn.ExecuteAsync(cmd);
        }

        // ------------------------------------------------------------
        // UTILITIES
        // ------------------------------------------------------------

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
                return await action(existingConn, existingTx, ct);
            }

            return await _db.ExecuteWithRetryAsync(async (connection, cancelToken) => await action(connection, null, cancelToken), ct);
        }

        private void InvalidateCache()
        {
            _mappingCache.Clear();
            _entityCache.Clear();
            lock (_cacheLock) { _cacheLoader = null; }
            _logger.LogInformation("Evicted PayeeService RAM cache due to data mutation.");
        }

        public void Dispose()
        {
            _broker.UnregisterAll(this);
            GC.SuppressFinalize(this);
        }
    }
}