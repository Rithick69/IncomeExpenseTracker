using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.ImportHub;

/// <summary>
/// ViewModel for managing file intake via drag and drop and displaying parsing progress.
/// </summary>
public partial class ImportQueueViewModel : ViewModelBase
{
    private readonly IStatementManager _statementManager;
    private readonly IApplicationBroker _broker;

    [ObservableProperty]
    private bool _isExcelSelected = true;

    [ObservableProperty]
    private bool _isCsvSelected = false;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _loadingMessage = string.Empty;

    public ObservableCollection<PendingFilePreview> StagedFiles { get; } = new();

    public ImportQueueViewModel(IStatementManager statementManager, IApplicationBroker broker)
        : base(broker)
    {
        _statementManager = statementManager;
        _broker = broker;
    }

    /// <summary>
    /// Handles dropped files from the UI.
    /// </summary>
    [RelayCommand]
    public async Task HandleDroppedFilesAsync(IEnumerable<string> filePaths)
    {
        if (filePaths == null || !filePaths.Any()) return;

        var paths = filePaths.ToList();

        if (paths.Count > 5)
        {
            _broker.Send(new ToastNotificationMessage("Maximum of 5 files allowed.", NotificationType.Warning));
            return;
        }

        // Validate type based on selection
        string expectedExtension = IsExcelSelected ? ".xlsx" : ".csv"; // simplified
        if (paths.Any(p => !string.Equals(Path.GetExtension(p), expectedExtension, StringComparison.OrdinalIgnoreCase)))
        {
            _broker.Send(new ToastNotificationMessage($"Only {expectedExtension} files are allowed currently.", NotificationType.Error));
            return;
        }

        try
        {
            IsLoading = true;
            LoadingMessage = "Processing files...";

            // Wire up live progress reporting to update UI state
            var progress = new Progress<LoadingProgress>(p =>
            {
                // Run on UI thread to update observable properties safely
                RunOnUIThread(() =>
                {
                    LoadingMessage = p.Message;
                });
            });

            var result = await _statementManager.StageFilesAsync(paths, progress, CancellationToken.None);

            foreach (var success in result.Successes)
            {
                StagedFiles.Add(success);
            }

            foreach (var failure in result.Failures)
            {
                _broker.Send(new ToastNotificationMessage($"Failed to stage {failure.FileName}: {failure.Message}", NotificationType.Error));
            }
        }
        catch (Exception ex)
        {
            _broker.Send(new ToastNotificationMessage(ex.Message, NotificationType.Error));
        }
        finally
        {
            IsLoading = false;
            LoadingMessage = string.Empty;
        }
    }

    /// <summary>
    /// Handles manual sheet selection from the expandable file tree.
    /// Broadcasts PreviewSheetMessage (NOT NavigationMessage) to avoid collision with MainWindowViewModel's global routing.
    /// Only ImportHubViewModel subscribes to this message for internal sub-routing.
    /// </summary>
    [RelayCommand]
    public void SelectSheet(object parameter)
    {
        // Parameter is a tuple containing the file ID and sheet name
        if (parameter is not (Guid fileId, string sheetName))
            return;

        // Find the file to get the file name
        var selectedFile = StagedFiles.FirstOrDefault(f => f.Id == fileId);
        if (selectedFile == null)
            return;

        // Broadcast a sub-routing message ONLY to ImportHubViewModel.
        // This does NOT propagate to MainWindowViewModel, preventing the "Unknown route: PreviewSheet" crash.
        _broker.Send(new PreviewSheetMessage(fileId, selectedFile.FileName, sheetName));
    }

    public bool HasUncommittedFiles() => StagedFiles.Any();

    public void DiscardAllFiles()
    {
        foreach (var file in StagedFiles)
        {
            _statementManager.DiscardFile(file.Id);
        }
        StagedFiles.Clear();
    }
}
