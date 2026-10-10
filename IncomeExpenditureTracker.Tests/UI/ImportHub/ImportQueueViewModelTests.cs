using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.UI.ImportHub.ViewModels;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.Tests.UI.ImportHub;

public class ImportQueueViewModelTests
{
    private readonly Mock<IStatementManager> _mockStatementManager;
    private readonly Mock<IApplicationBroker> _mockBroker;

    public ImportQueueViewModelTests()
    {
        ViewModelBase.IsTestEnvironment = true;
        _mockStatementManager = new Mock<IStatementManager>();
        _mockBroker = new Mock<IApplicationBroker>();
    }

    [Fact]
    public async Task HandleDroppedFilesAsync_MoreThan5Files_RejectsAndSendsWarning()
    {
        // Arrange
        var viewModel = new ImportQueueViewModel(_mockStatementManager.Object, _mockBroker.Object);
        var files = new List<string> { "1.csv", "2.csv", "3.csv", "4.csv", "5.csv", "6.csv" };

        // Act
        await viewModel.HandleDroppedFilesAsync(files);

        // Assert
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m =>
            m.Type == NotificationType.Warning && m.Message.Contains("Maximum of 5 files"))), Times.Once);
        _mockStatementManager.VerifyNoOtherCalls();
    }
}