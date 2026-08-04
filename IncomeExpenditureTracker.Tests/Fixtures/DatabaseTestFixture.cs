using System;
using System.Data.Common;
using System.IO;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Services.Database;
using Microsoft.Data.Sqlite;
using Xunit;

// This fixture manages the lifecycle of your isolated SQLite test databases.
// It creates a temporary .db file for each test, initializes WAL mode and foreign keys, runs your table creation scripts,
// and guarantees clean deletion when the test finishes.

namespace IncomeExpenditureTracker.Tests.Fixtures
{
    public class DatabaseTestFixture : IAsyncLifetime, IDisposable
    {
        public string DatabasePath { get; private set; } = string.Empty;
        public string ConnectionString { get; private set; } = string.Empty;

        public readonly DatabaseInitializer _databaseInitializer;
        private SqliteConnection? _keepAliveConnection;

        public DatabaseTestFixture(DatabaseInitializer databaseInitializer)
        {
            _databaseInitializer = databaseInitializer;
        }

        public async Task InitializeAsync()
        {
            // Generate a unique temporary database path for total test isolation
            DatabasePath = Path.Combine(Path.GetTempPath(), $"iet_test_{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={DatabasePath};Mode=ReadWriteCreate;";

            // Open a keep-alive connection so in-memory or WAL temp tables persist during the test
            _keepAliveConnection = new SqliteConnection(ConnectionString);
            await _keepAliveConnection.OpenAsync();

            await ApplyPragmasAsync(_keepAliveConnection);
            await InitializeSchemaAsync(_keepAliveConnection);
        }

        private async Task ApplyPragmasAsync(SqliteConnection connection)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                PRAGMA foreign_keys = ON;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task InitializeSchemaAsync(SqliteConnection connection)
        {
            // Note: Replace this raw SQL block with a call to your real production DatabaseInitializer service
            // e.g., await _databaseInitializer.InitializeAsync();
            await _databaseInitializer.InitializeAsync();
        }

        public async Task DisposeAsync()
        {
            if (_keepAliveConnection != null)
            {
                await _keepAliveConnection.CloseAsync();
                await _keepAliveConnection.DisposeAsync();
                _keepAliveConnection = null;
            }

            // Force GC and connection pooling cleanup before attempting OS file deletion
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            try
            {
                if (File.Exists(DatabasePath))
                {
                    File.Delete(DatabasePath);
                    var walPath = $"{DatabasePath}-wal";
                    var shmPath = $"{DatabasePath}-shm";
                    if (File.Exists(walPath)) File.Delete(walPath);
                    if (File.Exists(shmPath)) File.Delete(shmPath);
                }
            }
            catch (IOException)
            {
                // Ignore transient OS file-lock delays during rapid test teardown
            }
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}