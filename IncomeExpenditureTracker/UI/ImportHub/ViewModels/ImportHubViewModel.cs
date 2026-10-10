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

        // Subscribe to navigation messages to return to queue from preview
        // This allows the preview to navigate back after commit/discard
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
    /// Handles navigation requests (e.g., returning to the ImportHub queue after confirm/discard).
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
