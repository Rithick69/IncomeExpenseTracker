using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls.ApplicationLifetimes;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input; // Needed for [RelayCommand]
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.UI.Gatekeeper;

// =========================================================================
// ARCHITECTURAL NOTE: Why 'partial'?
// =========================================================================
// When you use [ObservableProperty], the MVVM Toolkit Source Generators
// write boilerplate code in a hidden background file to wire up the UI bindings.
// Your class must be 'partial' so C# can stitch this file and the hidden
// generated file together during compilation.
// =========================================================================

namespace IncomeExpenditureTracker.UI.Shell
{
    /// <summary>
    /// The root context of the application.
    /// Acts as the Global Router, Dialog Controller, Global Notification Hub, listening to the Broker for any
    /// ToastNotificationMessage sent by any service or ViewModel.
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        private readonly IServiceProvider _serviceProvider;

        // =========================================================================
        // ROUTING STATE (The ContentControl binding)
        // =========================================================================
        // @desc    Acts as our React Router <Outlet /> state.
        //          Holds the current child ViewModel (e.g., LoginViewModel, MasterDataViewModel)
        // @state   ViewModelBase
        [ObservableProperty]
        private ViewModelBase? _currentView;

        // =========================================================================
        // GLOBAL DIALOG STATE (The Modal Overlay)
        // =========================================================================
        [ObservableProperty]
        private bool _isDialogVisible;

        [ObservableProperty]
        private string _dialogTitle = string.Empty;

        [ObservableProperty]
        private string _dialogBody = string.Empty;

        [ObservableProperty]
        private bool _isConfirmationDialog;

        private TaskCompletionSource<bool>? _currentDialogTcs;

        // =========================================================================
        // TOAST & PROGRESS STATE (The Status Tray)
        // =========================================================================

        // 1. The Visual Toast Stack
        // The Avalonia ItemsControl binds directly to this.
        // When items are added/removed here, they slide in and out of the screen.
        public ObservableCollection<ToastAlert> Toasts { get; } = new();

        // 2. Global Progress Bar State
        // Bound to a progress bar at the bottom of the main window for background tasks
        [ObservableProperty]
        private int _loadingPercentage;

        [ObservableProperty]
        private string _loadingStatus = "Ready";

        // =========================================================================
        // SHELL LAYOUT STATE (Menu vs. Gatekeeper Messages)
        // =========================================================================
        [ObservableProperty]
        private bool _isMenuVisible;

        [ObservableProperty]
        private bool _isWelcomeMessageVisible;

        [ObservableProperty]
        private string _welcomeMessageText = "Welcome to the Tracker. Please authenticate to continue."; // Your generic message

        [ObservableProperty]
        private bool _isCopyButtonVisible;

        // =========================================================================
        // CONSTRUCTOR
        // =========================================================================
        public MainWindowViewModel(IApplicationBroker broker, IServiceProvider serviceProvider)
            : base(broker)
        {
            _serviceProvider = serviceProvider;

            // 1. Subscribe to Routing
            Broker.Register<NavigationMessage>(this, OnNavigationRequested);

            // 2. Subscribe to Global Dialogs
            Broker.Register<ShowHelperMessage>(this, OnShowHelperRequested);
            Broker.Register<ShowConfirmationMessage>(this, OnShowConfirmationRequested);

            // 3. Subscribe to Toasts & Progress
            Broker.Register<ToastNotificationMessage>(this, OnToastReceived);
            Broker.Register<StagingProgressMessage>(this, OnProgressReceived);
            Broker.Register<FileStagingErrorMessage>(this, OnFileStagingErrorReceived);
            Broker.Register<StagingBatchCompletedMessage>(this, OnStagingCompleted);

            // 4. Initial Route
            NavigateTo("Login");
        }

        // =========================================================================
        // EVENT HANDLERS (The Reactive Magic)
        // =========================================================================

        private void OnNavigationRequested(NavigationMessage message)
        {
            NavigateTo(message.Destination);
        }

        private void NavigateTo(string destination)
        {
            RunOnUIThread(() =>
            {
                // 1. EVALUATE THE ZONE
                // If we are routing to Login or Register, hide the menu and show the generic message.
                if (destination == "Login" || destination == "Register")
                {
                    IsMenuVisible = false;
                    IsWelcomeMessageVisible = true;
                }
                else
                {
                    // For Dashboard, Settings, etc. - show the menu, hide the welcome message.
                    IsMenuVisible = true;
                    IsWelcomeMessageVisible = false;
                }

                // CRITICAL: Utilizing _serviceProvider requests a brand new
                // Transient instance from the DI container, destroying stale state.
                CurrentView = destination switch
                {
                    "Login" => _serviceProvider.GetRequiredService<LoginViewModel>(),
                    "Register" => _serviceProvider.GetRequiredService<RegisterViewModel>(),
                    // "Dashboard" => _serviceProvider.GetRequiredService<DashboardViewModel>(),
                    _ => throw new ArgumentException($"Unknown route: {destination}")
                };
            });
        }

