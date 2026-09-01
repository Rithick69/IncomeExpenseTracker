using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Ledger
{
    public partial class TransactionReviewViewModel : ViewModelBase
    {
        private readonly ITransactionReviewOrchestrator _orchestrator;

        // =========================================================================
        // OBSERVABLE PROPERTIES
        // =========================================================================

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "Ready";

        // Pagination State
        [ObservableProperty]
        private int _currentPage = 1;

        [ObservableProperty]
        private int _pageSize = 50;

        [ObservableProperty]
        private int _totalItems;

        public int TotalPages => TotalItems == 0 ? 1 : (int)Math.Ceiling((double)TotalItems / PageSize);

        // The main Data Grid binds directly to this collection
        public ObservableCollection<Transaction> Transactions { get; } = new();

        // =========================================================================
        // CONSTRUCTOR
        // =========================================================================
        public TransactionReviewViewModel(
            ITransactionReviewOrchestrator orchestrator,
            IApplicationBroker broker)
            : base(broker)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

            // The Waiter listens for backend updates to keep the grid perfectly synced!
            Broker.Register<BatchUpdateCompletedMessage>(this, OnBatchUpdateCompleted);
            Broker.Register<ImportBatchCompletedMessage>(this, OnNewImportCompleted);
            Broker.Register<EntityDeletedMessage>(this, OnEntityDeleted);
        }

        // =========================================================================
        // COMMANDS (Triggered by UI actions)
        // =========================================================================

        [RelayCommand]
        public async Task LoadTransactionsAsync()
        {
            IsLoading = true;
            StatusText = $"Loading transactions (Page {CurrentPage})...";

            try
            {
                // Set up the pagination filters expected by the Orchestrator
                var filterArgs = new TransactionFilterArgs
                {
                    Limit = PageSize,
                    Offset = (CurrentPage - 1) * PageSize
                };

                // Fetch data via the Orchestrator facade
                var result = await _orchestrator.GetTransactionsAsync(filterArgs);

                Transactions.Clear();
                foreach (var tx in result.Items)
                {
                    Transactions.Add(tx);
                }

                TotalItems = result.TotalCount;
                OnPropertyChanged(nameof(TotalPages)); // Notify UI that the page count might have changed

                StatusText = $"Loaded {Transactions.Count} transactions.";
            }
            catch (Exception)
            {
                // Relying on the internal services to log the actual exception stack traces
                StatusText = "Failed to load transactions. Check system logs.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        public async Task NextPageAsync()
        {
            if (CurrentPage < TotalPages)
            {
                CurrentPage++;
                await LoadTransactionsAsync();
            }
        }

        [RelayCommand]
        public async Task PreviousPageAsync()
        {
            if (CurrentPage > 1)
            {
                CurrentPage--;
                await LoadTransactionsAsync();
            }
        }

        // =========================================================================
        // EVENT HANDLERS (The Reactive Magic)
        // =========================================================================

        private void OnBatchUpdateCompleted(BatchUpdateCompletedMessage message)
        {
            RunOnUIThread(() =>
            {
                StatusText = $"✅ Successfully applied {message.UpdatedRowCount} corrections.";
                // Auto-refresh the grid to drop any "Needs Review" flags that were just fixed!
                _ = LoadTransactionsAsync();
            });
        }

        private void OnNewImportCompleted(ImportBatchCompletedMessage message)
        {
            RunOnUIThread(() =>
            {
                StatusText = $"✅ New batch imported with {message.TotalTransactions} records.";
                CurrentPage = 1; // Reset to page 1 to see the newest data
                _ = LoadTransactionsAsync();
            });
        }

        private void OnEntityDeleted(EntityDeletedMessage message)
        {
            RunOnUIThread(() =>
            {
                // If a user completely reverts a batch, we MUST refresh the grid
                // so we don't display orphaned transactions.
                if (message.EntityType == "Import Batch")
                {
                    StatusText = "🗑️ Import batch reverted. Refreshing grid...";
                    _ = LoadTransactionsAsync();
                }
            });
        }
    }
}