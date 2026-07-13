// Import SQLite connection support
using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Dapper;

namespace IncomeExpenditureTracker.Services.Database;

// This service is responsible for providing a connection
// to the SQLite database file used by the application.

public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger = null!; // Initialized in constructor

    // Retry configuration constants
    private const int MaxRetryAttempts = 5;
    private const int BaseDelayMilliseconds = 50;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger)
    {
        // Check if an external environment variable or test config explicitly overrides the DB path
        var configPath = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(configPath))
        {
            _connectionString = configPath;
            return;
        }

        // Default Desktop Behavior: Restore your exact LocalApplicationData path and shared cache logic
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataFolder, "IncomeExpenditureTracker");

        // directory creation is idempotent; if it already exists, this is a no-op
        Directory.CreateDirectory(appFolder);

        var databaseFile = Path.Combine(appFolder, "transactions.db");

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();
        _logger = logger;
    }

    /// <summary>
    /// Creates a new SqliteConnection, opens it asynchronously, and strictly applies
    /// required SQLite PRAGMAs before yielding it to the caller.
    /// </summary>
    public async Task<IDbConnection> GetOpenConnectionAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // -------------------------------------------------------------------------
        // ARCHITECTURAL GUARDRAIL: MANDATORY PRAGMAS
        // -------------------------------------------------------------------------
        // 1. foreign_keys = ON: Must be executed per connection in SQLite. Without this,
        //    our compound uniqueness constraints and relational bindings are ignored.
        // 2. journal_mode = WAL: Ensures non-blocking concurrent reads while writes occur.
        // -------------------------------------------------------------------------
        await connection.ExecuteAsync("PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;");

        return connection;
    }

    public async Task ExecuteWithRetryAsync(Func<IDbConnection, Task> action)
    {
        await ExecuteWithRetryInternalAsync(async () =>
        {
            using var connection = await GetOpenConnectionAsync();
            await action(connection);
            return true; // Dummy return to satisfy generic helper
        });
    }

    public async Task<T> ExecuteWithRetryAsync<T>(Func<IDbConnection, Task<T>> action)
    {
        return await ExecuteWithRetryInternalAsync(async () =>
        {
            using var connection = await GetOpenConnectionAsync();
            return await action(connection);
        });
    }

    public async Task ExecuteInTransactionWithRetryAsync(Func<IDbConnection, IDbTransaction, Task> action)
    {
        await ExecuteWithRetryInternalAsync(async () =>
        {
            using var connection = await GetOpenConnectionAsync();

            // Initiate the explicit transaction boundary
            using var transaction = connection.BeginTransaction();
            try
            {
                // Pass connection and active transaction to caller's repository methods
                await action(connection, transaction);

                // If delegate succeeds without throwing, commit atomically to disk
                transaction.Commit();
                return true;
            }
            catch (Exception ex)
            {
                // -------------------------------------------------------------------------
                // DEFENSIVE ROLLBACK
                // -------------------------------------------------------------------------
                // If any foreign key violation, formatting error, or constraint failure occurs,
                // we instantly revert all changes made during this session.
                // -------------------------------------------------------------------------
                _logger.LogWarning(ex, "Exception occurred inside explicit database transaction. Rolling back changes.");
                try
                {
                    transaction.Rollback();
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "Failed to execute transaction rollback.");
                }

                throw; // Rethrow original exception so the calling service knows it failed
            }
        });
    }

    public async Task<T> ExecuteInTransactionWithRetryAsync<T>(Func<IDbConnection, IDbTransaction, Task<T>> action)
    {
        return await ExecuteWithRetryInternalAsync(async () =>
        {
            using var connection = await GetOpenConnectionAsync();
            using var transaction = connection.BeginTransaction();
            try
            {
                var result = await action(connection, transaction);
                transaction.Commit();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Exception occurred inside explicit database transaction. Rolling back changes.");
                try { transaction.Rollback(); } catch { /* Ignore cascade rollback failures */ }
                throw;
            }
        });
    }

    /// <summary>
    /// Core retry engine. Intercepts transient SQLite lock errors (SQLITE_BUSY / SQLITE_LOCKED)
    /// and applies exponential backoff with random jitter.
    /// </summary>
    private async Task<T> ExecuteWithRetryInternalAsync<T>(Func<Task<T>> operation)
    {
        int attempt = 0;
        var random = new Random();

        while (true)
        {
            try
            {
                attempt++;
                return await operation();
            }
            catch (SqliteException ex) when (IsTransientLockError(ex))
            {
                // If we've exhausted our retry budget, log and bubble up the crash
                if (attempt >= MaxRetryAttempts)
                {
                    _logger.LogError(ex, "Database remained locked after {MaxAttempts} exponential retry attempts. Aborting operation.", MaxRetryAttempts);
                    throw;
                }

                // -------------------------------------------------------------------------
                // EXPONENTIAL BACKOFF WITH JITTER
                // -------------------------------------------------------------------------
                // Formula: (BaseDelay * 2^attempt) + Random(10, 25) ms
                // Example: Attempt 1 = ~115ms | Attempt 2 = ~215ms | Attempt 3 = ~415ms
                // The random jitter prevents multiple background tasks from waking up at the exact
                // same millisecond and colliding again.
                // -------------------------------------------------------------------------
                int exponentialDelay = BaseDelayMilliseconds * (int)Math.Pow(2, attempt);
                int jitter = random.Next(10, 25);
                int totalDelay = exponentialDelay + jitter;

                _logger.LogWarning("SQLite lock contention detected (Error Code: {ErrorCode}). Retrying attempt {Attempt}/{MaxAttempts} in {Delay}ms...",
                    ex.SqliteErrorCode, attempt, MaxRetryAttempts, totalDelay);

                await Task.Delay(totalDelay);
            }
            catch (Exception ex)
            {
                // Non-transient exceptions (syntax errors, null refs, schema bugs) fail immediately
                _logger.LogDebug(ex, "Non-transient database exception encountered. Failing immediately without retry.");
                throw;
            }
        }
    }

    /// <summary>
    /// Evaluates if a SqliteException represents a temporary file lock.
    /// SQLite Error Code 5 = SQLITE_BUSY (The database file is locked)
    /// SQLite Error Code 6 = SQLITE_LOCKED (A table in the database is locked)
    /// </summary>
    private static bool IsTransientLockError(SqliteException ex)
    {
        return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
    }
}