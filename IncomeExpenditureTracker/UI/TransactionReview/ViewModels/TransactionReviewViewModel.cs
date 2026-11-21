using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.TransactionReview;

/// <summary>
/// ViewModel for the Hybrid Ledger screen.
/// Manages cross-page SQLite filtering and sorting, MasterData taxonomy lookups,
/// inline editing, split transactions, transfer linking, bulk actions, and staged checkout.
/// </summary>
public partial class TransactionReviewViewModel : ViewModelBase
{
    // =============================================================================
    // 1. DEPENDENCIES & INTERNAL STATE
    // =============================================================================

    private readonly ITransactionReviewOrchestrator _orchestrator;
    private readonly IMasterDataOrchestrator _masterDataOrchestrator;

    private bool _isInitializing = true;
    private bool _isUpdatingSelection = false;
    private int _currentOffset = 0;
    private int _totalCount = 0;
    private CancellationTokenSource? _searchDebounceCts;

    // Fast O(1) MasterData lookup dictionaries for grid mapping and ID resolution
    private Dictionary<int, string> _accountNamesById = new();
    private Dictionary<int, string> _payeeNamesById = new();
    private Dictionary<int, string> _tagNamesById = new();
    private Dictionary<string, int> _payeeIdsByName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _tagIdsByName = new(StringComparer.OrdinalIgnoreCase);

    // =============================================================================
    // 2. ZONE 1: TOP ACTION BAR (FILTERS, SEARCH, SORTING & BALANCE WIDGET)
    // =============================================================================

    /// <summary>
    /// When enabled, filters the ledger to show only transactions with ReviewStatus > 0.
    /// </summary>
    [ObservableProperty]
    private bool _isTriageMode = false;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private DateTimeOffset? _startDate = null;

    [ObservableProperty]
    private DateTimeOffset? _endDate = null;

    [ObservableProperty]
    private int? _selectedAccountId = null;

    /// <summary>
    /// Account filter options populated from MasterData (includes "All Accounts" at Id = 0).
    /// </summary>
    public ObservableCollection<AccountFilterItem> AvailableAccounts { get; } = new();

