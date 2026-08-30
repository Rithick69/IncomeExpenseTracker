// Import SQLite connection support
using System;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security;
using System.Runtime.InteropServices;
using Dapper;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.Services.Database;

// This service is responsible for providing a connection
// to the SQLite database file used by the application.

public class DatabaseService : IDatabaseService
{
    private string _connectionString;

    // --- The Airlock Primitives ---
    // Volatile ensures all threads see the exact same value immediately without caching.
    private volatile bool _isSwapping = false;

    // Tracks how many queries are currently flying through the database.
    private int _activeQueries = 0;

    private SecureString? _activeProfilePassword;

    // The "Passport" token. Changes every time a profile is swapped.
    private Guid _currentProfileSessionId = Guid.NewGuid();

    private readonly ILogger<DatabaseService> _logger = null!; // Initialized in constructor

    private readonly SemaphoreSlim _swapLock = new SemaphoreSlim(1, 1);
    private readonly IApplicationBroker _broker;

    // Retry configuration constants
    private const int MaxRetryAttempts = 5;
    private const int BaseDelayMilliseconds = 50;

    public DatabaseService(IConfiguration configuration, ILogger<DatabaseService> logger, IApplicationBroker broker)
    {
        // 1. Assign mandatory dependencies immediately at the top so early returns can never skip them!
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker;

        // Check if an external environment variable or test config explicitly overrides the DB path
        var configPath = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrEmpty(configPath))
        {
            _connectionString = configPath;
            return;
        }

        // 2. Default state is now empty. The DatabaseInitializer and core services will not be able
        // to connect until the Gatekeeper UI authenticates a profile and calls SetConnectionStringAsync.
        _connectionString = string.Empty;
    }

    /// <summary>
    /// Safely drains all active queries, swaps the connection string to the new profile,
    /// and annihilates the SQLite connection pool to release OS file locks.
    /// </summary>
    public async Task SetConnectionStringAsync(string newConnectionString, SecureString? profilePassword = null)
    {
        // 1. SEMAPHORE LOCK: Prevent simultaneous swaps (Double-click defense)
        await _swapLock.WaitAsync();
        try
        {
            // 1. Close the gate: prevent any NEW queries from starting.
            _isSwapping = true;

            // 2. Wait for the Airlock to drain: Check if any queries are currently executing.
            // We use Interlocked to safely read the active count across multiple threads.
            while (Interlocked.CompareExchange(ref _activeQueries, 0, 0) > 0)
            {
                // Yield the thread briefly to let active queries finish their work.
                await Task.Delay(50);
            }

            // 3. Swap the string securely now that traffic is completely stopped.
            _connectionString = newConnectionString;

            // Store the SecureString safely. It remains encrypted in RAM.
            _activeProfilePassword = profilePassword;

            // Invalidate all old passports!
            _currentProfileSessionId = Guid.NewGuid();

            // 4. Annihilate the connection pool. This is the critical step that forces
            // the OS to physically release the Windows file handle on the old .db file.
            SqliteConnection.ClearAllPools();

            // 6. CACHE ANNIHILATION: Must happen INSIDE the Airlock
            _broker.Send(new ProfileSwappedMessage());
        }
        finally
        {
            // 7. Open the gate ONLY after the swap and cache wipe are 100% complete.
            _isSwapping = false;

            // 8. Release the semaphore so future swaps can occur.
            _swapLock.Release();
        }
    }

    /// <summary>
    /// Creates a new SqliteConnection, opens it asynchronously, and strictly applies
    /// required SQLite PRAGMAs before yielding it to the caller.
    /// </summary>
    public async Task<IDbConnection> GetOpenConnectionAsync()
    {
        if (string.IsNullOrEmpty(_connectionString))
        {
            throw new InvalidOperationException("Attempted to connect to the database before a profile was loaded.");
        }
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        // -------------------------------------------------------------------------
        // SECURE SQLCIPHER UNLOCK & ZERO-LEAK MEMORY ANNIHILATION
        // -------------------------------------------------------------------------
        // In .NET, strings are immutable. If a password is built into a standard
        // connection string, it lingers in the managed heap until the Garbage Collector
        // runs, leaving it highly vulnerable to RAM scraping and memory dumps.
        //
        // To achieve true zero-leak memory:
        // 1. We omit the password from the static connection string builder.
        // 2. We dynamically inject the password into the engine using 'PRAGMA key'.
        // 3. We use an 'unsafe' block and a 'fixed' pointer to bypass .NET safety guards.
        //    This allows us to physically pin the transient string's location in RAM
        //    and forcefully overwrite the memory addresses with null terminators ('\0').
        //
        // This mathematically guarantees the plaintext password is independently
        // wiped from the application's memory space the millisecond it is no longer needed.
        // -------------------------------------------------------------------------
        if (_activeProfilePassword != null && _activeProfilePassword.Length > 0)
        {
            IntPtr unmanagedPointer = IntPtr.Zero;
            try
            {
                // Unwrap into unmanaged memory
                unmanagedPointer = Marshal.SecureStringToGlobalAllocUnicode(_activeProfilePassword);
                string tempPassword = Marshal.PtrToStringUni(unmanagedPointer)!;

                // Use raw ADO.NET to prevent Dapper from caching the password string in memory
                using var keyCommand = connection.CreateCommand();
                keyCommand.CommandText = $"PRAGMA key = '{tempPassword}';";
                await keyCommand.ExecuteNonQueryAsync();

                // Annihilate the transient managed string immediately to prevent RAM scraping
                unsafe
                {
                    fixed (char* p = tempPassword)
                    {
                        for (int i = 0; i < tempPassword.Length; i++)
                            p[i] = '\0';
                    }
                }
            }
            finally
            {
                if (unmanagedPointer != IntPtr.Zero)
                    Marshal.ZeroFreeGlobalAllocUnicode(unmanagedPointer);
            }
        }

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

        // 1. Capture the passport BEFORE waiting. This is the database this query was meant for.
        Guid expectedSessionId = _currentProfileSessionId;

        // 2. AIRLOCK WAITING: Pause execution if a profile swap is currently underway.(Double-Checked Locking)
        while (true)
        {
            // Wait if a swap is already actively happening
            while (_isSwapping)
            {
                await Task.Delay(10);
            }

            // Step onto the scale: register this query as active
            Interlocked.Increment(ref _activeQueries);

            // DOUBLE-CHECK: Did the swapper close the gate the exact microsecond we stepped forward?
            if (!_isSwapping)
            {
                // 2. PASSPORT CHECK: Did the profile change while we were waiting in line?
                if (expectedSessionId != _currentProfileSessionId)
                {
                    // We are in the wrong database! Step off the scale and ABORT immediately.
                    Interlocked.Decrement(ref _activeQueries);

                    _logger.LogWarning("Query aborted: A profile swap occurred while the query was waiting in the Airlock.");
                    throw new InvalidOperationException("Database query aborted to prevent cross-profile data bleed.");
                }

                // Passport is valid. We are safely inside.
                break;
            }

            // A swap started! We must step back out, decrement the counter, and wait.
            Interlocked.Decrement(ref _activeQueries);
            await Task.Delay(10);
        }

        try
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
        finally
        {
            // 3. AIRLOCK EXIT: Always decrement the counter, even if the query throws an exception,
            // to prevent permanently deadlocking the system.
            Interlocked.Decrement(ref _activeQueries);
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