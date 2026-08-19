using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Orchestration;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Integration
{
    public class TransactionReviewOrchestratorTests
    {
        private readonly Mock<IDatabaseService> _dbMock = new();
        private readonly Mock<ITransactionService> _transactionMock = new();
        private readonly Mock<IImportBatchService> _batchMock = new();
        private readonly Mock<ITagService> _tagMock = new();

        private readonly Mock<ILogger<TransactionReviewOrchestrator>> _loggerMock = new();
        private readonly Mock<IApplicationBroker> _brokerMock = new();

        private TransactionReviewOrchestrator CreateOrchestrator()
        {
            return new TransactionReviewOrchestrator(
                _dbMock.Object,
                _transactionMock.Object,
                _batchMock.Object,
                _tagMock.Object,
                _loggerMock.Object,
                _brokerMock.Object);
        }

        private void SetupDatabaseTransactionMock()
        {
            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action =>
                   {
                       await action.Invoke(null!, null!);
                   });
        }

        /// <summary>
        /// Objective: Validate that the orchestrator accurately fetches paginated lists and counts
        /// from the stateless B-Tree WAL queries in the TransactionService.
        /// </summary>
        [Fact]
        public async Task GetTransactionsAsync_FetchesDataAndTotalCount_ReturnsPagedResult()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            var args = new TransactionFilterArgs();
            var expectedTransactions = new List<Transaction> { new Transaction { Id = 1 } };
            int expectedCount = 50;

            _transactionMock.Setup(x => x.GetFilteredTransactionsAsync(args, null, null))
                            .ReturnsAsync(expectedTransactions);
            _transactionMock.Setup(x => x.GetFilteredTransactionCountAsync(args, null, null))
                            .ReturnsAsync(expectedCount);

            // Act
            var result = await orchestrator.GetTransactionsAsync(args);

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Items);
            Assert.Equal(expectedCount, result.TotalCount);
        }

        /// <summary>
        /// Objective: Validate the Ripple Effect Decoupling.
        /// Batch corrections must update the database, and then trigger asynchronous background learning.
        /// </summary>
        [Fact]
        public async Task ApplyCorrectionsAsync_ExecutesBatchUpdate_AndFiresBackgroundLearning()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();

            var corrections = new List<TransactionCorrectionDTO>
            {
                new TransactionCorrectionDTO { TransactionId = 1, RawDescription = "UBER EATS", TargetTagId = 5 }
            };

            // Simply setup the mock to return a completed task
            _tagMock.Setup(x => x.LearnRuleFromOverrideAsync("UBER EATS", 5))
                    .Returns(Task.CompletedTask);

            // Act
            await orchestrator.ApplyCorrectionsAsync(corrections);

            // Assert
            // 1. Verify the database batch update executed immediately on the main thread
            _transactionMock.Verify(x => x.UpdateTransactionsBulkAsync(corrections, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);

            // 2. Use the polling pattern to wait for the background Task.Run to execute and hit our mock
            bool backgroundTaskCompleted = false;
            for (int i = 0; i < 60; i++) // 60 attempts * 50ms = 3 seconds max wait
            {
                try
                {
                    // If this succeeds, the background thread finished and called the mock!
                    _tagMock.Verify(x => x.LearnRuleFromOverrideAsync("UBER EATS", 5), Times.Once);

                    backgroundTaskCompleted = true;
                    break; // Exit the loop immediately to keep the test fast
                }
                catch (MockException)
                {
                    await Task.Delay(50); // Not done yet, yield for 50ms and check again
                }
            }

            // 3. Verify the background task successfully completed within the timeout limit
            Assert.True(backgroundTaskCompleted, "Background learning task did not execute within the timeout.");
        }

        /// <summary>
        /// Objective: Validate 100% All-or-Nothing Revert. Reverting an import batch must delete
        /// all child transactions before deleting the master batch record under a unified token.
        /// </summary>
        [Fact]
        public async Task RevertImportBatchAsync_DeletesTransactionsAndBatchRecord()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();
            int batchId = 123;

            // Act
            await orchestrator.RevertImportBatchAsync(batchId);

            // Assert
            _transactionMock.Verify(x => x.DeleteByBatchIdAsync(batchId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _batchMock.Verify(x => x.DeleteBatchAsync(batchId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Edge Case. If the corrections list is null or empty, the method must exit
        /// early without opening a database transaction or spawning background threads.
        /// </summary>
        [Fact]
        public async Task ApplyCorrectionsAsync_NullOrEmptyCorrections_ReturnsEarly()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();

            // Act
            await orchestrator.ApplyCorrectionsAsync(null!);
            await orchestrator.ApplyCorrectionsAsync(new List<TransactionCorrectionDTO>());

            // Assert
            _dbMock.Verify(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()), Times.Never);
            _tagMock.Verify(x => x.LearnRuleFromOverrideAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }

        /// <summary>
        /// Objective: Negative Case. If a correction does not contain a TargetTagId, the background
        /// learning process must skip it rather than attempting to learn a null tag.
        /// </summary>
        [Fact]
        public async Task ApplyCorrectionsAsync_TargetTagIdIsNull_SkipsLearning()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();

            var corrections = new List<TransactionCorrectionDTO>
            {
                new TransactionCorrectionDTO { TransactionId = 1, RawDescription = "UBER EATS", TargetTagId = null }
            };

            // Act
            await orchestrator.ApplyCorrectionsAsync(corrections);

            // We need a small delay because we aren't using a TaskCompletionSource to wait on a null execution
            await Task.Delay(100);

            // Assert
            _transactionMock.Verify(x => x.UpdateTransactionsBulkAsync(corrections, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _tagMock.Verify(x => x.LearnRuleFromOverrideAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }

        /// <summary>
        /// Objective: Fault Tolerance (Ripple Effect). If the background learning task throws an exception,
        /// it must be safely swallowed without crashing the application or rolling back the user's batch update.
        /// </summary>
        [Fact]
        public async Task ApplyCorrectionsAsync_LearningThrowsException_SwallowsExceptionAndDoesNotCrash()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();

            var corrections = new List<TransactionCorrectionDTO>
            {
                new TransactionCorrectionDTO { TransactionId = 1, RawDescription = "UBER EATS", TargetTagId = 5 }
            };

            // Setup the mock to throw an exception when the background task calls it
            _tagMock.Setup(x => x.LearnRuleFromOverrideAsync("UBER EATS", 5))
                    .ThrowsAsync(new InvalidOperationException("Failed to tokenize description"));

            // Act - This should NOT throw an exception back to the caller
            await orchestrator.ApplyCorrectionsAsync(corrections);

            // Assert

            // 1. Verify DB update still happened successfully on the main thread
            _transactionMock.Verify(x => x.UpdateTransactionsBulkAsync(corrections, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);

            // 2. Use the polling pattern to wait for the background Task.Run to execute and hit our mock
            bool backgroundTaskCompleted = false;
            for (int i = 0; i < 60; i++) // 60 attempts * 50ms = 3 seconds max wait
            {
                try
                {
                    // If this succeeds, the background thread finished and called the mock!
                    _tagMock.Verify(x => x.LearnRuleFromOverrideAsync("UBER EATS", 5), Times.Once);
                    backgroundTaskCompleted = true;
                    break; // Exit the loop immediately to keep the test fast
                }
                catch (MockException)
                {
                    await Task.Delay(50); // Not done yet, yield for 50ms and check again
                }
            }

            Assert.True(backgroundTaskCompleted, "Background learning task did not execute within the timeout.");
        }

        /// <summary>
        /// Objective: Negative Case for 100% Rollback. If deleting child transactions fails,
        /// the master batch record deletion must never be called, simulating a strict rollback.
        /// </summary>
        [Fact]
        public async Task RevertImportBatchAsync_TransactionDeletionFails_ThrowsAndRollsBack()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();
            int batchId = 123;

            _transactionMock.Setup(x => x.DeleteByBatchIdAsync(batchId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()))
                            .ThrowsAsync(new InvalidOperationException("Database locked"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await orchestrator.RevertImportBatchAsync(batchId));

            // The batch deletion must never execute because the child transaction wipe failed
            _batchMock.Verify(x => x.DeleteBatchAsync(It.IsAny<int>(), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Never);
        }
    }
}