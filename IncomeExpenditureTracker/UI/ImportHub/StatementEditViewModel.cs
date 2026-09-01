using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.ImportHub
{
    public partial class StatementEditViewModel : ViewModelBase
    {
        private readonly StatementManager _statementManager;
        private Guid _currentFileId;

        // =========================================================================
        // OBSERVABLE PROPERTIES (The Waiter's Tray)
        // Avalonia automatically updates the screen when these change.
        // =========================================================================

        [ObservableProperty]
        private StatementPreview? _previewData;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private string _currentFileName = string.Empty;

        // =========================================================================
        // CONSTRUCTOR
        // =========================================================================
        public StatementEditViewModel(
            StatementManager statementManager,
            IApplicationBroker broker)
            : base(broker)
        {
            _statementManager = statementManager ?? throw new ArgumentNullException(nameof(statementManager));

            // Listen for any specific progress events while on this screen
            Broker.Register<StagingProgressMessage>(this, OnProgressReceived);
        }

        // =========================================================================
        // COMMANDS (Fired by Avalonia UI Buttons)
        // =========================================================================

        /// <summary>
        /// Phase 2: Called when the user clicks a staged file to preview it.
        /// </summary>
        [RelayCommand]
        public async Task LoadPreviewAsync(PendingFilePreview pendingFile)
        {
            // Null check in case the UI passes an empty binding
            if (IsBusy || pendingFile == null) return;

            _currentFileId = pendingFile.Id;
            CurrentFileName = pendingFile.FileName; // Store it for the UI to display!

            IsBusy = true;
            StatusText = $"Analyzing document structure for {pendingFile.FileName}...";

            try
            {
                // Retrieve the in-memory extraction analysis[cite: 1]
                PreviewData = await _statementManager.PreviewStagedFileAsync(pendingFile.Id, null);
                StatusText = $"Preview loaded for {pendingFile.FileName}. Please map your columns.";
            }
            catch (Exception)
            {
                // The StatementManager already logged the error and sent a FileStagingErrorMessage![cite: 1]
                StatusText = $"Failed to load preview for {pendingFile.FileName}.";
                PreviewData = null;
                CurrentFileName = string.Empty;
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Phase 3: Called when the user confirms their column mappings and clicks "Import".
        /// </summary>
        [RelayCommand]
        public async Task CommitImportAsync(PreviewTracker confirmedTracker)
        {
            if (IsBusy || _currentFileId == Guid.Empty) return;

            IsBusy = true;
            StatusText = "Committing transactions to database...";

            try
            {
                // Dispatch the background learning and SQLite batch import[cite: 1]
                await _statementManager.CommitStagedFileAsync(_currentFileId, confirmedTracker);

                // On success, clear the preview since the file is now consumed
                PreviewData = null;
                _currentFileId = Guid.Empty;
                StatusText = "Import successful!";

                // Broadcast to the rest of the app (like the Transaction Grid) that new data is available
                Broker.Send(new ImportBatchCompletedMessage(confirmedTracker.FinalPreview.PreviewTransactions.Count));
            }
            catch (Exception)
            {
                StatusText = "Import failed. Check notifications for details.";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Called if the user decides to cancel and throw the file away.
        /// </summary>
        [RelayCommand]
        public void DiscardFile()
        {
            if (_currentFileId != Guid.Empty)
            {
                // Releases the OS file lock and clears RAM[cite: 1]
                _statementManager.DiscardFile(_currentFileId);

                PreviewData = null;
                _currentFileId = Guid.Empty;
                CurrentFileName = string.Empty; // Clear the name!
                StatusText = "File discarded.";
            }
        }

        // =========================================================================
        // EVENT HANDLERS
        // =========================================================================
        private void OnProgressReceived(StagingProgressMessage message)
        {
            // Update the UI text if a progress event fires while editing
            StatusText = message.StatusMessage;
        }
    }
}