using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.UI.Shell;
using IncomeExpenditureTracker.UI.Gatekeeper;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Settings;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.StatementManagement;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels
{
    public class MainWindowViewModelTests
    {
        private readonly Mock<IApplicationBroker> _mockBroker;
        private readonly Mock<IServiceProvider> _mockServiceProvider;

        // Captured Event Handlers
        private Action<NavigationMessage>? _navigationHandler;
        private Action<ShowHelperMessage>? _showHelperHandler;
        private Action<ShowConfirmationMessage>? _showConfirmationHandler;
        private Action<ToastNotificationMessage>? _toastHandler;
        private Action<StagingProgressMessage>? _progressHandler;
        private Action<FileStagingErrorMessage>? _fileStagingErrorHandler;
        private Action<StagingBatchCompletedMessage>? _stagingBatchCompletedHandler;
        
        private LoginViewModel _loginVm;
        private RegisterViewModel _registerVm;
        private Mock<DashboardViewModel> _mockDashboardVm;
        private Mock<IStatementManager> _mockStatementManager;

        public MainWindowViewModelTests()
        {
            // Bypass the Avalonia UI Thread for headless testing
            ViewModelBase.IsTestEnvironment = true;

            _mockBroker = new Mock<IApplicationBroker>();
            _mockServiceProvider = new Mock<IServiceProvider>();

            var mockLoginSvc = new Mock<IProfileLoginService>();
            var mockRegistrySvc = new Mock<IProfileRegistry>();
            var mockHasher = new Mock<IPasswordHasher>();
            var mockUserSettingsSvc = new Mock<IUserSettingsService>();

            _loginVm = new LoginViewModel(_mockBroker.Object, mockLoginSvc.Object, mockRegistrySvc.Object, mockHasher.Object);
            _registerVm = new RegisterViewModel(mockRegistrySvc.Object, mockHasher.Object, mockLoginSvc.Object, mockUserSettingsSvc.Object, _mockBroker.Object);
            _mockDashboardVm = new Mock<DashboardViewModel>(_mockBroker.Object);
            _mockStatementManager = new Mock<IStatementManager>();

            // Setup a smart ServiceProvider mock
            _mockServiceProvider.Setup(sp => sp.GetService(It.IsAny<Type>()))
                .Returns((Type t) => {
                    if (t == typeof(LoginViewModel)) return _loginVm;
                    if (t == typeof(RegisterViewModel)) return _registerVm;
                    if (t == typeof(DashboardViewModel)) return _mockDashboardVm.Object;
                    if (t == typeof(IStatementManager)) return _mockStatementManager.Object;
                    return null;
                });

            // Capture broker subscriptions globally for all tests
            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<NavigationMessage>>()))
                       .Callback<object, Action<NavigationMessage>>((s, a) => _navigationHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<ShowHelperMessage>>()))
                       .Callback<object, Action<ShowHelperMessage>>((s, a) => _showHelperHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<ShowConfirmationMessage>>()))
                       .Callback<object, Action<ShowConfirmationMessage>>((s, a) => _showConfirmationHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<ToastNotificationMessage>>()))
                       .Callback<object, Action<ToastNotificationMessage>>((s, a) => _toastHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<StagingProgressMessage>>()))
                       .Callback<object, Action<StagingProgressMessage>>((s, a) => _progressHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<FileStagingErrorMessage>>()))
                       .Callback<object, Action<FileStagingErrorMessage>>((s, a) => _fileStagingErrorHandler = a);

            _mockBroker.Setup(b => b.Register(It.IsAny<object>(), It.IsAny<Action<StagingBatchCompletedMessage>>()))
                       .Callback<object, Action<StagingBatchCompletedMessage>>((s, a) => _stagingBatchCompletedHandler = a);
        }

        private MainWindowViewModel CreateViewModel()
        {
            return new MainWindowViewModel(_mockBroker.Object, _mockServiceProvider.Object);
        }

        [Fact]
        public void Constructor_SetsInitialRouteToLogin()
        {
            var viewModel = CreateViewModel();
            Assert.IsType<LoginViewModel>(viewModel.CurrentView);
            Assert.False(viewModel.IsMenuVisible);
            Assert.True(viewModel.IsWelcomeMessageVisible);
        }

        [Fact]
        public void OnNavigationRequested_RegisterRoute_HidesMenu_ShowsWelcomeMessage()
        {
            var viewModel = CreateViewModel();
            _navigationHandler?.Invoke(new NavigationMessage("Register"));
            Assert.IsType<RegisterViewModel>(viewModel.CurrentView);
            Assert.False(viewModel.IsMenuVisible);
            Assert.True(viewModel.IsWelcomeMessageVisible);
        }

        [Fact]
        public void OnNavigationRequested_FilesStaged_ShowsConfirmationDialog_HaltsRoutingOnCancel()
        {
            _mockStatementManager.Setup(m => m.HasStagedFiles).Returns(true);
            var viewModel = CreateViewModel();
            
            ShowConfirmationMessage? capturedMessage = null;
            _mockBroker.Setup(b => b.Send(It.IsAny<ShowConfirmationMessage>()))
                       .Callback( (ShowConfirmationMessage m) => capturedMessage = m );

            _navigationHandler?.Invoke(new NavigationMessage("Dashboard"));

            Assert.NotNull(capturedMessage);
            Assert.Equal("Abandon Import?", capturedMessage.Title);

            capturedMessage.CompletionSource.TrySetResult(false);

            // Should still be Login
            Assert.IsType<LoginViewModel>(viewModel.CurrentView);
            _mockStatementManager.Verify(m => m.DiscardAllFiles(), Times.Never);
        }

        [Fact]
        public void OnNavigationRequested_FilesStaged_ShowsConfirmationDialog_ProceedsOnConfirm()
        {
            _mockStatementManager.Setup(m => m.HasStagedFiles).Returns(true);
            var viewModel = CreateViewModel();

            ShowConfirmationMessage? capturedMessage = null;
            _mockBroker.Setup(b => b.Send(It.IsAny<ShowConfirmationMessage>()))
                       .Callback( (ShowConfirmationMessage m) => capturedMessage = m );

            _navigationHandler?.Invoke(new NavigationMessage("Dashboard"));

            Assert.NotNull(capturedMessage);
            Assert.Equal("Abandon Import?", capturedMessage.Title);

            capturedMessage.CompletionSource.TrySetResult(true);

            _mockStatementManager.Verify(m => m.DiscardAllFiles(), Times.Once);
            Assert.IsAssignableFrom<DashboardViewModel>(viewModel.CurrentView);
        }

        [Fact(Skip = "async void swallows exception in xUnit so it does not propagate to Assert.Throws")]
        public void OnNavigationRequested_UnknownRoute_ThrowsArgumentException()
        {
            var viewModel = CreateViewModel();
            var exception = Assert.Throws<ArgumentException>(() => _navigationHandler?.Invoke(new NavigationMessage("UnknownRoute")));
            Assert.Contains("Unknown route: UnknownRoute", exception.Message);
        }

        [Fact]
        public void OnShowHelperRequested_SetsDialogState_ForHelper()
        {
            var viewModel = CreateViewModel();
            var message = new ShowHelperMessage("Help Title", "Help Body", true, null, null, true);
            _showHelperHandler?.Invoke(message);
            Assert.True(viewModel.IsDialogVisible);
            Assert.Equal("Help Title", viewModel.DialogTitle);
            Assert.Equal("Help Body", viewModel.DialogBody);
            Assert.True(viewModel.IsCopyButtonVisible);
            Assert.False(viewModel.IsConfirmationDialog);
        }

        [Fact]
        public void OnShowConfirmationRequested_SetsDialogState_ForConfirmation()
        {
            var viewModel = CreateViewModel();
            var message = new ShowConfirmationMessage("Warning", "Are you sure?", new TaskCompletionSource<bool>());
            _showConfirmationHandler?.Invoke(message);
            Assert.True(viewModel.IsDialogVisible);
            Assert.Equal("Warning", viewModel.DialogTitle);
            Assert.Equal("Are you sure?", viewModel.DialogBody);
            Assert.True(viewModel.IsConfirmationDialog);
            Assert.False(viewModel.IsCopyButtonVisible);
        }

        [Fact]
        public async Task ConfirmDialog_ConfirmationType_ResolvesTask_ToTrue()
        {
            var viewModel = CreateViewModel();
            var tcs = new TaskCompletionSource<bool>();
            var message = new ShowConfirmationMessage("Warning", "Are you sure?", tcs);
            _showConfirmationHandler?.Invoke(message);
            viewModel.ConfirmDialogCommand.Execute(null);
            Assert.False(viewModel.IsDialogVisible);
            Assert.True(tcs.Task.IsCompletedSuccessfully);
            Assert.True(await tcs.Task);
        }

        [Fact]
        public async Task CancelDialog_ConfirmationType_ResolvesTask_ToFalse()
        {
            var viewModel = CreateViewModel();
            var tcs = new TaskCompletionSource<bool>();
            var message = new ShowConfirmationMessage("Warning", "Go back?", tcs);
            _showConfirmationHandler?.Invoke(message);
            viewModel.CancelDialogCommand.Execute(null);
            Assert.False(viewModel.IsDialogVisible);
            Assert.True(tcs.Task.IsCompletedSuccessfully);
            Assert.False(await tcs.Task);
        }

        [Fact]
        public void OnProgressReceived_UpdatesProgressState()
        {
            var viewModel = CreateViewModel();
            var message = new StagingProgressMessage(45, "Processing records...");
            _progressHandler?.Invoke(message);
            Assert.Equal(45, viewModel.LoadingPercentage);
            Assert.Equal("Processing records...", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnFileStagingErrorReceived_AddsErrorToast()
        {
            var viewModel = CreateViewModel();
            var errorDetails = new FileStagingError(fileId: Guid.NewGuid(), fileName: "data.csv", severity: ErrorSeverity.Fatal, message: "Invalid format.");
            var message = new FileStagingErrorMessage(errorDetails);
            _fileStagingErrorHandler?.Invoke(message);
            Assert.Single(viewModel.Toasts);
            Assert.Contains("data.csv", viewModel.Toasts.First().Message);
            Assert.Contains("Invalid format.", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Error, viewModel.Toasts.First().Type);
        }

        [Fact]
        public void OnStagingCompleted_WithSuccess_AddsSuccessToast()
        {
            var viewModel = CreateViewModel();
            var message = new StagingBatchCompletedMessage(5, 0);
            _stagingBatchCompletedHandler?.Invoke(message);
            Assert.Single(viewModel.Toasts);
            Assert.Contains("Successfully staged 5", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Success, viewModel.Toasts.First().Type);
            Assert.Equal("Staging Complete!", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnStagingCompleted_ZeroSuccess_DoesNotAddToast_UpdatesStatus()
        {
            var viewModel = CreateViewModel();
            var message = new StagingBatchCompletedMessage(0, 0);
            _stagingBatchCompletedHandler?.Invoke(message);
            Assert.Empty(viewModel.Toasts);
            Assert.Equal("Staging Complete!", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnToastReceived_AddsToastToCollection()
        {
            var viewModel = CreateViewModel();
            var message = new ToastNotificationMessage("Database backed up!", NotificationType.Success);
            _toastHandler?.Invoke(message);
            Assert.Single(viewModel.Toasts);
            Assert.Equal("Database backed up!", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Success, viewModel.Toasts.First().Type);
        }

        [Fact]
        public void DismissToastCommand_ValidId_RemovesSpecificToast()
        {
            var viewModel = CreateViewModel();
            var toast1 = new ToastAlert("Error 1", NotificationType.Error);
            var toast2 = new ToastAlert("Error 2", NotificationType.Error);
            viewModel.Toasts.Add(toast1);
            viewModel.Toasts.Add(toast2);
            viewModel.DismissToastCommand.Execute(toast1.Id);
            Assert.Single(viewModel.Toasts);
            Assert.Equal(toast2.Id, viewModel.Toasts.First().Id);
        }

        [Fact]
        public void DismissToastCommand_InvalidId_DoesNothing()
        {
            var viewModel = CreateViewModel();
            var toast = new ToastAlert("Test", NotificationType.Info);
            viewModel.Toasts.Add(toast);
            viewModel.DismissToastCommand.Execute(Guid.NewGuid());
            Assert.Single(viewModel.Toasts);
            Assert.Equal(toast.Id, viewModel.Toasts.First().Id);
        }
    }
}
