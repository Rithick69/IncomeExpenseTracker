// Import SQLite connection support
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
namespace IncomeExpenditureTracker.Services.Database;

// This service is responsible for providing a connection
// to the SQLite database file used by the application.

public class DatabaseService : IDatabaseService
{
    private readonly string _connectionString;

    public DatabaseService()
    {
        // 1. Path Safety: Store in the user's local app data folder
        var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appFolder = Path.Combine(appDataFolder, "IncomeExpenditureTracker");

        if (!Directory.Exists(appFolder))
        {
            Directory.CreateDirectory(appFolder);
        }

        var databaseFile = Path.Combine(appFolder, "transactions.db");

        // 2. Connection String Construction
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Cache = SqliteCacheMode.Shared
        };

        _connectionString = builder.ToString();
    }

    // This method returns a connection object that other
    // parts of the application will use to run queries.
    public SqliteConnection GetConnection()
    {
        // Return a new SQLite connection
        return new SqliteConnection(_connectionString);
    }

    // 3. Robust Execution Wrapper with Retry Logic
    public async Task<T> ExecuteWithRetryAsync<T>(Func<SqliteConnection, Task<T>> dbOperation, int maxRetries = 3)
    {
        int attempt = 0;
        int baseDelayMs = 200; // Starting delay for exponential backoff

        while (true)
        {
            try
            {
                // Clean termination: 'await using' ensures the connection is immediately disposed
                await using var connection = GetConnection();
                await connection.OpenAsync();

                // Enforce SQLite safety PRAGMAs per connection
                await using (var command = connection.CreateCommand())
                {
                    // WAL mode vastly improves concurrent read/write performance
                    // PRAGMA foreign_keys = ON ensures referential integrity
                    command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
                    await command.ExecuteNonQueryAsync();
                }

                // Execute the actual database work
                return await dbOperation(connection);
            }
            catch (SqliteException ex) when (IsTransientError(ex))
            {
                attempt++;
                if (attempt > maxRetries)
                {
                    // Exhausted all retries, throw the error up the stack
                    throw new InvalidOperationException($"Database operation failed after {maxRetries} attempts due to concurrency locks.", ex);
                }

                // Exponential backoff: Wait 200ms, then 400ms, then 800ms...
                int delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                await Task.Delay(delay);
            }
        }
    }

    // Overload for operations that do not return data (e.g., INSERTS, UPDATES)
    public async Task ExecuteWithRetryAsync(Func<SqliteConnection, Task> dbOperation, int maxRetries = 3)
    {
        await ExecuteWithRetryAsync<bool>(async conn =>
        {
            await dbOperation(conn);
            return true;
        }, maxRetries);
    }

    // Helper to identify if the error is a temporary lock
    private bool IsTransientError(SqliteException ex)
    {
        // 5 = SQLITE_BUSY (Database is locked)
        // 6 = SQLITE_LOCKED (A specific table is locked)
        return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
    }
}