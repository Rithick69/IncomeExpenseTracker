using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input; // Needed for [RelayCommand]
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;

// =========================================================================
// ARCHITECTURAL NOTE: Why 'partial'?
// =========================================================================
// When you use [ObservableProperty], the MVVM Toolkit Source Generators
// write boilerplate code in a hidden background file to wire up the UI bindings.
// Your class must be 'partial' so C# can stitch this file and the hidden
// generated file together during compilation.
// =========================================================================

namespace IncomeExpenditureTracker.ViewModels
{
    /// <summary>
    /// The root context of the application.
    /// Acts as the Global Notification Hub, listening to the Broker for any
    /// ToastNotificationMessage sent by any service or ViewModel.
    /// </summary>
    public partial class MainWindowViewModel : ViewModelBase
    {
        // =========================================================================
        // OBSERVABLE PROPERTIES (The Waiter's Tray)
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
        // CONSTRUCTOR
        // =========================================================================
        public MainWindowViewModel(IApplicationBroker broker)
            : base(broker)
        {
            // Subscribe to Legacy File Staging events
            Broker.Register<FileStagingErrorMessage>(this, OnFileStagingErrorReceived);
            Broker.Register<StagingBatchCompletedMessage>(this, OnStagingCompleted);
            Broker.Register<StagingProgressMessage>(this, OnProgressReceived);

            // Subscribe to the new Global Notification Stream
            Broker.Register<ToastNotificationMessage>(this, OnToastReceived);
        }

        // =========================================================================
        // EVENT HANDLERS (The Reactive Magic)
        // =========================================================================

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