    private AccountFilterItem? _selectedAccount;
    public AccountFilterItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value))
            {
                SelectedAccountId = (value != null && value.Id > 0) ? value.Id : null;
                OnPropertyChanged(nameof(IsAccountSelected));
                OnPropertyChanged(nameof(HasActiveFilters));
                BalanceLabel = IsAccountSelected ? "Account Balance:" : "Total Balance:";

                if (!_isInitializing)
                {
                    _currentOffset = 0;
                    _ = RefreshGridAsync();
                }
            }
        }
    }

    /// <summary>
    /// Import batch filter options populated from MasterData (includes "All Batches" at Id = 0).
    /// </summary>
    public ObservableCollection<ImportBatchFilterItem> AvailableImportBatches { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBatchSelected))]
    private ImportBatchFilterItem? _selectedImportBatch = null;

    public bool IsAccountSelected => SelectedAccountId.HasValue && SelectedAccountId.Value > 0;
    public bool IsBatchSelected => SelectedImportBatch != null && SelectedImportBatch.Id > 0;

    /// <summary>
    /// Controls visibility of the "Reset Filters" button in the top action bar.
    /// </summary>
    public bool HasActiveFilters =>
        IsTriageMode ||
        !string.IsNullOrWhiteSpace(SearchText) ||
        StartDate.HasValue ||
        EndDate.HasValue ||
        IsAccountSelected ||
        IsBatchSelected;

    // Cross-Page SQLite Sorting
    public ObservableCollection<string> SortByOptions { get; } = new() { "Date", "Debit", "Credit" };

    [ObservableProperty]
    private string _selectedSortBy = "Date";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortDirectionIcon))]
    [NotifyPropertyChangedFor(nameof(SortDirectionTooltip))]
    private bool _isSortDescending = true;

    public string SortDirectionIcon => IsSortDescending ? "↓" : "↑";
    public string SortDirectionTooltip => IsSortDescending
        ? "Descending (Newest / High to Low)"
        : "Ascending (Oldest / Low to High)";

    // Highlighted Balance Widget
    [ObservableProperty]
    private decimal _currentFilterBalance = 0m;

    [ObservableProperty]
    private string _balanceLabel = "Total Balance:";

    // Filter & Search Change Hooks
    partial void OnStartDateChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        if (_isInitializing) return;
        _currentOffset = 0;
        _ = RefreshGridAsync();
    }

    partial void OnEndDateChanged(DateTimeOffset? value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        if (_isInitializing) return;
        _currentOffset = 0;
        _ = RefreshGridAsync();
    }

    partial void OnSelectedImportBatchChanged(ImportBatchFilterItem? value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        if (_isInitializing) return;
        _currentOffset = 0;
        _ = RefreshGridAsync();
    }

    partial void OnSelectedSortByChanged(string value)
    {
        if (_isInitializing) return;
        _currentOffset = 0;
        _ = RefreshGridAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        if (_isInitializing) return;

        // Debounce live search by 300ms to avoid firing a SQLite query on every keystroke
        _searchDebounceCts?.Cancel();
        _searchDebounceCts?.Dispose();
        _searchDebounceCts = new CancellationTokenSource();
        var token = _searchDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    _currentOffset = 0;
                    await RefreshGridAsync();
                });
            }
            catch (TaskCanceledException) { }
        }, token);
    }

    // =============================================================================
    // 3. ZONE 2: DATAGRID DATA SOURCE & NATIVE GROUPING
    // =============================================================================

    private readonly ObservableCollection<TransactionRowViewModel> _rawTransactions = new();

    /// <summary>
    /// Grouped collection view bound directly to the Avalonia DataGrid.
    /// </summary>
    public DataGridCollectionView TransactionsView { get; }

    public ObservableCollection<string> GroupByOptions { get; } = new()
    {
        "None",
        "Date",
        "Payee",
        "Account"
    };

    [ObservableProperty]
    private string _selectedGroupBy = "None";

    partial void OnSelectedGroupByChanged(string value)
    {
        TransactionsView.GroupDescriptions.Clear();

        if (value == "Date")
            TransactionsView.GroupDescriptions.Add(new DataGridPathGroupDescription("Date"));
        else if (value == "Payee")
            TransactionsView.GroupDescriptions.Add(new DataGridPathGroupDescription("PayeeName"));
        else if (value == "Account")
            TransactionsView.GroupDescriptions.Add(new DataGridPathGroupDescription("AccountName"));
    }

    // =============================================================================
    // 4. ZONE 2 TOOLBAR & FLOATING BULK ACTIONS MODAL
    // =============================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedTransactions))]
    [NotifyPropertyChangedFor(nameof(SelectedCountText))]
    [NotifyPropertyChangedFor(nameof(CanLinkTransfer))]
    [NotifyPropertyChangedFor(nameof(CanOpenBulkActions))]
    [NotifyPropertyChangedFor(nameof(BulkActionsButtonText))]
    private int _selectedCount = 0;

    public bool HasSelectedTransactions => SelectedCount > 0;
    public string SelectedCountText => $"{SelectedCount} {(SelectedCount == 1 ? "Transaction" : "Transactions")} Selected";
    public bool CanLinkTransfer => SelectedCount == 2;
    public bool CanOpenBulkActions => SelectedCount >= 2;
    public string BulkActionsButtonText => SelectedCount >= 2 ? $"Bulk Actions ({SelectedCount})" : "Bulk Actions";

    [ObservableProperty]
    private bool _isBulkActionsModalVisible = false;

    [ObservableProperty]
    private bool _isAllSelected = false;

    [ObservableProperty]
    private string _bulkTag = string.Empty;

    [ObservableProperty]
    private string _bulkPayee = string.Empty;

    partial void OnIsAllSelectedChanged(bool value)
    {
        if (_isUpdatingSelection) return;

        _isUpdatingSelection = true;
        try
        {
            foreach (var row in _rawTransactions)
            {
                row.IsSelected = value;
            }
            SelectedCount = value ? _rawTransactions.Count : 0;
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    // =============================================================================
    // 5. ZONE 3 (BOTTOM): PAGINATION PROPERTIES
    // =============================================================================

    [ObservableProperty]
    private string _pageInfo = "Showing 0 transactions";

    public ObservableCollection<int> PageSizeOptions { get; } = new() { 50, 100, 200 };

    [ObservableProperty]
    private int _selectedPageSize = 50;

    [ObservableProperty]
    private bool _canGoPrevious = false;

    [ObservableProperty]
    private bool _canGoNext = false;

    partial void OnSelectedPageSizeChanged(int value)
    {
        if (_isInitializing) return;
        _currentOffset = 0;
        _ = RefreshGridAsync();
    }

    // =============================================================================
    // 6. ZONE 3 (RIGHT): SLIDE-IN EDIT PANEL, AUTOCOMPLETE & LIVE SPLIT SUMMARY
    // =============================================================================

    [ObservableProperty]
    private bool _isPanelVisible = false;

    [ObservableProperty]
    private TransactionRowViewModel? _selectedTransaction = null;

    [ObservableProperty]
    private DateTimeOffset? _editDate = DateTimeOffset.Now;

    [ObservableProperty]
    private decimal _editDebit = 0m;

    [ObservableProperty]
    private decimal _editCredit = 0m;

    [ObservableProperty]
    private string _editPayee = string.Empty;

    [ObservableProperty]
    private string _editTag = string.Empty;

    [ObservableProperty]
    private string _editMemo = string.Empty;

    [ObservableProperty]
    private string _rawAmountText = string.Empty;

    // Shared AutoCompleteBox sources (populated from MasterData)
    public ObservableCollection<string> AvailablePayees { get; } = new();
    public ObservableCollection<string> AvailableTags { get; } = new();

    // Split Transaction Lines & Live Allocation Summary
    public ObservableCollection<SplitLineViewModel> MockSplitTransactions { get; } = new();

    [ObservableProperty] private decimal _splitTargetAmount = 0m;
    [ObservableProperty] private decimal _splitAllocatedAmount = 0m;
    [ObservableProperty] private decimal _splitRemainingAmount = 0m;
    [ObservableProperty] private bool _canCommitSplit = false;
    [ObservableProperty] private string _splitStatusColor = "#9CA3AF";

    // =============================================================================
    // 7. ZONE 4: STAGING REVIEW MODAL CONTROL
    // =============================================================================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStagedEdits))]
    private int _stagedEditsCount = 0;

    public bool HasStagedEdits => StagedEditsCount > 0;

    [ObservableProperty]
    private bool _isStagingModalVisible = false;

    public StagingReviewModalViewModel StagingViewModel { get; }

    // =============================================================================
    // 8. CONSTRUCTOR & MASTERDATA INITIALIZATION
    // =============================================================================

    public TransactionReviewViewModel(
        ITransactionReviewOrchestrator orchestrator,
        IMasterDataOrchestrator masterDataOrchestrator,
        IApplicationBroker broker)
        : base(broker)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _masterDataOrchestrator = masterDataOrchestrator ?? throw new ArgumentNullException(nameof(masterDataOrchestrator));

        TransactionsView = new DataGridCollectionView(_rawTransactions);

        StagingViewModel = new StagingReviewModalViewModel(orchestrator, broker)
        {
            CloseAction = () => IsStagingModalVisible = false
        };

        // Keep badge count and row draft indicators synced with the staging cart
        StagingViewModel.StagedCorrections.CollectionChanged += OnStagedCorrectionsChanged;
        StagedEditsCount = StagingViewModel.StagedCorrections.Count;

        _ = InitializeAsync();
    }

    /// <summary>
    /// Loads taxonomy and filter dropdowns first so the initial grid fetch resolves real names.
    /// </summary>
    private async Task InitializeAsync()
    {
        try
        {
            await LoadMasterDataAsync();
        }
        finally
        {
            _isInitializing = false;
        }

        await RefreshGridAsync();
    }

    private async Task LoadMasterDataAsync()
    {
        try
        {
            var accountsTask = _masterDataOrchestrator.GetAllAccountsAsync();
            var payeesTask = _masterDataOrchestrator.GetAllPayeesAsync();
            var tagsTask = _masterDataOrchestrator.GetAllTagsAsync();
            var batchesTask = _masterDataOrchestrator.GetAllImportBatchesAsync();
            var entitiesTask = _masterDataOrchestrator.GetAllEntitiesAsync();

            await Task.WhenAll(accountsTask, payeesTask, tagsTask, batchesTask, entitiesTask);

            // 1. Entities & Accounts (builds formatted "Institution (Type) •••• 1234" display strings)
            var accounts = accountsTask.Result;
            var entityNamesById = entitiesTask.Result.ToDictionary(e => e.Id, e => e.Name);

            _accountNamesById = accounts.ToDictionary(
                a => a.Id,
                a => FormatAccountDisplay(a, entityNamesById));

            AvailableAccounts.Clear();
            AvailableAccounts.Add(new AccountFilterItem { Id = 0, DisplayName = "All Accounts" });
            foreach (var acc in accounts)
            {
                AvailableAccounts.Add(new AccountFilterItem
                {
                    Id = acc.Id,
                    DisplayName = _accountNamesById[acc.Id]
                });
            }
            SelectedAccount = AvailableAccounts.FirstOrDefault();

            // 2. Payees
            var payees = payeesTask.Result.ToList();
            _payeeNamesById = payees.ToDictionary(p => p.Id, p => p.Name);
            _payeeIdsByName = payees
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            AvailablePayees.Clear();
            foreach (var payee in payees.OrderBy(p => p.Name))
            {
                AvailablePayees.Add(payee.Name);
            }

            // 3. Tags
            var tags = tagsTask.Result;
            _tagNamesById = tags.ToDictionary(t => t.Id, t => t.Name);
            _tagIdsByName = tags
                .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

            AvailableTags.Clear();
            foreach (var tag in tags.OrderBy(t => t.Name))
            {
                AvailableTags.Add(tag.Name);
            }

            // 4. Import Batches
            var batches = batchesTask.Result;
            AvailableImportBatches.Clear();
            AvailableImportBatches.Add(new ImportBatchFilterItem { Id = 0, DisplayName = "All Batches" });
            foreach (var batch in batches.OrderByDescending(b => b.Id))
            {
                AvailableImportBatches.Add(new ImportBatchFilterItem
                {
                    Id = batch.Id,
                    DisplayName = $"Batch #{batch.Id} - {batch.FileName}"
                });
            }
            SelectedImportBatch = AvailableImportBatches.FirstOrDefault();
        }
        catch (Exception)
        {
            Broker.Send(new ToastNotificationMessage("Failed to load filter & taxonomy data.", NotificationType.Error));
        }
    }

    // =============================================================================
    // 9. COMMANDS: GRID REFRESH, FILTERING & SORTING
    // =============================================================================

    [RelayCommand]
    private async Task RefreshGridAsync()
    {
        try
        {
            int? batchId = (SelectedImportBatch != null && SelectedImportBatch.Id > 0)
                ? SelectedImportBatch.Id
                : null;

            var filters = new TransactionFilterArgs(
                BatchId: batchId,
                AccountId: SelectedAccountId,
                SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
                IsTriageMode: IsTriageMode,
                DateFrom: StartDate?.DateTime,
                DateTo: EndDate?.DateTime,
                SortBy: SelectedSortBy,
                SortDescending: IsSortDescending,
                Limit: SelectedPageSize,
                Offset: _currentOffset
            );

            var result = await _orchestrator.GetTransactionsAsync(filters);
            var items = result.Items.ToList();
            _totalCount = result.TotalCount;

            // Resolve Balance Widget (Account Balance uses latest chronological row regardless of sort order)
            if (IsAccountSelected && items.Any())
            {
                CurrentFilterBalance = items
                    .OrderByDescending(x => x.Date)
                    .ThenByDescending(x => x.Id)
                    .First()
                    .RunningBalance;
            }
            else if (!IsAccountSelected)
            {
                CurrentFilterBalance = await _orchestrator.GetTotalGlobalBalanceAsync();
            }
            else
            {
                CurrentFilterBalance = 0m;
            }

            // Unsubscribe old rows before clearing to prevent memory leaks
            foreach (var oldRow in _rawTransactions)
            {
                oldRow.PropertyChanged -= OnTransactionRowPropertyChanged;
            }
            _rawTransactions.Clear();

            _isUpdatingSelection = true;
            SelectedCount = 0;
            IsAllSelected = false;
            _isUpdatingSelection = false;

            var stagedIds = StagingViewModel.StagedCorrections
                .Select(c => c.TransactionId)
                .ToHashSet();

            foreach (var txn in items)
            {
                var accountName = _accountNamesById.TryGetValue(txn.AccountId, out var accName)
                    ? accName
                    : $"Account {txn.AccountId}";

                string? payeeName = txn.PayeeId.HasValue
                    ? (_payeeNamesById.TryGetValue(txn.PayeeId.Value, out var pName) ? pName : $"Payee {txn.PayeeId.Value}")
                    : null;

                string? tagName = txn.TagId.HasValue
                    ? (_tagNamesById.TryGetValue(txn.TagId.Value, out var tName) ? tName : $"Tag {txn.TagId.Value}")
                    : null;

                var row = TransactionRowViewModel.FromEntity(txn, accountName, payeeName, tagName);
                row.IsStaged = stagedIds.Contains(row.Id);
                row.PropertyChanged += OnTransactionRowPropertyChanged;
                _rawTransactions.Add(row);
            }

            // Update Pagination Summary
            if (_totalCount == 0)
            {
                PageInfo = "Showing 0 transactions";
            }
            else
            {
                int start = _currentOffset + 1;
                int end = Math.Min(_currentOffset + items.Count, _totalCount);
                PageInfo = $"Showing {start}-{end} of {_totalCount:N0} transactions";
            }

            CanGoPrevious = _currentOffset > 0;
            CanGoNext = (_currentOffset + SelectedPageSize) < _totalCount;
        }
        catch (Exception)
        {
            Broker.Send(new ToastNotificationMessage("Failed to load transactions.", NotificationType.Error));
        }
    }

    [RelayCommand]
    private async Task ToggleTriageModeAsync()
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _currentOffset = 0;
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task ToggleSortDirectionAsync()
    {
        IsSortDescending = !IsSortDescending;
        _currentOffset = 0;
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        _currentOffset = 0;
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task ClearFiltersAsync()
    {
        _isInitializing = true;
        try
        {
            IsTriageMode = false;
            SearchText = string.Empty;
            StartDate = null;
            EndDate = null;
            SelectedAccount = AvailableAccounts.FirstOrDefault();
            SelectedImportBatch = AvailableImportBatches.FirstOrDefault();
            SelectedGroupBy = "None";
            SelectedSortBy = "Date";
            IsSortDescending = true;
            _currentOffset = 0;
        }
        finally
        {
            _isInitializing = false;
            OnPropertyChanged(nameof(HasActiveFilters));
        }

        await RefreshGridAsync();
    }

    // =============================================================================
    // 10. COMMANDS: ROW EDITING & STAGING
    // =============================================================================

    [RelayCommand]
    private async Task ReviewPendingEditsAsync()
    {
        if (StagedEditsCount == 0) return;

        IsStagingModalVisible = true;
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void SelectTransaction(TransactionRowViewModel? transaction)
    {
        ClearSplitLines();

        if (transaction == null)
        {
            IsPanelVisible = false;
            SelectedTransaction = null;
            RecalculateSplitSummary();
            return;
        }

        SelectedTransaction = transaction;
        EditDate = new DateTimeOffset(transaction.Date);
        EditDebit = transaction.Debit;
        EditCredit = transaction.Credit;
        EditPayee = transaction.PayeeName ?? string.Empty;
        EditTag = transaction.TagName ?? string.Empty;
        EditMemo = transaction.Memo ?? string.Empty;
        RawAmountText = transaction.RawAmountText ?? string.Empty;
        IsPanelVisible = true;

        RecalculateSplitSummary();
    }

    [RelayCommand]
    private async Task StageUpdatesAsync()
    {
        if (SelectedTransaction == null) return;

        decimal originalDebit = SelectedTransaction.Debit;
        decimal originalCredit = SelectedTransaction.Credit;

        SelectedTransaction.Date = EditDate?.DateTime ?? SelectedTransaction.Date;
        SelectedTransaction.Debit = EditDebit;
        SelectedTransaction.Credit = EditCredit;
        SelectedTransaction.PayeeName = string.IsNullOrWhiteSpace(EditPayee) ? null : EditPayee.Trim();
        SelectedTransaction.TagName = string.IsNullOrWhiteSpace(EditTag) ? null : EditTag.Trim();
        SelectedTransaction.Memo = string.IsNullOrWhiteSpace(EditMemo) ? null : EditMemo.Trim();

        // Clear resolved taxonomy flags
        if (!string.IsNullOrWhiteSpace(SelectedTransaction.PayeeName) && SelectedTransaction.ReviewStatus.HasFlag(ReviewFlags.MissingPayee))
        {
            SelectedTransaction.ReviewStatus &= ~ReviewFlags.MissingPayee;
        }
        if (!string.IsNullOrWhiteSpace(SelectedTransaction.TagName) && SelectedTransaction.ReviewStatus.HasFlag(ReviewFlags.MissingTag))
        {
            SelectedTransaction.ReviewStatus &= ~ReviewFlags.MissingTag;
        }

        StageOrUpdateCorrection(SelectedTransaction, originalDebit, originalCredit);
        SelectedTransaction.IsStaged = true;

        IsPanelVisible = false;
        SelectedTransaction = null;

        Broker.Send(new ToastNotificationMessage("Changes staged. Click 'Review Edits' to commit.", NotificationType.Success));
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        ClearSplitLines();
        IsPanelVisible = false;
        SelectedTransaction = null;
    }

    // =============================================================================
    // 11. COMMANDS: BULK ACTIONS & TRANSFER LINKING
    // =============================================================================

    [RelayCommand]
    private void OpenBulkActionsModal()
    {
        if (!CanOpenBulkActions) return;
        IsBulkActionsModalVisible = true;
    }

    [RelayCommand]
    private void CloseBulkActionsModal()
    {
        IsBulkActionsModalVisible = false;
    }

    [RelayCommand]
    private async Task ApplyBulkChangesAsync()
    {
        var selectedRows = _rawTransactions.Where(t => t.IsSelected).ToList();
        if (!selectedRows.Any()) return;

        bool hasTag = !string.IsNullOrWhiteSpace(BulkTag);
        bool hasPayee = !string.IsNullOrWhiteSpace(BulkPayee);

        if (!hasTag && !hasPayee)
        {
            Broker.Send(new ToastNotificationMessage("Enter a Tag or Payee to apply in bulk.", NotificationType.Warning));
            return;
        }

        foreach (var row in selectedRows)
        {
            if (hasTag)
            {
                row.TagName = BulkTag.Trim();
                if (row.ReviewStatus.HasFlag(ReviewFlags.MissingTag))
                    row.ReviewStatus &= ~ReviewFlags.MissingTag;
            }

            if (hasPayee)
            {
                row.PayeeName = BulkPayee.Trim();
                if (row.ReviewStatus.HasFlag(ReviewFlags.MissingPayee))
                    row.ReviewStatus &= ~ReviewFlags.MissingPayee;
            }

            StageOrUpdateCorrection(row, row.Debit, row.Credit);
            row.IsStaged = true;
        }

        Broker.Send(new ToastNotificationMessage($"Staged bulk changes for {selectedRows.Count} transactions.", NotificationType.Success));
        CancelSelection();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void CancelSelection()
    {
        _isUpdatingSelection = true;
        try
        {
            foreach (var row in _rawTransactions)
            {
                row.IsSelected = false;
            }
            SelectedCount = 0;
            IsAllSelected = false;
        }
        finally
        {
            _isUpdatingSelection = false;
        }

        BulkTag = string.Empty;
        BulkPayee = string.Empty;
        IsBulkActionsModalVisible = false;
    }

    [RelayCommand]
    private async Task LinkAsTransferAsync()
    {
        var selectedTxns = _rawTransactions.Where(t => t.IsSelected).ToList();
        if (selectedTxns.Count != 2) return;

        var tx1 = selectedTxns[0];
        var tx2 = selectedTxns[1];

        var debitTx = tx1.Debit > 0 ? tx1 : (tx2.Debit > 0 ? tx2 : null);
        var creditTx = tx1.Credit > 0 ? tx1 : (tx2.Credit > 0 ? tx2 : null);

        if (debitTx == null || creditTx == null || debitTx.Id == creditTx.Id)
        {
            Broker.Send(new ToastNotificationMessage("Select one Debit transaction and one Credit transaction to link.", NotificationType.Warning));
            return;
        }

        // Resolve or auto-create the system Transfer tag dynamically
        if (!_tagIdsByName.TryGetValue("Transfer", out int systemTransferTagId))
        {
            systemTransferTagId = await _masterDataOrchestrator.GetOrCreateTagAsync("Transfer");
            _tagIdsByName["Transfer"] = systemTransferTagId;
            _tagNamesById[systemTransferTagId] = "Transfer";
            if (!AvailableTags.Contains("Transfer")) AvailableTags.Add("Transfer");
        }

        await _orchestrator.LinkTransferAsync(debitTx.Id, creditTx.Id, systemTransferTagId);

        CancelSelection();
        await RefreshGridAsync();
    }

    // =============================================================================
    // 12. COMMANDS: SPLIT TRANSACTIONS
    // =============================================================================

    [RelayCommand]
    private void AddSplitLine()
    {
        var line = new SplitLineViewModel
        {
            LineNumber = MockSplitTransactions.Count + 1,
            SplitAmount = SplitRemainingAmount > 0 ? SplitRemainingAmount.ToString("0.00") : "0.00",
            SplitTag = string.Empty,
            SplitPayee = string.Empty
        };

        line.PropertyChanged += OnSplitLinePropertyChanged;
        MockSplitTransactions.Add(line);
        RecalculateSplitSummary();
    }

    [RelayCommand]
    private void RemoveSplitLine(SplitLineViewModel splitLine)
    {
        if (splitLine == null) return;

        splitLine.PropertyChanged -= OnSplitLinePropertyChanged;
        MockSplitTransactions.Remove(splitLine);

        // Re-index remaining split card headers
        for (int i = 0; i < MockSplitTransactions.Count; i++)
        {
            MockSplitTransactions[i].LineNumber = i + 1;
        }

        RecalculateSplitSummary();
    }

    [RelayCommand]
    private async Task CommitSplitAsync()
    {
        if (SelectedTransaction == null || !MockSplitTransactions.Any()) return;

        decimal targetTotal = SelectedTransaction.Debit > 0 ? SelectedTransaction.Debit : SelectedTransaction.Credit;
        decimal currentSplitTotal = 0m;
        var childTransactions = new List<Transaction>();

        foreach (var split in MockSplitTransactions)
        {
            if (decimal.TryParse(split.SplitAmount, out decimal amount) && amount > 0)
            {
                currentSplitTotal += amount;

                int? tagId = (!string.IsNullOrWhiteSpace(split.SplitTag) && _tagIdsByName.TryGetValue(split.SplitTag.Trim(), out var resolvedTagId))
                    ? resolvedTagId
                    : null;

                int? payeeId = (!string.IsNullOrWhiteSpace(split.SplitPayee) && _payeeIdsByName.TryGetValue(split.SplitPayee.Trim(), out var resolvedPayeeId))
                    ? resolvedPayeeId
                    : null;

                childTransactions.Add(new Transaction
                {
                    Description = SelectedTransaction.RawDescription,
                    Debit = SelectedTransaction.Debit > 0 ? amount : 0,
                    Credit = SelectedTransaction.Credit > 0 ? amount : 0,
                    TagId = tagId,
                    PayeeId = payeeId,
                    ReviewStatus = ReviewFlags.None,
                    RawAmountText = SelectedTransaction.Debit > 0 ? $"Dr: {amount:N2}" : $"Cr: {amount:N2}"
                });
            }
        }

        if (childTransactions.Count < 2)
        {
            Broker.Send(new ToastNotificationMessage("Add at least two split lines with valid amounts.", NotificationType.Warning));
            return;
        }

        // FRACTIONAL MATH GUARDRAIL: Auto-correct minor rounding remainders (<= 0.05) on the final line
        decimal difference = targetTotal - currentSplitTotal;
        if (difference != 0 && Math.Abs(difference) <= 0.05m)
        {
            var lastChild = childTransactions.Last();
            if (SelectedTransaction.Debit > 0)
                lastChild.Debit += difference;
            else
                lastChild.Credit += difference;

            lastChild.RawAmountText = SelectedTransaction.Debit > 0
                ? $"Dr: {lastChild.Debit:N2}"
                : $"Cr: {lastChild.Credit:N2}";

            Broker.Send(new ToastNotificationMessage($"Fractional difference of {difference:N2} auto-corrected on the last line.", NotificationType.Info));
        }

        await _orchestrator.SplitTransactionAsync(SelectedTransaction.Id, childTransactions);

        ClearSplitLines();
        IsPanelVisible = false;
        SelectedTransaction = null;
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task RevertSplitAsync()
    {
        if (SelectedTransaction == null || !SelectedTransaction.IsSplitParent) return;

        await _orchestrator.RevertSplitAsync(SelectedTransaction.Id);

        ClearSplitLines();
        IsPanelVisible = false;
        SelectedTransaction = null;
        await RefreshGridAsync();
    }

    // =============================================================================
    // 13. COMMANDS: PAGINATION & IMPORT BATCH
    // =============================================================================

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (!CanGoPrevious) return;

        _currentOffset = Math.Max(0, _currentOffset - SelectedPageSize);
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (!CanGoNext) return;

        _currentOffset += SelectedPageSize;
        await RefreshGridAsync();
    }

    [RelayCommand]
    private async Task RevertBatchAsync()
    {
        if (SelectedImportBatch == null || SelectedImportBatch.Id <= 0)
        {
            Broker.Send(new ToastNotificationMessage("No batch selected to revert.", NotificationType.Warning));
            return;
        }

        await _orchestrator.RevertImportBatchAsync(SelectedImportBatch.Id);

        _isInitializing = true;
        await LoadMasterDataAsync();
        _isInitializing = false;

        _currentOffset = 0;
        await RefreshGridAsync();
    }

    // =============================================================================
    // 14. INTERNAL EVENT HANDLERS & HELPERS
    // =============================================================================

    private void OnTransactionRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection || e.PropertyName != nameof(TransactionRowViewModel.IsSelected))
            return;

        _isUpdatingSelection = true;
        try
        {
            SelectedCount = _rawTransactions.Count(t => t.IsSelected);
            IsAllSelected = _rawTransactions.Count > 0 && SelectedCount == _rawTransactions.Count;
        }
        finally
        {
            _isUpdatingSelection = false;
        }
    }

    private void OnStagedCorrectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        StagedEditsCount = StagingViewModel.StagedCorrections.Count;

        // Sync draft row indicators in case a correction was discarded inside the staging modal
        var stagedIds = StagingViewModel.StagedCorrections
            .Select(c => c.TransactionId)
            .ToHashSet();

        foreach (var row in _rawTransactions)
        {
            row.IsStaged = stagedIds.Contains(row.Id);
        }
    }

    private void OnSplitLinePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitLineViewModel.SplitAmount))
        {
            RecalculateSplitSummary();
        }
    }

    private void ClearSplitLines()
    {
        foreach (var line in MockSplitTransactions)
        {
            line.PropertyChanged -= OnSplitLinePropertyChanged;
        }
        MockSplitTransactions.Clear();
    }

    private void RecalculateSplitSummary()
    {
        SplitTargetAmount = SelectedTransaction == null
            ? 0m
            : (SelectedTransaction.Debit > 0 ? SelectedTransaction.Debit : SelectedTransaction.Credit);

        decimal allocated = 0m;
        int validLines = 0;

        foreach (var line in MockSplitTransactions)
        {
            if (decimal.TryParse(line.SplitAmount, out decimal amt) && amt > 0)
            {
                allocated += amt;
                validLines++;
            }
        }

        SplitAllocatedAmount = allocated;
        SplitRemainingAmount = SplitTargetAmount - allocated;
        CanCommitSplit = validLines >= 2 && Math.Abs(SplitRemainingAmount) <= 0.05m;

        SplitStatusColor = CanCommitSplit
            ? "#22C55E"                                       // Green: Balanced and ready
            : (SplitRemainingAmount < -0.05m ? "#EF4444"      // Red: Over-allocated
                                             : "#F59E0B");    // Amber: Remaining amount to allocate
    }

    private void StageOrUpdateCorrection(TransactionRowViewModel row, decimal originalDebit, decimal originalCredit)
    {
        var existing = StagingViewModel.StagedCorrections.FirstOrDefault(c => c.TransactionId == row.Id);
        if (existing != null)
        {
            existing.Date = row.Date;
            existing.NewPayee = row.PayeeName;
            existing.NewTag = row.TagName;
            existing.CorrectedDebit = row.Debit;
            existing.CorrectedCredit = row.Credit;
        }
        else
        {
            StagingViewModel.StagedCorrections.Add(new TransactionCorrectionRowViewModel
            {
                TransactionId = row.Id,
                Date = row.Date,
                OriginalMerchant = row.SanitizedSource,
                NewPayee = row.PayeeName,
                NewTag = row.TagName,
                OriginalDebit = originalDebit,
                CorrectedDebit = row.Debit,
                OriginalCredit = originalCredit,
                CorrectedCredit = row.Credit
            });
        }
    }

    /// <summary>
    /// Formats an Account into a readable label combining EntityName, AccountType, and masked Account/Card number.
    /// </summary>
    private static string FormatAccountDisplay(Account acc, Dictionary<int, string> entityNamesById)
    {
        var institution = !string.IsNullOrWhiteSpace(acc.EntityName)
            ? acc.EntityName.Trim()
            : (acc.EntityId.HasValue && entityNamesById.TryGetValue(acc.EntityId.Value, out var entityName)
                ? entityName
                : "Account");

        var rawNumber = !string.IsNullOrWhiteSpace(acc.CardNumber)
            ? acc.CardNumber.Trim()
            : acc.AccountNumber?.Trim();

        var masked = !string.IsNullOrWhiteSpace(rawNumber)
            ? (rawNumber.Length > 4 ? $"•••• {rawNumber[^4..]}" : rawNumber)
            : string.Empty;

        if (!string.IsNullOrWhiteSpace(acc.AccountType) && !string.IsNullOrWhiteSpace(masked))
            return $"{institution} ({acc.AccountType}) {masked}";
        if (!string.IsNullOrWhiteSpace(masked))
            return $"{institution} {masked}";
        if (!string.IsNullOrWhiteSpace(acc.AccountType))
            return $"{institution} ({acc.AccountType})";

        return institution;
    }
}

