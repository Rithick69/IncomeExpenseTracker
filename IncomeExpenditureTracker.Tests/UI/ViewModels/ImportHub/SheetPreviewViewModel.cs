using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.UI.ImportHub;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Settings;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class SheetPreviewViewModelTests
{
    private readonly Mock<IStatementManager> _mockStatementManager;
    private readonly Mock<IApplicationBroker> _mockBroker;

    // Dependencies for the child AccountMappingViewModel
    private readonly Mock<IAccountService> _mockAccountService;
    private readonly Mock<IEntityService> _mockEntityService;
    private readonly Mock<IUserSettingsService> _mockUserSettingsService;

    private readonly Mock<IStatementEditSession> _mockEditSession;

    public SheetPreviewViewModelTests()
    {
        // Enforce the Conveyor Belt Bypass for headless testing
        ViewModelBase.IsTestEnvironment = true;

        _mockStatementManager = new Mock<IStatementManager>();
        _mockBroker = new Mock<IApplicationBroker>();
        _mockAccountService = new Mock<IAccountService>();
        _mockEntityService = new Mock<IEntityService>();
        _mockUserSettingsService = new Mock<IUserSettingsService>();
        _mockEditSession = new Mock<IStatementEditSession>();

        // Setup the Edit Session provider
        _mockStatementManager.Setup(m => m.GetEditSession(It.IsAny<Guid>())).Returns(_mockEditSession.Object);

        // Prevent NullReferenceExceptions from Debounce triggers in the child ViewModel
        _mockEntityService.Setup(s => s.SearchEntityNamesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _mockAccountService.Setup(s => s.SearchAccountNumbersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _mockAccountService.Setup(s => s.SearchCardNumbersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        // FIXED: Prevent NullReferenceException during AccountMapping.InitializeAsync()
        _mockAccountService.Setup(s => s.GetDistinctAccountTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _mockAccountService.Setup(s => s.GetDistinctCurrenciesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
        _mockUserSettingsService.Setup(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("USD");
    }

    private SheetPreviewViewModel CreateViewModel()
    {
        var accountMappingVM = new AccountMappingViewModel(
            _mockAccountService.Object,
            _mockEntityService.Object,
            _mockUserSettingsService.Object,
            _mockBroker.Object);

        return new SheetPreviewViewModel(
            _mockStatementManager.Object,
            _mockBroker.Object,
            accountMappingVM);
    }

    [Fact]
    public async Task LoadPreviewAsync_PopulatesState_AndEvaluatesAiTraining()
    {
        // =================================================================================
        // OBJECTIVE: Ensure the workbench correctly maps data and toggles the AI Training helper.
        // =================================================================================
        // Arrange
        var viewModel = CreateViewModel();
        var fileId = Guid.NewGuid();

        var preview = new StatementPreview
        {
            FileName = "Test.xlsx",
            HeaderRow = 1,
            ConfidenceScore = 75, // Below 80, should prevent confirmation
            Fields = new Dictionary<string, DetectedField>
            {
                { "Col:DATE", new DetectedField { ColumnName = "TXN_DATE", ColumnIndex = 0 } },
                // Notice we intentionally omit Meta:ENTITY_NAME to trigger the AI Training banner
            },
            RawHeaders = new List<string> { "TXN_DATE", "DESC" },
            PreviewTransactions = new List<TransactionPreview>
            {
                new TransactionPreview { Date = DateTime.UtcNow, Description = "Test", Amount = 10 }
            }
        };

        _mockStatementManager.Setup(m => m.PreviewStagedFileAsync(fileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preview);

        // Act
        await viewModel.LoadPreviewAsync(fileId, "Test.xlsx");

        // Assert
        Assert.Equal(75, viewModel.ConfidenceScore);
        Assert.Equal("TXN_DATE", viewModel.SelectedDateHeader);
        Assert.Equal(2, viewModel.AvailableHeaders.Count);
        Assert.Single(viewModel.TransactionRows);

        // Assert AI Training trigger logic
        Assert.True(viewModel.HasMissingMetadata); // Because Entity Name is missing
        Assert.True(viewModel.SuggestAiTraining); // User hasn't acknowledged it yet

        // Assert Validation logic
        Assert.False(viewModel.CanConfirmImport); // Confidence is < 80
    }

    [Fact]
    public void EvaluateMappingToggles_EnforcesMutualExclusivity()
    {
        // =================================================================================
        // OBJECTIVE: Validate the UI exclusivity logic. A user cannot map BOTH a single
        // Amount column AND separate Debit/Credit columns simultaneously.
        // =================================================================================
        // Arrange
        var viewModel = CreateViewModel();

        // Act 1: User selects an Amount header
        viewModel.SelectedAmountHeader = "Amount";

        // Assert 1: Split mapping (Debit/Credit) should be disabled
        Assert.True(viewModel.IsAmountMappingEnabled);
        Assert.False(viewModel.IsSplitMappingEnabled);

        // Act 2: User clears Amount and selects Debit
        viewModel.SelectedAmountHeader = string.Empty;
        viewModel.SelectedDebitHeader = "Withdrawals";

        // Assert 2: Amount mapping should be disabled
        Assert.True(viewModel.IsSplitMappingEnabled);
        Assert.False(viewModel.IsAmountMappingEnabled);
    }

    [Fact]
    public async Task ConfirmImportAsync_ProtectsLearningEngine_AndCommits()
    {
        // =================================================================================
        // OBJECTIVE: Validate the Anti-Poisoning AI Guardrails.
        // DECISION: Verify that structural labels (e.g., "Account Number:") are pushed
        // to the Edit Session using a -1 coordinate index to prevent coordinate binding.
        // =================================================================================
        // Arrange
        var viewModel = CreateViewModel();
        var fileId = Guid.NewGuid();

        // Load initial state to populate _currentFileId
        var preview = new StatementPreview { FileName = "Test.xlsx", Fields = new Dictionary<string, DetectedField>() };
        _mockStatementManager.Setup(m => m.PreviewStagedFileAsync(fileId, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(preview);
        await viewModel.LoadPreviewAsync(fileId, "Test.xlsx");

        // Simulate user typing structural labels into the AI Training textboxes
        viewModel.SelectedEntityNameLabel = "Customer Name:";
        viewModel.SelectedAccountNumberLabel = "A/C No.";

        _mockEditSession.Setup(s => s.ConfirmAndPrepareForImport()).Returns(new PreviewTracker { FinalPreview = new StatementPreview() });

        // Act
        await viewModel.ConfirmImportAsync();

        // Assert 1: Anti-Poisoning Guardrail (Index -1, Category "METADATA")
        _mockEditSession.Verify(s => s.UpdateColumnMapping("Meta:ENTITY_NAME", "Customer Name:", -1, "METADATA"), Times.Once);
        _mockEditSession.Verify(s => s.UpdateColumnMapping("Meta:ACCOUNT_NUMBER", "A/C No.", -1, "METADATA"), Times.Once);

        // Assert 2: Orchestrator commit called
        _mockStatementManager.Verify(m => m.CommitStagedBatchAsync(fileId, It.IsAny<PreviewTracker>(), It.IsAny<CancellationToken>()), Times.Once);

        // Assert 3: Broker routes user back to the Queue view
        _mockBroker.Verify(b => b.Send(It.IsAny<ReturnToQueueMessage>()), Times.Once);
    }

    [Fact]
    public async Task RequestDiscardPreview_WhenConfirmed_AbandonsAndReturnsToQueue()
    {
        // =================================================================================
        // OBJECTIVE: Validate the Global Dialog Router integration.
        // DECISION: Simulate the user clicking "Yes" on the confirmation modal.
        // =================================================================================
        // Arrange
        var viewModel = CreateViewModel();

        // Use CompletionSource instead of ConfirmationTask
        _mockBroker.Setup(b => b.Send(It.IsAny<ShowConfirmationMessage>()))
            .Callback<ShowConfirmationMessage>(msg =>
            {
                msg.CompletionSource.SetResult(true);
            });

        // Act
        await viewModel.RequestDiscardPreview();

        // Assert
        // Verify state is cleared (SuggestAiTraining resets to false)
        Assert.False(viewModel.SuggestAiTraining);

        // Verify navigation back to the queue
        _mockBroker.Verify(b => b.Send(It.IsAny<ReturnToQueueMessage>()), Times.Once);
    }
}