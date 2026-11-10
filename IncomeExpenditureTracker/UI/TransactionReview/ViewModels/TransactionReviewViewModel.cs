using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.TransactionReview;

/// <summary>
/// ViewModel for the Hybrid Ledger where users review transactions, triage errors, and perform inline edits.
/// Designed to work with keyset cursor paging and delta patching for optimal performance.
/// </summary>
public partial class TransactionReviewViewModel : ViewModelBase
{
    private readonly ITransactionReviewOrchestrator _orchestrator;

    // =============================================================================
    // FILTER & SEARCH PROPERTIES (Top Action Bar - Zone 1)
    // =============================================================================

    /// <summary>
    /// When enabled, filters the grid to show only transactions with ReviewStatus > 0.
    /// This allows users to focus exclusively on rows requiring attention.
    /// </summary>
    [ObservableProperty]
    private bool _isTriageMode = false;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private DateTime? _startDate = null;

    [ObservableProperty]
    private DateTime? _endDate = null;

    [ObservableProperty]
    private int? _selectedAccountId = null;

    /// <summary>
    /// Number of staged edits pending commit to the database.
    /// Displayed in the "Review X Edits" button badge.
    /// </summary>
    [ObservableProperty]
    private int _stagedEditsCount = 0;

    /// <summary>
    /// Computed property to control visibility of the "Review X Edits" button.
    /// </summary>
    public bool HasStagedEdits => _stagedEditsCount > 0;

    // =============================================================================
    // DATAGRID DATA SOURCE (Main Grid - Zone 2)
    // =============================================================================

    /// <summary>
    /// The main observable collection bound to the DataGrid.
    /// Populated with mock data for UI preview purposes.
    /// In production, this will be updated via delta patching when background sweeps occur.
    /// </summary>
    public ObservableCollection<TransactionRowViewModel> Transactions { get; } = new();

    // =============================================================================
    // SLIDE-IN PANEL PROPERTIES (Right Panel - Zone 3)
    // =============================================================================

    /// <summary>
    /// Controls the visibility of the right-side edit panel.
    /// True when a row is selected from the DataGrid.
    /// </summary>
    [ObservableProperty]
    private bool _isPanelVisible = false;

    /// <summary>
    /// The currently selected transaction for inline editing.
    /// Null when no row is selected or the panel is closed.
    /// </summary>
    [ObservableProperty]
    private TransactionRowViewModel? _selectedTransaction = null;

    /// <summary>
    /// Editable Date field in the slide-in panel.
    /// Bound to a DatePicker for user corrections.
    /// </summary>
    [ObservableProperty]
    private DateTime _editDate = DateTime.Today;

    /// <summary>
    /// Editable Debit field in the slide-in panel.
    /// Bound to a TextBox for user corrections.
    /// </summary>
    [ObservableProperty]
    private decimal _editDebit = 0m;

    /// <summary>
    /// Editable Credit field in the slide-in panel.
    /// Bound to a TextBox for user corrections.
    /// </summary>
    [ObservableProperty]
    private decimal _editCredit = 0m;

    /// <summary>
    /// Editable Payee autocomplete field.
    /// Populated with available payees from the database.
    /// </summary>
    [ObservableProperty]
    private string _editPayee = string.Empty;

    /// <summary>
    /// Editable Tag autocomplete field.
    /// Populated with available tags from the database.
    /// </summary>
    [ObservableProperty]
    private string _editTag = string.Empty;

    /// <summary>
    /// Editable Memo field for user notes.
    /// Allows users to add personal comments to transactions.
    /// </summary>
    [ObservableProperty]
    private string _editMemo = string.Empty;

    /// <summary>
    /// Raw amount text extracted from the selected transaction.
    /// Displayed in the slide-in panel for audit trail purposes.
    /// </summary>
    [ObservableProperty]
    private string _rawAmountText = string.Empty;

    // =============================================================================
    // AUTOCOMPLETE DATA SOURCES
    // =============================================================================

    /// <summary>
    /// Mock data source for the Payee AutoCompleteBox.
    /// In production, this will be fetched from the database via IMasterDataOrchestrator.
    /// </summary>
    public ObservableCollection<string> AvailablePayees { get; } = new();

