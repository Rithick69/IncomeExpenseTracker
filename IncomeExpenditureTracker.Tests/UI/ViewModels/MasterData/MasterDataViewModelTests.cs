using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.UI.MasterData;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels
{
    public class MasterDataViewModelTests
    {
        public MasterDataViewModelTests()
        {
            // THE CONVEYOR BELT BYPASS
            // Ensures our reactive Broker updates execute instantly in the test environment
            // instead of getting stuck waiting for an Avalonia UI thread that doesn't exist.
            ViewModelBase.IsTestEnvironment = true;
        }

        [Fact]
        public async Task LoadDataAsync_PopulatesAllCollections_And_HandlesLoadingState()
        {
            // ====================================================================
            // ARRANGE
            // ====================================================================
            var mockOrchestrator = new Mock<IMasterDataOrchestrator>();
            var mockBroker = new Mock<IApplicationBroker>();

            // We use a TaskCompletionSource on just ONE of the calls to pause the process
            // so we can test the IsLoading state mid-flight.
            var categoriesTcs = new TaskCompletionSource<List<Category>>();

            mockOrchestrator.Setup(o => o.GetAllCategoriesAsync(It.IsAny<CancellationToken>()))
                            .Returns(categoriesTcs.Task);

            // For the remaining calls, we just return empty lists instantly so the method doesn't crash
            mockOrchestrator.Setup(o => o.GetAllSubCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SubCategory>());
            mockOrchestrator.Setup(o => o.GetAllTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Tag> { new Tag { Id = 1 }, new Tag { Id = 2 } });
            mockOrchestrator.Setup(o => o.GetAllEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Entity>());
            mockOrchestrator.Setup(o => o.GetAllAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Account>());
            mockOrchestrator.Setup(o => o.GetAllSynonymsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Synonyms>());

            var viewModel = new MasterDataViewModel(mockOrchestrator.Object, mockBroker.Object);

            // ====================================================================
            // ACT
            // ====================================================================
            var commandTask = viewModel.LoadDataCommand.ExecuteAsync(null);

            // ====================================================================
            // ASSERT (Mid-Flight & Final)
            // ====================================================================

            // MID-FLIGHT: The ViewModel should be locked and show a loading message
            Assert.True(viewModel.IsLoading);
            Assert.Contains("Loading", viewModel.StatusText);

            // RELEASE THE PAUSE: Hand over the fake Categories to resume execution
            categoriesTcs.SetResult(new List<Category> { new Category { Id = 1 } });
            await commandTask;

            // FINAL STATE: The ViewModel should unlock and correctly map the data
            Assert.False(viewModel.IsLoading);
            Assert.Single(viewModel.Categories);   // We returned 1 category
            Assert.Equal(2, viewModel.Tags.Count); // We returned 2 tags
            Assert.Empty(viewModel.Entities);      // We returned 0 entities
        }

        [Fact]
        public void OnEntitySaved_BrokerMessage_TriggersAutomaticGridRefresh()
        {
            // ====================================================================
            // ARRANGE
            // ====================================================================
            var mockOrchestrator = new Mock<IMasterDataOrchestrator>();
            var mockBroker = new Mock<IApplicationBroker>();

            // Setup empty returns to prevent null references during the auto-refresh
            mockOrchestrator.Setup(o => o.GetAllCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Category>());
            mockOrchestrator.Setup(o => o.GetAllSubCategoriesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<SubCategory>());
            mockOrchestrator.Setup(o => o.GetAllTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Tag>());
            mockOrchestrator.Setup(o => o.GetAllEntitiesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Entity>());
            mockOrchestrator.Setup(o => o.GetAllAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Account>());
            mockOrchestrator.Setup(o => o.GetAllSynonymsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new List<Synonyms>());
            mockOrchestrator.Setup(o => o.GetAllImportBatchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ImportBatch>());

            mockOrchestrator.Setup(o => o.GetRuleBookSnapshotAsync(It.IsAny<CancellationToken>()))
                            .ReturnsAsync(new RuleBookSnapshot(new Dictionary<string, TagRuleDTO[]>(), 0));

            // CAPTURE THE CALLBACK
            // We intercept the ViewModel's attempt to listen for EntitySavedMessages
            Action<EntitySavedMessage>? capturedCallback = null;
            mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<EntitySavedMessage>>()))
                      .Callback<object, Action<EntitySavedMessage>>((subscriber, callback) =>
                      {
                          capturedCallback = callback;
                      });

            var viewModel = new MasterDataViewModel(mockOrchestrator.Object, mockBroker.Object);

            // ====================================================================
            // ACT
            // ====================================================================

            // Simulate another part of the application saving a new Tag
            var message = new EntitySavedMessage("Tag", "Groceries");

            // Fire the message into the ViewModel
            capturedCallback?.Invoke(message);

            // ====================================================================
            // ASSERT
            // ====================================================================

            // Prove the UI Status Text reacted to the specific entity name
            Assert.Contains("Groceries", viewModel.StatusText);
            Assert.Contains("was created", viewModel.StatusText);

            // Prove the ViewModel automatically asked the orchestrator for fresh data
            // We verify GetAllTagsAsync was called exactly 1 time in response to the message.
            mockOrchestrator.Verify(o => o.GetAllTagsAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}