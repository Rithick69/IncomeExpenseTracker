using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Tests.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Integration
{
    // Uses the upgraded fixture to generate multiple dynamic .db files
    public class DatabaseServiceAirlockTests : IAsyncLifetime
    {
        private readonly DatabaseTestFixture _fixture;

        private readonly Mock<IApplicationBroker> _brokerMock;

        public DatabaseServiceAirlockTests()
        {
            // Instantiate manually. We pass null because Airlock tests only test OS file locks,
            // so we don't need the DatabaseInitializer to create table schemas.
            _fixture = new DatabaseTestFixture(null);
            _brokerMock = new Mock<IApplicationBroker>();
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public async Task DisposeAsync()
        {
            // Guarantee OS lock cleanup after every test run
            await _fixture.DisposeAsync();
        }

        /// <summary>
        /// Creates a pristine instance of DatabaseService for each test
        /// to prevent shared state interference during concurrent runs.
        /// </summary>
        private DatabaseService CreateIsolatedDatabaseService()
        {
            var loggerMock = new Mock<ILogger<DatabaseService>>();

            // Build a real, empty configuration object.
            // GetConnectionString("DefaultConnection") will naturally return null,
            // completely bypassing Moq's extension method limitations.
            var emptyConfig = new ConfigurationBuilder().Build();

            return new DatabaseService(emptyConfig, loggerMock.Object, _brokerMock.Object);
        }

        [Fact]
        public async Task Drain_Test_Swap_Waits_For_InFlight_Queries_To_Finish()
        {
            // Arrange
            var dbService = CreateIsolatedDatabaseService();
            var profileA = await _fixture.CreateIsolatedDatabaseAsync("DrainA");
            var profileB = await _fixture.CreateIsolatedDatabaseAsync("DrainB");

            await dbService.SetConnectionStringAsync(profileA.ConnectionString);

            bool slowQueryFinished = false;

            // Act - Start a query that takes 500ms (e.g., a complex background learning task)
            var slowQueryTask = dbService.ExecuteWithRetryAsync(async conn =>
            {
                await Task.Delay(500);
                slowQueryFinished = true;
            });

            // Yield briefly to ensure the slow query has entered the Airlock (Interlocked incremented)
            await Task.Delay(50);

            // Attempt to swap databases while the slow query is still running
            var swapTask = dbService.SetConnectionStringAsync(profileB.ConnectionString);

            // Await the swap. It MUST block internally until the slow query finishes.
            await swapTask;

            // Assert
            // If the Airlock works, the swap task completes ONLY AFTER the slow query sets this to true.
            Assert.True(slowQueryFinished, "The database was swapped before the in-flight query finished! Data corruption risk.");
        }

        [Fact]
        public async Task Stowaway_Test_Queries_Waiting_During_Swap_Are_Aborted()
        {
            // Arrange
            var dbService = CreateIsolatedDatabaseService();
            var profileA = await _fixture.CreateIsolatedDatabaseAsync("GateA");
            var profileB = await _fixture.CreateIsolatedDatabaseAsync("GateB");

            await dbService.SetConnectionStringAsync(profileA.ConnectionString);

            // Block the Airlock for 1 full second
            var blockingTask = dbService.ExecuteWithRetryAsync(async conn =>
            {
                await Task.Delay(1000);
            });

            await Task.Delay(50);

            // Initiate the swap to Profile B (closing the gate)
            var swapTask = dbService.SetConnectionStringAsync(profileB.ConnectionString);

            await Task.Delay(50);

            // Act - Fire 50 concurrent queries meant for Profile A while the gate is closed
            var stopwatch = Stopwatch.StartNew();
            var parallelQueries = Enumerable.Range(0, 50).Select(_ =>
                dbService.ExecuteWithRetryAsync(async conn => await conn.ExecuteScalarAsync<int>("SELECT 1;"))
            ).ToList();

            // Assert - The queries MUST throw an exception because the passport changed while they waited!
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.WhenAll(parallelQueries));

            // Verify the exception is exactly our security abort message
            Assert.Contains("cross-profile data bleed", exception.Message);

            // Wait for the swap to fully complete
            await swapTask;
            stopwatch.Stop();

            // Verify the queries actually waited at the gate for the swap to finish before they aborted
            Assert.True(stopwatch.ElapsedMilliseconds >= 800, "Queries aborted too early! They didn't wait at the gate.");
        }

        [Fact]
        public async Task Pool_Annihilation_Test_Releases_OS_File_Locks()
        {
            // Arrange
            var dbService = CreateIsolatedDatabaseService();
            var profileA = await _fixture.CreateIsolatedDatabaseAsync("LockA");
            var profileB = await _fixture.CreateIsolatedDatabaseAsync("LockB");

            await dbService.SetConnectionStringAsync(profileA.ConnectionString);

            // Act 1 - Execute a query on Profile A.
            // This forces SQLite to pool the connection and keeps the OS file locked.
            await dbService.ExecuteWithRetryAsync(async conn => await conn.ExecuteScalarAsync<int>("SELECT 1;"));

            // Act 2 - Swap to Profile B.
            // This triggers SqliteConnection.ClearAllPools().
            await dbService.SetConnectionStringAsync(profileB.ConnectionString);

            // Assert - Attempt to delete Profile A's database file physically from the disk.
            // If the pool was not cleared, the OS will throw an IOException ("File is being used by another process").
            var exception = Record.Exception(() =>
            {
                File.Delete(profileA.Path);

                // Also assert WAL buffers are cleanly released
                if (File.Exists($"{profileA.Path}-wal")) File.Delete($"{profileA.Path}-wal");
                if (File.Exists($"{profileA.Path}-shm")) File.Delete($"{profileA.Path}-shm");
            });

            Assert.Null(exception); // Must be perfectly null, meaning complete OS lock release
        }

        [Fact]
        public async Task SetConnectionStringAsync_ConcurrentCalls_ExecuteSequentiallyWithoutCorruption()
        {
            // Arrange
            int swapCount = 0;
            var dbService = CreateIsolatedDatabaseService();

            // We mock the broker to track how many times the cache wipe was broadcast
            _brokerMock.Setup(b => b.Send(It.IsAny<ProfileSwappedMessage>()))
                       .Callback(() => Interlocked.Increment(ref swapCount));

            // Act - Simulate a user double-clicking or rapid-firing profile swaps
            var task1 = dbService.SetConnectionStringAsync("Path1");
            var task2 = dbService.SetConnectionStringAsync("Path2");
            var task3 = dbService.SetConnectionStringAsync("Path3");

            await Task.WhenAll(task1, task2, task3);

            // Assert
            // Proves the SemaphoreSlim successfully forced the 3 swaps to execute in perfect order
            // without deadlocking the Interlocked.CompareExchange while loops.
            Assert.Equal(3, swapCount);
        }
    }
}