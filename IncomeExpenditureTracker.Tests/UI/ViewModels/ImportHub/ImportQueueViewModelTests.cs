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

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class ImportQueueViewModelTests
{
    private readonly Mock<IStatementManager> _mockStatementManager;
    private readonly Mock<IApplicationBroker> _mockBroker;

    public ImportQueueViewModelTests()
    {
        // Bypass the Avalonia Dispatcher lock for headless testing
        ViewModelBase.IsTestEnvironment = true;

        _mockStatementManager = new Mock<IStatementManager>();
        _mockBroker = new Mock<IApplicationBroker>();

        // Fix for the failing test: Set up the default constructor behavior
        _mockStatementManager.Setup(m => m.HasStagedFiles).Returns(false);
    }

    [Fact]
    public void Constructor_WithStagedFiles_RestoresStateForZeroStateLossNavigation()
    {
        // =================================================================================
        // OBJECTIVE: Test Zero-State Loss Navigation (SPA backwards routing).
        // =================================================================================
        // Arrange
        _mockStatementManager.Setup(m => m.HasStagedFiles).Returns(true);
        _mockStatementManager.Setup(m => m.GetStagedFiles()).Returns(new List<PendingFilePreview>
        {
            new PendingFilePreview(Guid.NewGuid(), "RestoredFile.xlsx", new List<string>())
        });

        // Act
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);

        // Assert
        Assert.Single(viewModel.StagedFiles);
        Assert.Equal("RestoredFile.xlsx", viewModel.StagedFiles.First().FileName);

        _mockStatementManager.Verify(m => m.HasStagedFiles, Times.Once);
        _mockStatementManager.Verify(m => m.GetStagedFiles(), Times.Once);
    }

    [Fact]
    public async Task HandleDroppedFilesAsync_MoreThan5Files_RejectsAndSendsWarning()
    {
        // Arrange
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);
        var files = new List<string> { "1.xlsx", "2.xlsx", "3.xlsx", "4.xlsx", "5.xlsx", "6.xlsx" };

        // Act
        await viewModel.HandleDroppedFilesAsync(files);

        // Assert
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m =>
            m.Type == NotificationType.Warning && m.Message.Contains("Maximum of 5 files"))), Times.Once);

        // Verify the orchestrator was never called
        _mockStatementManager.Verify(m => m.StageFilesAsync(It.IsAny<List<string>>(), It.IsAny<IProgress<LoadingProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDroppedFilesAsync_InvalidExtension_RejectsAndSendsError()
    {
        // Arrange
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);
        viewModel.IsExcelSelected = true; // Expecting .xlsx

        var files = new List<string> { "Statement.pdf" };

        // Act
        await viewModel.HandleDroppedFilesAsync(files);

        // Assert
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m =>
            m.Type == NotificationType.Error && m.Message.Contains(".xlsx files are allowed"))), Times.Once);

        _mockStatementManager.Verify(m => m.StageFilesAsync(It.IsAny<List<string>>(), It.IsAny<IProgress<LoadingProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDroppedFilesAsync_ValidFiles_StagesFilesAndUpdatesStagedFilesList()
    {
        // Arrange
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);
        var files = new List<string> { "Bank_Jan.xlsx" };
        var fileId = Guid.NewGuid();

        var stagingResult = new StagingBatchResult
        {
            Successes = new List<PendingFilePreview> { new PendingFilePreview(fileId, "Bank_Jan.xlsx", new List<string>()) },
            Failures = new List<FileStagingError>()
        };

        _mockStatementManager.Setup(m => m.StageFilesAsync(files, It.IsAny<IProgress<LoadingProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stagingResult);

        // Act
        await viewModel.HandleDroppedFilesAsync(files);

        // Assert
        Assert.Single(viewModel.StagedFiles);
        Assert.Equal(fileId, viewModel.StagedFiles.First().Id);
        Assert.False(viewModel.IsLoading); // Ensures finally block ran
    }

    [Fact]
    public void SelectSheet_BroadcastsPreviewMessage_AvoidingGlobalRouterCrash()
    {
        // =================================================================================
        // OBJECTIVE: Test safe sub-routing inside the ImportHub to avoid MainWindow ViewModel crashes.
        // =================================================================================
        // Arrange
        _mockStatementManager.Setup(m => m.HasStagedFiles).Returns(false);
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);

        var fileId = Guid.NewGuid();
        viewModel.StagedFiles.Add(new PendingFilePreview(fileId, "Valid.xlsx", new List<string>()));

        var parameter = (fileId, "Sheet1");

        // Act
        viewModel.SelectSheetCommand.Execute(parameter);

        // Assert: Must fire PreviewSheetMessage, not NavigationMessage
        _mockBroker.Verify(b => b.Send(It.Is<PreviewSheetMessage>(m =>
            m.FileId == fileId &&
            m.TargetSheetName == "Sheet1" &&
            m.FileName == "Valid.xlsx")), Times.Once);
    }

    [Fact]
    public void DeleteFile_RemovesFileFromQueue_AndDiscardsOSLock()
    {
        // Arrange
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);
        var fileId = Guid.NewGuid();
        viewModel.StagedFiles.Add(new PendingFilePreview(fileId, "ToTrash.xlsx", new List<string>()));

        // Act
        viewModel.DeleteFileCommand.Execute(fileId);

        // Assert
        _mockStatementManager.Verify(m => m.DiscardFile(fileId), Times.Once);
        Assert.Empty(viewModel.StagedFiles);
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m => m.Type == NotificationType.Info)), Times.Once);
    }
}