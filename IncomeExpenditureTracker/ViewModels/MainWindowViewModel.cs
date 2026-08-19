using Avalonia.Threading;
using System.Collections.ObjectModel;
using IncomeExpenditureTracker.Models;
using CommunityToolkit.Mvvm.ComponentModel; // Needed for [ObservableProperty]
using IncomeExpenditureTracker.Services.Messaging;

// Reason for using partial

// When you use the [ObservableProperty] attribute above your private fields (like _loadingPercentage), you are using C# Source Generators.
// The toolkit automatically writes a bunch of hidden boilerplate code in the background to create the public LoadingPercentage property and wire up the UI notifications.
// Because the compiler is writing code for this class in a hidden file,
// your side of the class must be declared as partial so C# knows to stitch them both together when it builds the app.

namespace IncomeExpenditureTracker.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        // 1. Reactive UI Collection: Avalonia automatically binds to this.
        // When we add an item, a UI toast/notification will instantly appear on screen.
        public ObservableCollection<string> ToastNotifications { get; } = new();

        // Observable properties for the live UI Progress Bar
        [ObservableProperty]
        private int _loadingPercentage;

        [ObservableProperty]
        private string _loadingStatus = "Ready";

        // 2. Constructor Injection: We ask for the broker and pass it to our safe ViewModelBase
        public MainWindowViewModel(IApplicationBroker broker)
            : base(broker)
        {
            // 3. Subscribe! We tell the postman: "If you ever see a FileStagingErrorMessage, hand it to me."
            Broker.Register<FileStagingErrorMessage>(this, OnFileStagingErrorReceived);

            Broker.Register<StagingBatchCompletedMessage>(this, OnStagingCompleted);

            // Subscribe to the Live Progress updates!
            Broker.Register<StagingProgressMessage>(this, OnProgressReceived);
        }

        // Event Handler for Live Progress
        private void OnProgressReceived(StagingProgressMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                // This will instantly animate the Avalonia Progress Bar
                LoadingPercentage = message.Percentage;
                LoadingStatus = message.StatusMessage;
            });
        }

        // 4. The Event Handler
        private void OnFileStagingErrorReceived(FileStagingErrorMessage message)
        {
            // CRITICAL: StatementManager stages files on parallel background threads.
            // You are strictly forbidden from updating UI components from a background thread.
            // Dispatcher.UIThread.Post safely "teleports" this operation back to the main UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                ToastNotifications.Add($"❌ Staging Failed: {message.Error.FileName}\n{message.Error.Message}");
            });
        }

        private void OnStagingCompleted(StagingBatchCompletedMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (message.TotalSuccess > 0)
                {
                    ToastNotifications.Add($"✅ Successfully staged {message.TotalSuccess} file(s) for preview.");
                }

                // Reset the loading text when finished
                LoadingStatus = "Staging Complete!";
            });
        }
    }
}