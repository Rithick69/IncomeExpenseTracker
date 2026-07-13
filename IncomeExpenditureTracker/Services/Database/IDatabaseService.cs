using System;
using System.Data;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Database;

/// <summary>
/// Provides centralized, resilient SQLite database access.
/// Enforces WAL mode, foreign key constraints, and automatic retry wrappers
/// to handle SQLITE_BUSY lock contention during concurrent background operations.
/// </summary>
public interface IDatabaseService
{
    /// <summary>
    /// Yields an open, configured database connection with PRAGMA foreign_keys = ON
    /// and PRAGMA journal_mode = WAL applied.
    /// Best suited for lock-free, read-only queries (e.g., populating RAM caches or UI grids).
    /// </summary>
    Task<IDbConnection> GetOpenConnectionAsync();

    /// <summary>
    /// Executes an asynchronous database operation within an exponential backoff retry loop.
    /// Automatically retries if transient SQLite lock contention (SQLITE_BUSY / SQLITE_LOCKED) occurs.
    /// Best suited for single-table atomic writes (e.g., adding a synonym or tag rule).
    /// </summary>
    Task ExecuteWithRetryAsync(Func<IDbConnection, Task> action);

    /// <summary>
    /// Executes an asynchronous database operation that returns a result within a retry loop.
    /// </summary>
    Task<T> ExecuteWithRetryAsync<T>(Func<IDbConnection, Task<T>> action);

    /// <summary>
    /// Initiates an explicit SQLite database transaction wrapped in a retry loop.
    /// Passes the active connection and transaction down to domain repositories.
    /// If any exception occurs, the entire transaction is explicitly rolled back.
    /// Best suited for multi-table batch imports (e.g., StatementImportService).
    /// </summary>
    Task ExecuteInTransactionWithRetryAsync(Func<IDbConnection, IDbTransaction, Task> action);

    /// <summary>
    /// Initiates an explicit SQLite database transaction that returns a result, wrapped in a retry loop.
    /// </summary>
    Task<T> ExecuteInTransactionWithRetryAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> action);
}