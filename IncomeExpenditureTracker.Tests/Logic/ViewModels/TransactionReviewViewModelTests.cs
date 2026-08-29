using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.ViewModels;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Tests.Logic.ViewModels
{
    public class TransactionReviewViewModelTests
    {
        public TransactionReviewViewModelTests()
        {
            // THE CONVEYOR BELT BYPASS
            // Avalonia uses a UI Thread (a conveyor belt) to queue screen updates.
            // xUnit does not have this conveyor belt. If we don't set this flag,
            // our UI update code gets placed in a queue that never runs, causing tests to fail.
            // Setting this to true forces RunOnUIThread() to execute code instantly.
            ViewModelBase.IsTestEnvironment = true;
        }

        [Fact]
        public async Task LoadTransactionsAsync_SetsLoadingState_And_PopulatesGrid()
        {
            // ====================================================================
            // ARRANGE (Set up the test environment)
            // ====================================================================
            var mockOrchestrator = new Mock<ITransactionReviewOrchestrator>();
            var mockBroker = new Mock<IApplicationBroker>();

            // The fake data our mocked database will eventually return
            var fakeData = new PagedResult<Transaction>
            {
                TotalCount = 150,
                Items = new List<Transaction>
                {
                    new Transaction { Id = 1 },
                    new Transaction { Id = 2 }
                }
            };

            // THE PAUSE BUTTON (TaskCompletionSource)
            // Normally, an async method runs too fast for us to check the "IsLoading" state.
            // A TaskCompletionSource allows us to pause the mocked Orchestrator mid-execution.
            var tcs = new TaskCompletionSource<PagedResult<Transaction>>();

            // We tell the mock: "When the ViewModel asks for Page 1 (Offset 0, Limit 50),
            // don't return data immediately. Give it the paused task instead."
            mockOrchestrator.Setup(o => o.GetTransactionsAsync(
                It.Is<TransactionFilterArgs>(a => a.Limit == 50 && a.Offset == 0),
                It.IsAny<CancellationToken>()))
                .Returns(tcs.Task);

            var viewModel = new TransactionReviewViewModel(mockOrchestrator.Object, mockBroker.Object);

            // ====================================================================
            // ACT (Execute the logic)
            // ====================================================================

            // We start the command, but we DO NOT 'await' it yet.
            // It is currently stuck waiting for the Orchestrator (our paused task).
            var commandTask = viewModel.LoadTransactionsCommand.ExecuteAsync(null);

            // ====================================================================
            // ASSERT (Verify the results)
            // ====================================================================

            // 1. MID-FLIGHT STATE: Prove the UI locked down and shows a loading message.
            Assert.True(viewModel.IsLoading);
            Assert.Contains("Loading", viewModel.StatusText);

            // RELEASE THE PAUSE BUTTON: Hand the fake data to the waiting ViewModel.
            tcs.SetResult(fakeData);

            // Now we wait for the ViewModel to finish processing the data.
            await commandTask;

            // 2. FINAL STATE: Prove the UI unlocked, updated the total counts,
            // and correctly calculated that 150 items / 50 per page = 3 pages.
            Assert.False(viewModel.IsLoading);
            Assert.Equal(2, viewModel.Transactions.Count);
            Assert.Equal(150, viewModel.TotalItems);
            Assert.Equal(3, viewModel.TotalPages);
        }

        [Fact]
        public void OnNewImportCompleted_BrokerMessage_TriggersAutomaticGridRefresh()
        {
            // ====================================================================
            // ARRANGE
            // ====================================================================
            var mockOrchestrator = new Mock<ITransactionReviewOrchestrator>();
            var mockBroker = new Mock<IApplicationBroker>();

            // Give the mock a default empty return so the reload doesn't crash
            mockOrchestrator.Setup(o => o.GetTransactionsAsync(It.IsAny<TransactionFilterArgs>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PagedResult<Transaction> { Items = new List<Transaction>(), TotalCount = 0 });

            // CAPTURING THE HIDDEN CALLBACK
            // When the ViewModel is created, it tells the Broker: "Call this hidden method
            // if you ever see an ImportBatchCompletedMessage." We use Moq to intercept
            // and save that hidden method so we can manually trigger it during the test.
            Action<ImportBatchCompletedMessage>? capturedCallback = null;

            mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<ImportBatchCompletedMessage>>()))
                      .Callback<object, Action<ImportBatchCompletedMessage>>((subscriber, callback) =>
                      {
                          capturedCallback = callback;
                      });

            var viewModel = new TransactionReviewViewModel(mockOrchestrator.Object, mockBroker.Object);

            // Force the user onto page 3 to set up our specific test scenario
            viewModel.CurrentPage = 3;

            // ====================================================================
            // ACT
            // ====================================================================

            // Create a fake success message (simulating a background import finishing)
            var message = new ImportBatchCompletedMessage(500);

            // Fire the message directly into the ViewModel's registered callback
            capturedCallback?.Invoke(message);

            // ====================================================================
            // ASSERT
            // ====================================================================

            // Prove the ViewModel reacted to the background event by immediately
            // resetting the view to Page 1 so the user sees the newest imported data.
            Assert.Equal(1, viewModel.CurrentPage);

            // Prove it automatically asked the database for Page 1 (Offset 0) without
            // waiting for the user to click a "Refresh" button.
            mockOrchestrator.Verify(o => o.GetTransactionsAsync(
                It.Is<TransactionFilterArgs>(a => a.Offset == 0),
                It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}