    /// <summary>
    /// Mock data source for the Tag AutoCompleteBox.
    /// In production, this will be fetched from the database via IMasterDataOrchestrator.
    /// </summary>
    public ObservableCollection<string> AvailableTags { get; } = new();

    /// <summary>
    /// Mock data source for the Account ComboBox in the top filter bar.
    /// In production, this will be fetched from the database via IAccountService.
    /// </summary>
    public ObservableCollection<AccountFilterItem> AvailableAccounts { get; } = new();

    private Account _selectedAccount;
    public Account SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            SetProperty(ref _selectedAccount, value);
            SelectedAccountId = value?.Id ?? 0;
        }
    }

    // =============================================================================
    // NEW FILTERS (Import Batch, Group By)
    // =============================================================================

    /// <summary>
    /// Mock data source for the Import Batch ComboBox in the top filter bar.
    /// In production, this will be fetched from the database via IImportBatchService.
    /// </summary>
    public ObservableCollection<ImportBatchFilterItem> AvailableImportBatches { get; } = new();

    [ObservableProperty]
    private ImportBatchFilterItem? _selectedImportBatch = null;

    /// <summary>
    /// Controls the visibility of the "Revert Batch" button.
    /// Only visible when a specific batch is selected.
    /// </summary>
    public bool IsBatchSelected => _selectedImportBatch != null;

    /// <summary>
    /// Mock data source for the Group By ComboBox.
    /// </summary>
    public ObservableCollection<string> GroupByOptions { get; } = new();

    [ObservableProperty]
    private string _selectedGroupBy = "None";

    // =============================================================================
    // BULK ACTIONS PROPERTIES
    // =============================================================================

    /// <summary>
    /// Count of currently selected transactions for bulk updates.
    /// Used to control visibility of the bulk actions bar.
    /// </summary>
    [ObservableProperty]
    private int _selectedCount = 0;

    /// <summary>
    /// Computed property to control visibility of the bulk actions bar.
    /// </summary>
    public bool HasSelectedTransactions => _selectedCount > 0;

    /// <summary>
    /// Display text for the selected count (e.g., "5 Transactions Selected").
    /// </summary>
    public string SelectedCountText => $"{_selectedCount} {(_selectedCount == 1 ? "Transaction" : "Transactions")} Selected";

    /// <summary>
    /// Tag to apply to all selected transactions in bulk.
    /// </summary>
    [ObservableProperty]
    private string _bulkTag = string.Empty;

    /// <summary>
    /// Payee to apply to all selected transactions in bulk.
    /// </summary>
    [ObservableProperty]
    private string _bulkPayee = string.Empty;

    // =============================================================================
    // PAGINATION PROPERTIES
    // =============================================================================

    /// <summary>
    /// Display text for the current page info (e.g., "Showing 1-50 of 2,450 transactions").
    /// </summary>
    [ObservableProperty]
    private string _pageInfo = "Showing 1-50 of 2,450 transactions";

    /// <summary>
    /// Available page size options for the ComboBox.
    /// </summary>
    public ObservableCollection<int> PageSizeOptions { get; } = new();

    [ObservableProperty]
    private int _selectedPageSize = 50;

    /// <summary>
    /// Controls whether the "Previous" button is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _canGoPrevious = false;

    /// <summary>
    /// Controls whether the "Next" button is enabled.
    /// </summary>
    [ObservableProperty]
    private bool _canGoNext = true;

    // =============================================================================
    // SPLIT TRANSACTION MOCK DATA
    // =============================================================================

    /// <summary>
    /// Mock collection for split transaction lines.
    /// In production, this would be bound to a real split transaction model.
    /// </summary>
    public ObservableCollection<SplitLineViewModel> MockSplitTransactions { get; } = new();

    // =============================================================================
    // STAGING REVIEW MODAL CONTROL
    // =============================================================================

    /// <summary>
    /// Controls visibility of the StagingReviewModal full-screen overlay.
    /// </summary>
    private bool _isStagingModalVisible = false;
    public bool IsStagingModalVisible
    {
        get => _isStagingModalVisible;
        set => SetProperty(ref _isStagingModalVisible, value);
    }

    public StagingReviewModalViewModel StagingViewModel { get; }

    // =============================================================================
    // CONSTRUCTOR & INITIALIZATION
    // =============================================================================

    public TransactionReviewViewModel(
        ITransactionReviewOrchestrator orchestrator,
        IApplicationBroker broker)
        : base(broker)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        // Populate mock data for UI preview
        InitializeMockData();

        StagingViewModel = new StagingReviewModalViewModel(orchestrator, broker)
        {
            CloseAction = () => IsStagingModalVisible = false
        };
    }

    /// <summary>
    /// Populates the ViewModel with dummy mock data so the UI can be previewed immediately.
    /// This method will be removed once real data fetching is implemented.
    /// </summary>
    private void InitializeMockData()
    {
        // Mock Payees for AutoCompleteBox
        AvailablePayees.Add("Amazon");
        AvailablePayees.Add("Zomato");
        AvailablePayees.Add("Swiggy");
        AvailablePayees.Add("Infosys Ltd");
        AvailablePayees.Add("Netflix");
        AvailablePayees.Add("Uber");
        AvailablePayees.Add("Groww");

        // Mock Tags for AutoCompleteBox
        AvailableTags.Add("Food & Dining");
        AvailableTags.Add("Salary");
        AvailableTags.Add("Investment");
        AvailableTags.Add("Entertainment");
        AvailableTags.Add("Transport");
        AvailableTags.Add("Shopping");
        AvailableTags.Add("Utilities");

        // Mock Accounts for ComboBox
        AvailableAccounts.Add(new AccountFilterItem { Id = 1, DisplayName = "HDFC Savings - 4532" });
        AvailableAccounts.Add(new AccountFilterItem { Id = 2, DisplayName = "ICICI Credit Card - 8876" });
        AvailableAccounts.Add(new AccountFilterItem { Id = 3, DisplayName = "SBI Salary Account - 1209" });

        // Mock Import Batches for ComboBox
        AvailableImportBatches.Add(new ImportBatchFilterItem { Id = 1, DisplayName = "Nov 2026 - HDFC Statement (500 txns)" });
        AvailableImportBatches.Add(new ImportBatchFilterItem { Id = 2, DisplayName = "Oct 2026 - ICICI Statement (320 txns)" });
        AvailableImportBatches.Add(new ImportBatchFilterItem { Id = 3, DisplayName = "Sep 2026 - SBI Statement (450 txns)" });

        // Mock Group By Options
        GroupByOptions.Add("None");
        GroupByOptions.Add("Date");
        GroupByOptions.Add("Payee");
        GroupByOptions.Add("Account");

        // Mock Page Size Options
        PageSizeOptions.Add(50);
        PageSizeOptions.Add(100);
        PageSizeOptions.Add(200);

        // Mock Transactions for DataGrid
        Transactions.Add(new TransactionRowViewModel
        {
            Id = 1,
            Date = new DateTime(2026, 11, 8),
            AccountName = "HDFC Savings",
            SanitizedSource = "Zomato",
            RawDescription = "UPI/P2M/123456789/ZOMATO LTD/Food Order/HDFC BANK",
            PayeeName = "Zomato",
            TagName = "Food & Dining",
            Debit = 450.00m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.None,
            RawAmountText = "Dr: 450.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 2,
            Date = new DateTime(2026, 11, 7),
            AccountName = "SBI Salary Account",
            SanitizedSource = "Infosys Ltd",
            RawDescription = "NEFT-INFOSYS LIMITED-SALARY CREDIT NOV 2026",
            PayeeName = "Infosys Ltd",
            TagName = "Salary",
            Debit = 0m,
            Credit = 95000.00m,
            ReviewStatus = ReviewFlags.None,
            RawAmountText = "Cr: 95000.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 3,
            Date = new DateTime(2026, 11, 6),
            AccountName = "ICICI Credit Card",
            SanitizedSource = "Amazon Pay",
            RawDescription = "AMAZON PAY INDIA PVT LTD BANGALORE",
            PayeeName = null, // Missing Payee
            TagName = null, // Missing Tag
            Debit = 1299.00m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.MissingPayee | ReviewFlags.MissingTag,
            RawAmountText = "Dr: 1299.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 4,
            Date = new DateTime(2026, 11, 5),
            AccountName = "HDFC Savings",
            SanitizedSource = "Netflix",
            RawDescription = "NETFLIX.COM BILL PAYMENT",
            PayeeName = "Netflix",
            TagName = "Entertainment",
            Debit = 649.00m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.None,
            RawAmountText = "Dr: 649.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 5,
            Date = new DateTime(2026, 11, 4),
            AccountName = "HDFC Savings",
            SanitizedSource = "Groww Invest",
            RawDescription = "UPI/P2M/987654321/GROWW INVEST TECH PVT/MF Purchase/HDFC",
            PayeeName = "Groww",
            TagName = null, // Missing Tag
            Debit = 5000.00m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.MissingTag,
            RawAmountText = "Dr: 5000.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 6,
            Date = new DateTime(2026, 11, 3),
            AccountName = "ICICI Credit Card",
            SanitizedSource = "Uber India",
            RawDescription = "UBER INDIA SYSTEMS PVT LTD BANGALORE",
            PayeeName = "Uber",
            TagName = "Transport",
            Debit = 285.00m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.None,
            RawAmountText = "Dr: 285.00"
        });

        Transactions.Add(new TransactionRowViewModel
        {
            Id = 7,
            Date = new DateTime(2026, 11, 2),
            AccountName = "HDFC Savings",
            SanitizedSource = "Unknown Merchant",
            RawDescription = "POS PURCHASE-INVALID AMOUNT TEXT",
            PayeeName = null,
            TagName = null,
            Debit = 0m,
            Credit = 0m,
            ReviewStatus = ReviewFlags.InvalidAmount | ReviewFlags.MissingPayee | ReviewFlags.MissingTag,
            RawAmountText = "INVALID"
        });
    }

    // =============================================================================
    // COMMANDS (User Actions)
    // =============================================================================

    [RelayCommand]
    private async Task ReviewPendingEditsAsync()
    {
        if (StagedEditsCount == 0) return;

        IsStagingModalVisible = true;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Triggered when the user selects a row in the DataGrid.
    /// Opens the right slide-in panel and populates editable fields with the selected transaction's data.
    /// </summary>
    [RelayCommand]
    private void SelectTransaction(TransactionRowViewModel? transaction)
    {
        if (transaction == null)
        {
            // Deselection: Close the panel
            IsPanelVisible = false;
            SelectedTransaction = null;
            return;
        }

        // Open the panel and populate edit fields
        SelectedTransaction = transaction;
        EditDate = transaction.Date;
        EditDebit = transaction.Debit;
        EditCredit = transaction.Credit;
        EditPayee = transaction.PayeeName ?? string.Empty;
        EditTag = transaction.TagName ?? string.Empty;
        EditMemo = string.Empty; // Reset memo
        RawAmountText = transaction.RawAmountText ?? string.Empty; // Populate raw amount text
        IsPanelVisible = true;

        // Initialize mock split transactions
        MockSplitTransactions.Clear();
    }

    /// <summary>
    /// Commits the staged edits to the in-memory collection and marks them for batch persistence.
    /// This does NOT write to the database immediately; instead, it stages the changes
    /// until the user clicks "Review X Edits" to trigger ApplyCorrectionsAsync.
    /// </summary>
    [RelayCommand]
    private async Task StageUpdatesAsync()
    {
        if (SelectedTransaction == null) return;

        // Apply the edits to the in-memory ViewModel
        // In production, this would mark the transaction as "dirty" and increment PendingEditCount
        SelectedTransaction.Date = EditDate;
        SelectedTransaction.Debit = EditDebit;
        SelectedTransaction.Credit = EditCredit;
        SelectedTransaction.PayeeName = EditPayee;
        SelectedTransaction.TagName = EditTag;

        // Clear any review flags if the user corrected the issues
        if (!string.IsNullOrWhiteSpace(EditPayee) && SelectedTransaction.ReviewStatus.HasFlag(ReviewFlags.MissingPayee))
        {
            SelectedTransaction.ReviewStatus &= ~ReviewFlags.MissingPayee;
        }
        if (!string.IsNullOrWhiteSpace(EditTag) && SelectedTransaction.ReviewStatus.HasFlag(ReviewFlags.MissingTag))
        {
            SelectedTransaction.ReviewStatus &= ~ReviewFlags.MissingTag;
        }

        // Increment staged edits count
        StagedEditsCount++;
        OnPropertyChanged(nameof(HasStagedEdits)); // Notify UI to update button visibility

        // Close the panel
        IsPanelVisible = false;
        SelectedTransaction = null;

        // Show a toast notification
        Broker.Send(new ToastNotificationMessage("Changes staged. Click 'Review Edits' to commit.", NotificationType.Success));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Discards the current edits and closes the slide-in panel.
    /// </summary>
    [RelayCommand]
    private void CancelEdit()
    {
        IsPanelVisible = false;
        SelectedTransaction = null;
    }

    /// <summary>
    /// Applies the Triage Mode filter by reloading the grid with IsTriageMode = true.
    /// This will fetch only transactions with ReviewStatus > 0 from the database.
    /// </summary>
    [RelayCommand]
    private async Task ToggleTriageModeAsync()
    {
        // In production, this would trigger a new fetch from the orchestrator
        // with TransactionFilterArgs.IsTriageMode = IsTriageMode
        await RefreshGridAsync();
    }

    /// <summary>
    /// Refreshes the DataGrid by fetching transactions from the orchestrator based on current filters.
    /// This is a placeholder for the actual keyset cursor paging logic.
    /// </summary>
    [RelayCommand]
    private async Task RefreshGridAsync()
    {
        // Placeholder: In production, this would call:
        // var filters = new TransactionFilterArgs(
        //     AccountId: SelectedAccountId,
        //     SearchText: SearchText,
        //     IsTriageMode: IsTriageMode,
        //     Limit: 50,
        //     Offset: 0
        // );
        // var result = await _orchestrator.GetTransactionsAsync(filters);
        // Then perform delta patching on the Transactions collection

        Broker.Send(new ToastNotificationMessage("Grid refresh triggered (mock)", NotificationType.Info));
        await Task.CompletedTask;
    }

    // =============================================================================
    // BULK ACTIONS COMMANDS
    // =============================================================================

    /// <summary>
    /// Applies the bulk tag and/or payee to all selected transactions.
    /// This stages the changes for commit.
    /// </summary>
    [RelayCommand]
    private async Task ApplyBulkChangesAsync()
    {
        // In production, this would iterate through all selected transactions
        // and apply the BulkTag and BulkPayee values
        Broker.Send(new ToastNotificationMessage($"Bulk changes applied to {SelectedCount} transactions (mock)", NotificationType.Success));

        // Reset bulk fields and clear selection
        BulkTag = string.Empty;
        BulkPayee = string.Empty;
        SelectedCount = 0;
        OnPropertyChanged(nameof(HasSelectedTransactions));
        OnPropertyChanged(nameof(SelectedCountText));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the current selection and hides the bulk actions bar.
    /// </summary>
    [RelayCommand]
    private void CancelSelection()
    {
        // In production, this would clear the IsSelected flag on all transactions
        SelectedCount = 0;
        BulkTag = string.Empty;
        BulkPayee = string.Empty;
        OnPropertyChanged(nameof(HasSelectedTransactions));
        OnPropertyChanged(nameof(SelectedCountText));
    }

    // =============================================================================
    // PAGINATION COMMANDS
    // =============================================================================

    /// <summary>
    /// Navigates to the previous page of transactions using keyset cursor paging.
    /// </summary>
    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        // In production, this would trigger a keyset cursor fetch with the previous boundary
        Broker.Send(new ToastNotificationMessage("Previous page loaded (mock)", NotificationType.Info));
        CanGoPrevious = false; // Mock: disable for demo
        CanGoNext = true;
        await Task.CompletedTask;
    }

    /// <summary>
    /// Navigates to the next page of transactions using keyset cursor paging.
    /// </summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        // In production, this would trigger a keyset cursor fetch with the next boundary
        Broker.Send(new ToastNotificationMessage("Next page loaded (mock)", NotificationType.Info));
        CanGoPrevious = true;
        CanGoNext = true;
        await Task.CompletedTask;
    }

    // =============================================================================
    // SPLIT TRANSACTION COMMANDS
    // =============================================================================

    /// <summary>
    /// Adds a new split line to the MockSplitTransactions collection.
    /// </summary>
    [RelayCommand]
    private void AddSplitLine()
    {
        MockSplitTransactions.Add(new SplitLineViewModel
        {
            SplitAmount = "0.00",
            SplitTag = string.Empty,
            SplitPayee = string.Empty
        });
    }

    /// <summary>
    /// Removes a split line from the MockSplitTransactions collection.
    /// </summary>
    [RelayCommand]
    private void RemoveSplitLine(SplitLineViewModel splitLine)
    {
        if (splitLine != null)
        {
            MockSplitTransactions.Remove(splitLine);
        }
    }

    // =============================================================================
    // IMPORT BATCH COMMANDS
    // =============================================================================

    /// <summary>
    /// Reverts all transactions from the selected import batch.
    /// This is a destructive operation that requires confirmation.
    /// </summary>
    [RelayCommand]
    private async Task RevertBatchAsync()
    {
        if (SelectedImportBatch == null)
        {
            Broker.Send(new ToastNotificationMessage("No batch selected to revert.", NotificationType.Warning));
            return;
        }

        // In production, this would show a confirmation modal and then call
        // the orchestrator to delete all transactions from the selected batch
        Broker.Send(new ToastNotificationMessage($"Batch '{SelectedImportBatch.DisplayName}' reverted (mock)", NotificationType.Success));
        SelectedImportBatch = null;
        OnPropertyChanged(nameof(IsBatchSelected));

        await Task.CompletedTask;
    }

    /// <summary>
    /// Triggered when the user types into the global search TextBox.
    /// Debounced in production to avoid excessive database queries.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        // In production, this would trigger RefreshGridAsync with the search text
        await RefreshGridAsync();
    }
}