// =============================================================================
// 15. SUPPORTING VIEW MODELS
// =============================================================================

/// <summary>
/// Represents a single row in the Ledger DataGrid with priority-based status icon helpers.
/// </summary>
public partial class TransactionRowViewModel : ObservableObject
{
    private const ReviewFlags HighPriorityFlags =
        ReviewFlags.InvalidAmount | ReviewFlags.InvalidDate | ReviewFlags.InvalidDescription;

    private const ReviewFlags MediumPriorityFlags =
        ReviewFlags.MissingPayee | ReviewFlags.MissingTag;

    [ObservableProperty] private int _id;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isStaged;
    [ObservableProperty] private DateTime _date;
    [ObservableProperty] private string _accountName = string.Empty;
    [ObservableProperty] private string _sanitizedSource = string.Empty;
    [ObservableProperty] private string _rawDescription = string.Empty;
    [ObservableProperty] private string? _payeeName;
    [ObservableProperty] private string? _tagName;
    [ObservableProperty] private decimal _debit;
    [ObservableProperty] private decimal _credit;
    [ObservableProperty] private string? _rawAmountText;
    [ObservableProperty] private decimal _runningBalance;
    [ObservableProperty] private string? _memo;
    [ObservableProperty] private int? _parentTransactionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTransfer))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private int? _linkedTransactionId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSplitParent))]
    [NotifyPropertyChangedFor(nameof(StatusDisplay))]
    [NotifyPropertyChangedFor(nameof(StatusIcon))]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    [NotifyPropertyChangedFor(nameof(StatusTooltip))]
    private ReviewFlags _reviewStatus;

    public bool IsSplitParent => ReviewStatus.HasFlag(ReviewFlags.IsSplitParent);
    public bool IsTransfer => LinkedTransactionId.HasValue;
    public string StatusDisplay => ReviewStatus == ReviewFlags.None ? "Clean" : ReviewStatus.ToString();

    public string StatusIcon
    {
        get
        {
            if ((ReviewStatus & HighPriorityFlags) != 0) return "⚠";
            if ((ReviewStatus & MediumPriorityFlags) != 0) return "⚑";
            if (IsTransfer) return "⇄";
            if (IsSplitParent) return "⑂";
            return "✓";
        }
    }

    public string StatusColor
    {
        get
        {
            if ((ReviewStatus & HighPriorityFlags) != 0) return "#EF4444";   // Red: High Priority Error
            if ((ReviewStatus & MediumPriorityFlags) != 0) return "#F59E0B"; // Amber: Missing Payee/Tag
            if (IsTransfer || IsSplitParent) return "#6366F1";               // Indigo: Linked Transfer / Split
            return "#22C55E";                                                // Green: Clean
        }
    }

    public string StatusTooltip
    {
        get
        {
            var notes = new List<string>();
            if (ReviewStatus.HasFlag(ReviewFlags.InvalidAmount)) notes.Add("Invalid Amount");
            if (ReviewStatus.HasFlag(ReviewFlags.InvalidDate)) notes.Add("Invalid Date");
            if (ReviewStatus.HasFlag(ReviewFlags.InvalidDescription)) notes.Add("Invalid Description");
            if (ReviewStatus.HasFlag(ReviewFlags.MissingPayee)) notes.Add("Missing Payee");
            if (ReviewStatus.HasFlag(ReviewFlags.MissingTag)) notes.Add("Missing Tag");
            if (IsSplitParent) notes.Add("Split Parent");
            if (IsTransfer) notes.Add("Linked Transfer");

            return notes.Count == 0 ? "Clean" : string.Join(" • ", notes);
        }
    }

    public static TransactionRowViewModel FromEntity(Transaction txn, string accountName, string? payeeName, string? tagName)
    {
        return new TransactionRowViewModel
        {
            Id = txn.Id,
            Date = txn.Date,
            AccountName = accountName,
            SanitizedSource = txn.Source,
            RawDescription = txn.Description,
            PayeeName = payeeName,
            TagName = tagName,
            Debit = txn.Debit,
            Credit = txn.Credit,
            ReviewStatus = txn.ReviewStatus,
            RawAmountText = txn.RawAmountText,
            RunningBalance = txn.RunningBalance,
            Memo = txn.Memo,
            ParentTransactionId = txn.ParentTransactionId,
            LinkedTransactionId = txn.LinkedTransactionId
        };
    }
}

public class AccountFilterItem
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public class ImportBatchFilterItem
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single split allocation line inside the Slide-In Edit Panel.
/// </summary>
public partial class SplitLineViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineHeader))]
    private int _lineNumber = 1;

    [ObservableProperty]
    private string _splitAmount = string.Empty;

    [ObservableProperty]
    private string _splitTag = string.Empty;

    [ObservableProperty]
    private string _splitPayee = string.Empty;

    public string LineHeader => $"Split #{LineNumber}";
}