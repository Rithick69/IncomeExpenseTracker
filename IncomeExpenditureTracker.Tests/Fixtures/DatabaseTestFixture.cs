using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using IncomeExpenditureTracker.Services.Database;
using Xunit;
using Moq;

// This fixture manages the lifecycle of your isolated SQLite test databases.
// It creates a temporary .db file for each test, initializes WAL mode and foreign keys, runs your table creation scripts,
// and guarantees clean deletion when the test finishes.
// It has been upgraded to support generating multiple physical .db files
// dynamically to simulate multi-profile environments.

namespace IncomeExpenditureTracker.Tests.Fixtures
{
    public class DatabaseTestFixture : IAsyncLifetime, IDisposable
    {
        public string DatabasePath { get; private set; } = string.Empty;
        public string ConnectionString { get; private set; } = string.Empty;

        public readonly IDatabaseInitializer? _databaseInitializer;
        // --- PHASE 6 MULTI-TENANT UPGRADES ---
        // We use ConcurrentBag because our Airlock tests will generate and interact with databases concurrently.
        // We must track ALL generated files and connections to guarantee OS lock releases during teardown.
        private readonly ConcurrentBag<SqliteConnection> _keepAliveConnections = new();
        private readonly ConcurrentBag<string> _createdDatabasePaths = new();

        // Exactly ONE public constructor.
        // We make it optional (= null) so we can bypass schema creation when we only need file locks.
        public DatabaseTestFixture(IDatabaseInitializer? databaseInitializer = null)
        {
            // If null is passed, we skip schema creation.
            // If a real initializer is passed, we store it for use.
            _databaseInitializer = databaseInitializer;
        }

        public async Task InitializeAsync()
        {
            // Generate the default database for existing single-tenant tests.
            var result = await CreateIsolatedDatabaseAsync("default");
            DatabasePath = result.Path;
            ConnectionString = result.ConnectionString;

            // Note: In Phase 6, DatabaseInitializer execution is delayed until after a profile is unlocked.
            // If your initializer relies on DatabaseService, ensure the service is pointed to this connection string first.
            await InitializeSchemaAsync();
        }

        /// <summary>
        /// Dynamically generates a new, isolated SQLite database file on disk.
        /// Perfect for simulating multi-profile environments (e.g., "ProfileA", "ProfileB").
        /// </summary>
        public async Task<(string Path, string ConnectionString)> CreateIsolatedDatabaseAsync(string profileIdentifier)
        {
            // Generate a unique temporary database path for total test isolation
            var path = Path.Combine(Path.GetTempPath(), $"iet_test_{profileIdentifier}_{Guid.NewGuid():N}.db");
            var connectionString = $"Data Source={path};Mode=ReadWriteCreate;";

            // Open a keep-alive connection so in-memory or WAL temp tables persist during the test
            var keepAliveConnection = new SqliteConnection(connectionString);
            await keepAliveConnection.OpenAsync();

            // Enforce architectural rules for WAL and Foreign Keys on the physical file
            await ApplyPragmasAsync(keepAliveConnection);

            // Track for mandatory OS file-lock cleanup during teardown
            _keepAliveConnections.Add(keepAliveConnection);
            _createdDatabasePaths.Add(path);

            return (path, connectionString);
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

        private async Task InitializeSchemaAsync()
        {
            // Only initialize the schema if an initializer was provided
            if (_databaseInitializer != null)
            {
                // Replace this raw SQL block with a call to your real production DatabaseInitializer service
                await _databaseInitializer.InitializeAsync();
            }
        }

        public async Task DisposeAsync()
        {
            // 1. Safely close all keep-alive connections to unbind SQLite from the processes
            foreach (var conn in _keepAliveConnections)
            {
                await conn.CloseAsync();
                await conn.DisposeAsync();
            }
            _keepAliveConnections.Clear();

            // 2. Force GC and connection pooling cleanup before attempting OS file deletion
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            // 3. Iterate through every generated database profile and nuke it from the OS
            foreach (var dbPath in _createdDatabasePaths)
            {
                var pathsToDelete = new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm" };

                foreach (var path in pathsToDelete)
                {
                    if (!File.Exists(path)) continue;

                    bool deleted = false;
                    int attempts = 0;

                    // Micro-retry loop for laggy Windows OS file locks
                    while (!deleted && attempts < 3)
                    {
                        try
                        {
                            File.Delete(path);
                            deleted = true;
                        }
                        catch (IOException)
                        {
                            attempts++;
                            if (attempts >= 3)
                            {
                                // Log to the test runner console, NOT the application error sink
                                Console.WriteLine($"[WARNING] Fixture failed to delete test artifact after 3 attempts: {path}. The OS locked it.");
                            }
                            else
                            {
                                // Wait 50ms and try again
                                Thread.Sleep(50);
                            }
                        }
                    }
                }
            }
            _createdDatabasePaths.Clear();
        }

        public void Dispose()
        {
            DisposeAsync().GetAwaiter().GetResult();
            GC.SuppressFinalize(this);
        }
    }
}