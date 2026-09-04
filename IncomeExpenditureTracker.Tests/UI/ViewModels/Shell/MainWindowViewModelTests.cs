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

        public MainWindowViewModelTests()
        {
            // Bypass the Avalonia UI Thread for headless testing
            ViewModelBase.IsTestEnvironment = true;

            _mockBroker = new Mock<IApplicationBroker>();
            _mockServiceProvider = new Mock<IServiceProvider>();

            // Setup ServiceProvider for legitimate ViewModel resolution
            var mockLoginSvc = new Mock<IProfileLoginService>();
            var mockRegistrySvc = new Mock<IProfileRegistry>();
            var mockHasher = new Mock<IPasswordHasher>();
            var mockUserSettingsSvc = new Mock<IUserSettingsService>();

            var loginVm = new LoginViewModel(_mockBroker.Object, mockLoginSvc.Object, mockRegistrySvc.Object, mockHasher.Object);
            var registerVm = new RegisterViewModel(mockRegistrySvc.Object, mockHasher.Object, mockLoginSvc.Object, mockUserSettingsSvc.Object, _mockBroker.Object);

            _mockServiceProvider.Setup(sp => sp.GetService(typeof(LoginViewModel))).Returns(loginVm);
            _mockServiceProvider.Setup(sp => sp.GetService(typeof(RegisterViewModel))).Returns(registerVm);

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
            // Act
            var viewModel = CreateViewModel();

            // Assert
            Assert.IsType<LoginViewModel>(viewModel.CurrentView);
            Assert.False(viewModel.IsMenuVisible);
            Assert.True(viewModel.IsWelcomeMessageVisible);
        }

        [Fact]
        public void OnNavigationRequested_RegisterRoute_HidesMenu_ShowsWelcomeMessage()
        {
            // Arrange
            var viewModel = CreateViewModel();

            // Act
            _navigationHandler?.Invoke(new NavigationMessage("Register"));

            // Assert
            Assert.IsType<RegisterViewModel>(viewModel.CurrentView);
            Assert.False(viewModel.IsMenuVisible);
            Assert.True(viewModel.IsWelcomeMessageVisible);
        }

        [Fact]
        public void OnNavigationRequested_UnknownRoute_ThrowsArgumentException()
        {
            // Arrange
            var viewModel = CreateViewModel();

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() => _navigationHandler?.Invoke(new NavigationMessage("UnknownRoute")));
            Assert.Contains("Unknown route: UnknownRoute", exception.Message);

            // Verifying the UI toggle triggered right before the exception
            Assert.True(viewModel.IsMenuVisible);
            Assert.False(viewModel.IsWelcomeMessageVisible);
        }

        [Fact]
        public void OnShowHelperRequested_SetsDialogState_ForHelper()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var message = new ShowHelperMessage("Help Title", "Help Body", true, null, null, true);

            // Act
            _showHelperHandler?.Invoke(message);

            // Assert
            Assert.True(viewModel.IsDialogVisible);
            Assert.Equal("Help Title", viewModel.DialogTitle);
            Assert.Equal("Help Body", viewModel.DialogBody);
            Assert.False(viewModel.IsConfirmationDialog);
            Assert.True(viewModel.IsCopyButtonVisible);
        }

        [Fact]
        public async Task OnShowConfirmationRequested_SetsDialogState_And_ConfirmDialog_ResolvesTask()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var tcs = new TaskCompletionSource<bool>();
            var message = new ShowConfirmationMessage("Confirm?", "Are you sure?", tcs);

            // Act
            _showConfirmationHandler?.Invoke(message);

            // Assert Initial State
            Assert.True(viewModel.IsDialogVisible);
            Assert.True(viewModel.IsConfirmationDialog);
            Assert.False(viewModel.IsCopyButtonVisible);

            // Act - Confirm
            viewModel.ConfirmDialogCommand.Execute(null);

            // Assert Resolution
            Assert.False(viewModel.IsDialogVisible);
            Assert.True(tcs.Task.IsCompletedSuccessfully);
            Assert.True(await tcs.Task);
        }

        [Fact]
        public async Task CancelDialog_ConfirmationType_ResolvesTask_ToFalse()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var tcs = new TaskCompletionSource<bool>();
            var message = new ShowConfirmationMessage("Warning", "Go back?", tcs);

            // Act
            _showConfirmationHandler?.Invoke(message);
            viewModel.CancelDialogCommand.Execute(null);

            // Assert
            Assert.False(viewModel.IsDialogVisible);
            Assert.True(tcs.Task.IsCompletedSuccessfully);
            Assert.False(await tcs.Task);
        }

        [Fact]
        public void OnProgressReceived_UpdatesProgressState()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var message = new StagingProgressMessage(45, "Processing records...");

            // Act
            _progressHandler?.Invoke(message);

            // Assert
            Assert.Equal(45, viewModel.LoadingPercentage);
            Assert.Equal("Processing records...", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnFileStagingErrorReceived_AddsErrorToast()
        {
            // Arrange
            var viewModel = CreateViewModel();

            // Create the error details matching the updated constructor signature
            var errorDetails = new FileStagingError(
                fileId: Guid.NewGuid(),
                fileName: "data.csv",
                severity: ErrorSeverity.Fatal, // Adjust this enum value if your definition uses 'Critical' or 'High'
                message: "Invalid format."
            );

            var message = new FileStagingErrorMessage(errorDetails);

            // Act
            _fileStagingErrorHandler?.Invoke(message);

            // Assert
            Assert.Single(viewModel.Toasts);

            // Verify the toast message contains both the filename and the error string we provided
            Assert.Contains("data.csv", viewModel.Toasts.First().Message);
            Assert.Contains("Invalid format.", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Error, viewModel.Toasts.First().Type);
        }

        [Fact]
        public void OnStagingCompleted_WithSuccess_AddsSuccessToast()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var message = new StagingBatchCompletedMessage(5, 0);

            // Act
            _stagingBatchCompletedHandler?.Invoke(message);

            // Assert
            Assert.Single(viewModel.Toasts);
            Assert.Contains("Successfully staged 5", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Success, viewModel.Toasts.First().Type);
            Assert.Equal("Staging Complete!", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnStagingCompleted_ZeroSuccess_DoesNotAddToast_UpdatesStatus()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var message = new StagingBatchCompletedMessage(0, 0);

            // Act
            _stagingBatchCompletedHandler?.Invoke(message);

            // Assert
            Assert.Empty(viewModel.Toasts);
            Assert.Equal("Staging Complete!", viewModel.LoadingStatus);
        }

        [Fact]
        public void OnToastReceived_AddsToastToCollection()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var message = new ToastNotificationMessage("Database backed up!", NotificationType.Success);

            // Act
            _toastHandler?.Invoke(message);

            // Assert
            Assert.Single(viewModel.Toasts);
            Assert.Equal("Database backed up!", viewModel.Toasts.First().Message);
            Assert.Equal(NotificationType.Success, viewModel.Toasts.First().Type);
        }

        [Fact]
        public void DismissToastCommand_ValidId_RemovesSpecificToast()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var toast1 = new ToastAlert("Error 1", NotificationType.Error);
            var toast2 = new ToastAlert("Error 2", NotificationType.Error);

            viewModel.Toasts.Add(toast1);
            viewModel.Toasts.Add(toast2);

            // Act
            viewModel.DismissToastCommand.Execute(toast1.Id);

            // Assert
            Assert.Single(viewModel.Toasts);
            Assert.Equal(toast2.Id, viewModel.Toasts.First().Id);
        }

        [Fact]
        public void DismissToastCommand_InvalidId_DoesNothing()
        {
            // Arrange
            var viewModel = CreateViewModel();
            var toast = new ToastAlert("Test", NotificationType.Info);
            viewModel.Toasts.Add(toast);

            // Act - Try dismissing with an entirely random, unmatched GUID
            viewModel.DismissToastCommand.Execute(Guid.NewGuid());

            // Assert - State should be unmodified
            Assert.Single(viewModel.Toasts);
            Assert.Equal(toast.Id, viewModel.Toasts.First().Id);
        }
    }
}