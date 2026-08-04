using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Tagging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;

namespace IncomeExpenditureTracker.Tests.Integration
{
    /// <summary>
    /// Validates the Concurrency & Stampede Defense mechanisms of reference services.
    /// </summary>
    public class TagServiceConcurrencyTests
    {
        private readonly Mock<IDatabaseService> _mockDatabaseService;
        private readonly Mock<ILogger<TagService>> _mockLogger;

        public TagServiceConcurrencyTests()
        {
            _mockDatabaseService = new Mock<IDatabaseService>();
            _mockLogger = new Mock<ILogger<TagService>>();
        }

        /*
         * -------------------------------------------------------------------------------------------------
         * ARCHITECTURE NOTE: WHY WE USE MOCKS FOR CONCURRENCY TESTING
         * -------------------------------------------------------------------------------------------------
         * Unlike integration tests (e.g., FieldMapperTests) where we use a real in-memory SQLite database
         * to validate SQL constraints and logic, this test specifically validates C# thread orchestration.
         *
         * We use Moq here instead of a real database for two critical reasons:
         *
         * 1. ARTIFICIAL BOTTLENECKS: A real in-memory SQLite read executes in under 0.1 milliseconds.
         *    This is often too fast to guarantee that 50 concurrent threads will actually collide.
         *    Mocks allow us to inject an artificial delay (e.g., Thread.Sleep) to freeze the first thread,
         *    forcing the other 49 threads to pile up and test the lock-free registry's defense.
         *
         * 2. EXACT INVOCATION COUNTING: We must mathematically prove that the underlying database method
         *    was triggered EXACTLY ONCE, regardless of how many threads requested the cache simultaneously.
         *    Moq provides .Verify(..., Times.Once) to give us this absolute certainty.
         * -------------------------------------------------------------------------------------------------
         */
        [Fact]
        public async Task GetRuleBookSnapshotAsync_CacheStampede_ExecutesExactlyOneDatabaseRead()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Simulate a multi-file cache stampede.
            // Prove that when 50 concurrent threads request the snapshot simultaneously,
            // the lazy cache registry enforces EXACTLY ONE underlying SQLite read.
            // ---------------------------------------------------------------------------------

            // Arrange
            int databaseReadCount = 0;

            // Mock the exact method used by TagService to query the database
            _mockDatabaseService
                .Setup(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<RuleBookSnapshot>>>()))
                .ReturnsAsync(() =>
                {
                    // Increment thread-safely just in case the defense fails
                    Interlocked.Increment(ref databaseReadCount);

                    // Simulate a slow database read (e.g., 50ms) to ensure threads pile up
                    // and wait on the Lazy<Task> instead of finishing sequentially.
                    Thread.Sleep(50);

                    return new RuleBookSnapshot(new Dictionary<string, TagRuleDTO[]>(), 999);
                });

            // Initialize the required DescriptionParser dependency.
            // DescriptionParser expects an ILogger<DescriptionParser>, not ILogger<TagService>.
            var descriptionLogger = new Mock<ILogger<DescriptionParser>>();
            var descriptionParser = new DescriptionParser(descriptionLogger.Object);

            // Initialize the TagService (System Under Test)
            var sut = new TagService(
                _mockDatabaseService.Object,
                descriptionParser,
                _mockLogger.Object);

            int concurrentRequestCount = 50;
            var tasks = new Task<RuleBookSnapshot>[concurrentRequestCount];

            // Act
            // We use a Barrier to pause all threads until they are all spun up,
            // guaranteeing they hit the GetRuleBookSnapshotAsync method at the EXACT same time.
            using var barrier = new Barrier(concurrentRequestCount);

            for (int i = 0; i < concurrentRequestCount; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    barrier.SignalAndWait(); // Wait for all 50 threads to reach this point
                    return await sut.GetRuleBookSnapshotAsync();
                });
            }

            // Await all 50 concurrent requests
            var results = await Task.WhenAll(tasks);

            // Assert
            // 1. Every single thread should have successfully received a snapshot
            Assert.All(results, snapshot => Assert.NotNull(snapshot));

            // 2. THE CRITICAL ASSERTION: The underlying database was only queried exactly ONCE
            Assert.Equal(1, databaseReadCount);

            // Verify via Moq as a secondary check that the ExecuteWithRetryAsync wrapper was only called once
            _mockDatabaseService.Verify(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<RuleBookSnapshot>>>()), Times.Once);
        }

        [Fact]
        public async Task GetRuleBookSnapshotAsync_TransientFault_EvictsCacheAndRetriesOnNextCall()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate Fault Eviction Guardrails[cite: 1].
            // If a database read fails, the faulted task must be immediately evicted from RAM[cite: 1, 3].
            // This prevents the application from serving permanently cached exceptions and allows
            // the next request to attempt a fresh database read.
            // ---------------------------------------------------------------------------------

            // Arrange
            int callCount = 0;

            // We set up the mock to fail on the FIRST call, but succeed on the SECOND call.
            _mockDatabaseService
                .Setup(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<RuleBookSnapshot>>>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    if (callCount == 1)
                    {
                        // First Attempt: Simulate a transient SQLite I/O glitch
                        throw new Exception("Transient SQLite lock exception");
                    }

                    // Second Attempt: Database recovered, return a valid snapshot
                    return new RuleBookSnapshot(new Dictionary<string, TagRuleDTO[]>(), 999);
                });

            var descriptionLogger = new Mock<ILogger<DescriptionParser>>();
            var descriptionParser = new DescriptionParser(descriptionLogger.Object);
            var sut = new TagService(_mockDatabaseService.Object, descriptionParser, _mockLogger.Object);

            // Act & Assert

            // Attempt 1: The database throws an exception. We assert that it naturally bubbles up[cite: 1].
            await Assert.ThrowsAsync<Exception>(() => sut.GetRuleBookSnapshotAsync());

            // Attempt 2: We call it again.
            // If fault eviction failed, the cache would just return the cached exception and skip the DB.
            // Because fault eviction succeeds, it will hit the DB a second time and return the valid snapshot.
            var result = await sut.GetRuleBookSnapshotAsync();

            // The second call must successfully return the snapshot
            Assert.NotNull(result);
            Assert.Equal(999, result.MiscTagId);

            // THE CRITICAL ASSERTION: Prove the database was queried EXACTLY TWICE.
            Assert.Equal(2, callCount);
            _mockDatabaseService.Verify(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<RuleBookSnapshot>>>()), Times.Exactly(2));
        }
    }
}