// =============================================================================
// SUPPORTING VIEW MODELS
// =============================================================================

/// <summary>
/// Represents a single row in the DataGrid.
/// This is a lightweight ViewModel that mirrors the Transaction entity but adds UI-specific properties.
/// </summary>
public class TransactionRowViewModel : ObservableObject
{
    private int _id;
    private bool _isSelected;
    private DateTime _date;
    private string _accountName = string.Empty;
    private string _sanitizedSource = string.Empty;
    private string _rawDescription = string.Empty;
    private string? _payeeName;
    private string? _tagName;
    private decimal _debit;
    private decimal _credit;
    private ReviewFlags _reviewStatus;
    private string? _rawAmountText;

    public int Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public string AccountName
    {
        get => _accountName;
        set => SetProperty(ref _accountName, value);
    }

    public string SanitizedSource
    {
        get => _sanitizedSource;
        set => SetProperty(ref _sanitizedSource, value);
    }

    public string RawDescription
    {
        get => _rawDescription;
        set => SetProperty(ref _rawDescription, value);
    }

    public string? PayeeName
    {
        get => _payeeName;
        set => SetProperty(ref _payeeName, value);
    }

    public string? TagName
    {
        get => _tagName;
        set => SetProperty(ref _tagName, value);
    }

    public decimal Debit
    {
        get => _debit;
        set => SetProperty(ref _debit, value);
    }

