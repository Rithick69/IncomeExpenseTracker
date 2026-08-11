using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

// Note: Update these namespaces to match your actual project structure
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Tests.Integration
{
    public class TransactionManagementTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _dbFilePath;
        private readonly string _connectionString;
        private readonly DatabaseService _databaseService;

        public TransactionManagementTests(ITestOutputHelper output)
        {
            _output = output;

            // 1. Generate an isolated physical file for OS lock testing
            _dbFilePath = Path.GetTempFileName();
            _connectionString = $"Data Source={_dbFilePath};Pooling=False;";

            // 2. Mock IConfiguration to trigger the override path in your DatabaseService constructor
            var inMemorySettings = new Dictionary<string, string?> {
                { "ConnectionStrings:DefaultConnection", _connectionString }
            };
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var mockLogger = new Mock<ILogger<DatabaseService>>();

            // 3. Initialize your exact DatabaseService
            _databaseService = new DatabaseService(configuration, mockLogger.Object);

            // 4. Manually initialize the SQLite Schema using Dapper
            InitializeSchema().GetAwaiter().GetResult();
        }

        private async Task InitializeSchema()
        {
            // Create a table with strict constraints to test rollbacks
            var createTableSql = @"
                CREATE TABLE Transactions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TransactionHash TEXT UNIQUE NOT NULL,
                    AccountId INTEGER NOT NULL,
                    Source TEXT,
                    Amount REAL NOT NULL,
                    Date TEXT NOT NULL
                );";

            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                await connection.ExecuteAsync(createTableSql);
            });
        }

        // =================================================================================
        // TEST 1: The Happy Path
        // =================================================================================
        [Fact]
        public async Task ImportBatch_HappyPath_CommitsAllRowsSuccessfully()
        {
            // Arrange
            var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Source, Amount, Date) VALUES (@TransactionHash, @AccountId, @Source, @Amount, @Date);";
            var row1 = new { TransactionHash = "HASH-001", AccountId = 1, Source = "Grocery Store", Amount = -50.00, Date = DateTime.UtcNow.ToString("O") };
            var row2 = new { TransactionHash = "HASH-002", AccountId = 1, Source = "Salary", Amount = 2000.00, Date = DateTime.UtcNow.ToString("O") };

            // Act
            await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
            {
                // Pass the transaction to Dapper so it runs inside the boundary
                await connection.ExecuteAsync(sql, row1, transaction);
                await connection.ExecuteAsync(sql, row2, transaction);
            });

            // Assert
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var rows = (await connection.QueryAsync("SELECT * FROM Transactions")).ToList();
                Assert.Equal(2, rows.Count);
            });
        }

        // =================================================================================
        // TEST 2: The Atomic Rollback (Explicit Failure)
        // =================================================================================
        [Fact]
        public async Task ImportBatch_AtomicRollback_SavesZeroRowsIfOneFails()
        {
            // Arrange
            var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Source, Amount, Date) VALUES (@TransactionHash, @AccountId, @Source, @Amount, @Date);";
            var validRow = new { TransactionHash = "HASH-VALID", AccountId = 1, Source = "Coffee", Amount = -5.00, Date = DateTime.UtcNow.ToString("O") };
            var invalidRow = new { TransactionHash = (string?)null, AccountId = 1, Source = "Ghost", Amount = -10.00, Date = DateTime.UtcNow.ToString("O") }; // Missing NOT NULL hash

            // Act & Assert
            await Assert.ThrowsAnyAsync<SqliteException>(async () =>
            {
                await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
                {
                    await connection.ExecuteAsync(sql, validRow, transaction);
                    // This will throw due to NOT NULL constraint, triggering the defensive rollback block in your service
                    await connection.ExecuteAsync(sql, invalidRow, transaction);
                });
            });

            // CRITICAL ASSERTION: Prove transaction rolled back and the VALID row was NOT saved.
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions");
                Assert.Equal(0, count); // Perfect atomicity
            });
        }

        // =================================================================================
        // TEST 3: The Duplicate Hash Rejection
        // =================================================================================
        [Fact]
        public async Task ImportBatch_DuplicateHash_ThrowsConstraintExceptionAndRollsBack()
        {
            // Arrange
            var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Source, Amount, Date) VALUES (@TransactionHash, @AccountId, @Source, @Amount, @Date);";
            var existingRow = new { TransactionHash = "SHARED-HASH", AccountId = 1, Source = "Initial", Amount = -100.00, Date = DateTime.UtcNow.ToString("O") };
            var duplicateRow = new { TransactionHash = "SHARED-HASH", AccountId = 1, Source = "Double", Amount = -100.00, Date = DateTime.UtcNow.ToString("O") };

            // Seed existing
            await _databaseService.ExecuteWithRetryAsync(async connection =>
                await connection.ExecuteAsync(sql, existingRow));

            // Act & Assert
            var ex = await Assert.ThrowsAnyAsync<SqliteException>(async () =>
            {
                await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
                {
                    // This violates the UNIQUE constraint on TransactionHash
                    await connection.ExecuteAsync(sql, duplicateRow, transaction);
                });
            });

            Assert.Contains("UNIQUE constraint failed", ex.Message);

            // Verify only the original row exists
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions");
                Assert.Equal(1, count);
            });
        }

        // =================================================================================
        // TEST 4: The Empty Batch
        // =================================================================================
        [Fact]
        public async Task ImportBatch_EmptyBatch_CompletesGracefullyWithoutGhostData()
        {
            // Act
            await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
            {
                // Simulate receiving 0 rows to import. We do nothing.
                await Task.CompletedTask;
            });

            // Assert
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions");
                Assert.Equal(0, count);
            });
        }

        // =================================================================================
        // TEST 5: The Connection Drop
        // =================================================================================
        [Fact]
        public async Task ImportBatch_ConnectionDrop_FailsLoudlyAndPreventsData()
        {
            // Act & Assert
            await Assert.ThrowsAnyAsync<Exception>(async () =>
            {
                await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
                {
                    // Forcibly close the connection mid-flight
                    connection.Close();

                    var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Amount, Date) VALUES ('ORPHAN', 1, 50, '2026-08-05');";
                    await connection.ExecuteAsync(sql, null, transaction);
                });
            });
        }

        // =================================================================================
        // TEST 6: The Permanent Lock
        // =================================================================================
        [Fact]
        public async Task ExecuteWithRetryAsync_PermanentLock_ExhaustsRetriesAndThrows()
        {
            // 1. Take a permanent EXCLUSIVE lock using an entirely separate connection
            using var lockingConnection = new SqliteConnection(_connectionString);
            await lockingConnection.OpenAsync();
            using var lockCommand = lockingConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            try
            {
                // Act & Assert: This should trigger your exponential backoff loop 5 times, then fail
                var ex = await Assert.ThrowsAsync<SqliteException>(async () =>
                {
                    await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
                    {
                        var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Amount, Date) VALUES ('LOCK-FAIL', 1, 50, '2026-08-05');";
                        await connection.ExecuteAsync(sql, null, transaction);
                    });
                });

                // Error code 5 = SQLITE_BUSY (Database is locked)
                Assert.Equal(5, ex.SqliteErrorCode);
            }
            finally
            {
                // Ensure lock is released so file can be deleted in Dispose()
                lockCommand.CommandText = "ROLLBACK;";
                await lockCommand.ExecuteNonQueryAsync();
            }
        }

        // =================================================================================
        // TEST 7: The Temporary Lock
        // =================================================================================
        [Fact]
        public async Task ExecuteWithRetryAsync_TemporaryLock_RecoversAndCommits()
        {
            // 1. Take an exclusive lock to simulate contention
            var lockingConnection = new SqliteConnection(_connectionString);
            await lockingConnection.OpenAsync();
            var lockCommand = lockingConnection.CreateCommand();
            lockCommand.CommandText = "BEGIN EXCLUSIVE;";
            await lockCommand.ExecuteNonQueryAsync();

            // 2. Schedule the lock to be released after 500ms
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                lockCommand.CommandText = "ROLLBACK;";
                await lockCommand.ExecuteNonQueryAsync();
                await lockingConnection.CloseAsync();
                await lockingConnection.DisposeAsync();
            });

            // Act
            // Your retry engine will hit the lock, wait using exponential backoff,
            // and try again. Because the background task releases the lock, it will succeed!
            await _databaseService.ExecuteInTransactionWithRetryAsync(async (connection, transaction) =>
            {
                var sql = "INSERT INTO Transactions (TransactionHash, AccountId, Amount, Date) VALUES ('TEMP-LOCK-RECOVER', 1, 50, '2026-08-05');";
                await connection.ExecuteAsync(sql, null, transaction);
            });

            // Assert
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var count = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transactions WHERE TransactionHash = 'TEMP-LOCK-RECOVER'");
                Assert.Equal(1, count);
            });
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (File.Exists(_dbFilePath))
            {
                try
                {
                    File.Delete(_dbFilePath);
                }
                catch (IOException) { /* Best effort */ }
            }
        }
    }
}