using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.DependencyInjection;

namespace IncomeExpenditureTracker.UI.ImportHub;

/// <summary>
/// Parent view model for the Import Hub, managing SPA routing between Queue and Preview states.
/// Subscribes to staging completion events to dynamically route users to the Preview Workbench.
/// </summary>
public partial class ImportHubViewModel : ViewModelBase
{
    private readonly IServiceProvider _serviceProvider;
    private ImportQueueViewModel? _queueViewModel;

    [ObservableProperty]
    private ViewModelBase _currentContent = null!;

    public ImportHubViewModel(IApplicationBroker broker, IServiceProvider serviceProvider)
        : base(broker)
    {
        _serviceProvider = serviceProvider;

        // Default state is the queue
        _queueViewModel = serviceProvider.GetRequiredService<ImportQueueViewModel>();
        CurrentContent = _queueViewModel;

        // Subscribe to staging batch completion to route to preview
        // When files are successfully staged, transition to the Preview Workbench
        Broker.Register<StagingBatchCompletedMessage>(this, OnStagingCompleted);

        // Subscribe to PreviewSheetMessage for internal sub-routing (manual sheet selection from file tree).
        // This prevents collision with MainWindowViewModel's global NavigationMessage routing.
        Broker.Register<PreviewSheetMessage>(this, OnPreviewSheetRequested);

        // Subscribe to navigation messages to return to queue from preview
        // This allows the preview to navigate back after commit/discard (uses global NavigationMessage "ImportHub")
        Broker.Register<NavigationMessage>(this, OnNavigationRequested);
    }

    /// <summary>
    /// Invoked when the StatementManager finishes staging files.
    /// Routes to SheetPreviewViewModel if files were successfully staged.
    /// </summary>
    private async void OnStagingCompleted(StagingBatchCompletedMessage message)
    {
        // Only transition if there were successful stagings
        if (message.TotalSuccess > 0 && _queueViewModel?.HasUncommittedFiles() == true)
        {
            // Get the first staged file to preview
            var firstFile = _queueViewModel.StagedFiles.FirstOrDefault();
            if (firstFile != null)
            {
                var previewVm = _serviceProvider.GetRequiredService<SheetPreviewViewModel>();

                // Load the preview asynchronously with the first staged file
                await previewVm.LoadPreviewAsync(firstFile.Id, firstFile.FileName);

                // Route to the preview view
                CurrentContent?.Dispose();
                CurrentContent = previewVm;
            }
        }
    }

    /// <summary>
    /// Handles PreviewSheetMessage for internal sub-routing when user manually selects a sheet.
    /// This message is broadcast by ImportQueueViewModel and does NOT propagate to MainWindowViewModel.
    /// </summary>
    private async void OnPreviewSheetRequested(PreviewSheetMessage message)
    {
        // Create and load the preview ViewModel with the selected sheet
        var previewVm = _serviceProvider.GetRequiredService<SheetPreviewViewModel>();
        await previewVm.LoadPreviewAsync(message.FileId, message.FileName, message.TargetSheetName);

        // Route to the preview view
        CurrentContent?.Dispose();
        CurrentContent = previewVm;
    }

    /// <summary>
    /// Handles NavigationMessage for returning to the ImportHub queue after confirm/discard.
    /// Only processes "ImportHub" destination; all other destinations are ignored to prevent routing conflicts.
    /// </summary>
    private void OnNavigationRequested(NavigationMessage message)
    {
        if (message.Destination == "ImportHub")
        {
            // Return to the queue view
            CurrentContent?.Dispose();

            // Create a fresh queue instance
            _queueViewModel = _serviceProvider.GetRequiredService<ImportQueueViewModel>();
            CurrentContent = _queueViewModel;
        }
    }

    public override void Dispose()
    {
        // Unregister message subscriptions to prevent memory leaks
        Broker.UnregisterAll(this);

        CurrentContent?.Dispose();
        base.Dispose();
    }
}