        // =========================================================================
        // DIALOG LOGIC
        // =========================================================================
        private void OnShowHelperRequested(ShowHelperMessage message)
        {
            RunOnUIThread(() =>
            {
                DialogTitle = message.Title;
                DialogBody = message.Body;
                // Capture the TCS so the Confirm button can resolve it
                _currentDialogTcs = message.CompletionSource;
                IsCopyButtonVisible = message.ShowCopyButton;
                IsConfirmationDialog = false;
                IsDialogVisible = true;
            });
        }

        private void OnShowConfirmationRequested(ShowConfirmationMessage message)
        {
            RunOnUIThread(() =>
            {
                DialogTitle = message.Title;
                DialogBody = message.Body;
                _currentDialogTcs = message.CompletionSource;
                IsConfirmationDialog = true;
                IsCopyButtonVisible = false;
                IsDialogVisible = true;
            });
        }

        [RelayCommand]
        public void ConfirmDialog()
        {
            IsDialogVisible = false;
            if (IsConfirmationDialog && _currentDialogTcs != null)
            {
                _currentDialogTcs.TrySetResult(true);
                _currentDialogTcs = null;
            }
        }

        [RelayCommand]
        public void CancelDialog()
        {
            IsDialogVisible = false;
            if (IsConfirmationDialog && _currentDialogTcs != null)
            {
                _currentDialogTcs.TrySetResult(false);
                _currentDialogTcs = null;
            }
        }

        // =========================================================================
        // TOAST & PROGRESS LOGIC
        // =========================================================================

        // Copy Command
        [RelayCommand]
        public async Task CopyToClipboardAsync()
        {
            // Access the Avalonia System Clipboard
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = desktop.MainWindow?.Clipboard;
                if (clipboard != null)
                {
                    await clipboard.SetTextAsync(DialogBody);

                    // Fire a toast so the user knows it worked!
                    Broker.Send(new ToastNotificationMessage("Copied to clipboard!", NotificationType.Success));
                }
            }
        }

        private void OnProgressReceived(StagingProgressMessage message)
        {
            // Using our test-safe Airlock from ViewModelBase!
            RunOnUIThread(() =>
            {
                // Instantly animates the Avalonia Progress Bar
                LoadingPercentage = message.Percentage;
                LoadingStatus = message.StatusMessage;
            });
        }

        private void OnFileStagingErrorReceived(FileStagingErrorMessage message)
        {
            // Translating the specific staging error into our global Toast system
            RunOnUIThread(() =>
            {
                var alert = new ToastAlert(
                    $"❌ Staging Failed: {message.Error.FileName}\n{message.Error.Message}",
                    NotificationType.Error);

                Toasts.Add(alert);
                _ = RemoveToastAfterDelayAsync(alert.Id, TimeSpan.FromSeconds(8)); // Show errors slightly longer
            });
        }

        private void OnStagingCompleted(StagingBatchCompletedMessage message)
        {
            RunOnUIThread(() =>
            {
                if (message.TotalSuccess > 0)
                {
                    var alert = new ToastAlert(
                        $"✅ Successfully staged {message.TotalSuccess} file(s).",
                        NotificationType.Success);

                    Toasts.Add(alert);
                    _ = RemoveToastAfterDelayAsync(alert.Id, TimeSpan.FromSeconds(5));
                }

                LoadingStatus = "Staging Complete!";
            });
        }

        private void OnToastReceived(ToastNotificationMessage message)
        {
            RunOnUIThread(() =>
            {
                var alert = new ToastAlert(message.Message, message.Type);
                Toasts.Add(alert);

                // Fire and forget the auto-cleanup timer
                _ = RemoveToastAfterDelayAsync(alert.Id, TimeSpan.FromSeconds(5));
            });
        }

        // =========================================================================
        // COMMANDS & BACKGROUND TASKS
        // =========================================================================

        /// <summary>
        /// Bound to the "X" button on each individual Toast in the UI.
        /// </summary>
        [RelayCommand]
        public void DismissToast(Guid toastId)
        {
            var toast = Toasts.FirstOrDefault(t => t.Id == toastId);
            if (toast != null)
            {
                Toasts.Remove(toast);
            }
        }

        private async Task RemoveToastAfterDelayAsync(Guid toastId, TimeSpan delay)
        {
            // Wait silently in the background without freezing the UI
            await Task.Delay(delay);

            // Marshal back to the UI thread to safely modify the ObservableCollection
            RunOnUIThread(() => DismissToast(toastId));
        }
    }
}