    public decimal Credit
    {
        get => _credit;
        set => SetProperty(ref _credit, value);
    }

    public ReviewFlags ReviewStatus
    {
        get => _reviewStatus;
        set => SetProperty(ref _reviewStatus, value);
    }

    public string? RawAmountText
    {
        get => _rawAmountText;
        set => SetProperty(ref _rawAmountText, value);
    }

    /// <summary>
    /// Computed property to display the review status as a human-readable string.
    /// Used in the DataGrid Status column.
    /// </summary>
    public string StatusDisplay => ReviewStatus == ReviewFlags.None ? "Clean" : ReviewStatus.ToString();
}

/// <summary>
/// Represents an account filter item for the ComboBox in the top action bar.
/// </summary>
public class AccountFilterItem
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Represents an import batch filter item for the ComboBox in the top action bar.
/// </summary>
public class ImportBatchFilterItem
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single split line in the split transaction UI.
/// This is a mock ViewModel for UI preview purposes.
/// </summary>
public class SplitLineViewModel : ObservableObject
{
    private string _splitAmount = string.Empty;
    private string _splitTag = string.Empty;
    private string _splitPayee = string.Empty;

    public string SplitAmount
    {
        get => _splitAmount;
        set => SetProperty(ref _splitAmount, value);
    }

    public string SplitTag
    {
        get => _splitTag;
        set => SetProperty(ref _splitTag, value);
    }

    public string SplitPayee
    {
        get => _splitPayee;
        set => SetProperty(ref _splitPayee, value);
    }
}
