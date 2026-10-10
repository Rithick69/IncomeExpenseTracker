using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.ImportHub;

/// <summary>
/// ViewModel for the Sheet Preview Workbench.
/// Allows users to review extracted transactions, edit account metadata, and map columns before database commit.
/// Implements delta-patch updates and bitwise ReviewFlags for transaction error highlighting.
/// </summary>
public partial class SheetPreviewViewModel : ViewModelBase
{
    private readonly IStatementManager _statementManager;
    private Guid _currentFileId;
    private StatementPreview? _originalPreview;

    // =========================================================================
    // ACCOUNT METADATA (Editable Form Fields)
    // =========================================================================
    [ObservableProperty]
    private string _bankName = string.Empty;

    [ObservableProperty]
    private string _accountNumber = string.Empty;

    [ObservableProperty]
    private string _targetSheetName = string.Empty;

    [ObservableProperty]
    private string _currentFileName = string.Empty;

    // =========================================================================
    // COLUMN MAPPING STATE (The Dictionary exposed for ComboBox bindings)
    // =========================================================================
    // Key: Domain field name (e.g., "Date", "Description", "Debit")
    // Value: DetectedField containing column index and metadata
    public Dictionary<string, DetectedField> FieldMappings { get; private set; } = new();

    /// <summary>
    /// Collection of all raw column headers from the Excel sheet for ComboBox binding.
    /// Each string represents a possible column header that can be mapped to domain fields.
    /// </summary>
    public ObservableCollection<string> AvailableHeaders { get; } = new();

    // =========================================================================
    // INDIVIDUAL FIELD MAPPINGS (For XAML ComboBox Bindings)
    // Avalonia does not support dictionary indexing syntax, so we expose individual properties
    // =========================================================================
    [ObservableProperty]
    private string _selectedDateHeader = string.Empty;

    [ObservableProperty]
    private string _selectedDescriptionHeader = string.Empty;

    [ObservableProperty]
    private string _selectedDebitHeader = string.Empty;

    [ObservableProperty]
    private string _selectedCreditHeader = string.Empty;

    [ObservableProperty]
    private string _selectedAmountHeader = string.Empty;

    // Track user corrections for synonym learning
    private readonly List<ColumnMappingCorrection> _corrections = new();

    // =========================================================================
    // TRANSACTION PREVIEW GRID (The DataGrid ItemsSource)
    // =========================================================================
    public ObservableCollection<TransactionPreviewRow> TransactionRows { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "Ready";

    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public SheetPreviewViewModel(IStatementManager statementManager, IApplicationBroker broker)
        : base(broker)
    {
        _statementManager = statementManager ?? throw new ArgumentNullException(nameof(statementManager));
    }

    // =========================================================================
    // PUBLIC METHODS (Called by Parent ImportHubViewModel)
    // =========================================================================

    /// <summary>
    /// Loads the preview for a staged file and populates the form + grid.
    /// </summary>
    public async Task LoadPreviewAsync(Guid fileId, string fileName, string? targetSheet = null)
    {
        if (IsBusy) return;

        _currentFileId = fileId;
        CurrentFileName = fileName;
        IsBusy = true;
        StatusText = $"Analyzing {fileName}...";

        try
        {
            // Retrieve the extraction from StatementManager
            _originalPreview = await _statementManager.PreviewStagedFileAsync(fileId, targetSheet);

            // Populate Account Metadata from DetectedFields
            if (_originalPreview.Fields.TryGetValue("EntityName", out var bankField))
            {
                BankName = bankField.ExtractedValue;
            }

            if (_originalPreview.Fields.TryGetValue("AccountNumber", out var accountField))
            {
                AccountNumber = accountField.ExtractedValue;
            }

            TargetSheetName = targetSheet ?? _originalPreview.FileName;

            // Copy the Fields dictionary for UI binding (ComboBoxes will mutate this)
            FieldMappings = new Dictionary<string, DetectedField>(_originalPreview.Fields, StringComparer.OrdinalIgnoreCase);

            // Populate AvailableHeaders from the detected fields (raw column names)
            // This provides the ComboBox options for column mapping
            AvailableHeaders.Clear();
            var uniqueHeaders = _originalPreview.Fields.Values
                .Where(f => !string.IsNullOrWhiteSpace(f.ColumnName))
                .Select(f => f.ColumnName)
                .Distinct()
                .OrderBy(h => h);
            foreach (var header in uniqueHeaders)
            {
                AvailableHeaders.Add(header);
            }

            // Populate the individual observable properties for XAML binding
            // These bridge the dictionary to simple bindable properties
            // Using the public property setter to trigger INotifyPropertyChanged
            if (FieldMappings.TryGetValue("Date", out var dateField))
            {
                SelectedDateHeader = dateField.ColumnName;
            }

            if (FieldMappings.TryGetValue("Description", out var descField))
            {
                SelectedDescriptionHeader = descField.ColumnName;
            }

            if (FieldMappings.TryGetValue("Debit", out var debitField))
            {
                SelectedDebitHeader = debitField.ColumnName;
            }

            if (FieldMappings.TryGetValue("Credit", out var creditField))
            {
                SelectedCreditHeader = creditField.ColumnName;
            }

            if (FieldMappings.TryGetValue("Amount", out var amountField))
            {
                SelectedAmountHeader = amountField.ColumnName;
            }

            // Build the transaction preview rows for the DataGrid
            TransactionRows.Clear();
            foreach (var txn in _originalPreview.PreviewTransactions)
            {
                // Calculate ReviewFlags based on transaction data integrity
                var flags = CalculateReviewFlags(txn);
                TransactionRows.Add(new TransactionPreviewRow
                {
                    Date = txn.Date,
                    Description = txn.Description,
                    Debit = txn.Debit,
                    Credit = txn.Credit,
                    Amount = txn.Amount,
                    ReviewStatus = flags
                });
            }

            StatusText = $"Preview loaded. {TransactionRows.Count} transactions found.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load preview: {ex.Message}";
            Broker.Send(new ToastNotificationMessage($"Preview Error: {ex.Message}", NotificationType.Error));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Calculates bitwise ReviewFlags for a transaction based on data integrity.
    /// </summary>
    private ReviewFlags CalculateReviewFlags(TransactionPreview txn)
    {
        var flags = ReviewFlags.None;

        // Check for invalid date (default DateTime or far future/past)
        if (txn.Date == default || txn.Date.Year < 1900 || txn.Date.Year > 2100)
        {
            flags |= ReviewFlags.InvalidDate;
        }

        // Check for invalid amounts (both debit and credit are zero or both are non-zero)
        if ((txn.Debit == 0 && txn.Credit == 0) || (txn.Debit != 0 && txn.Credit != 0))
        {
            flags |= ReviewFlags.InvalidAmount;
        }

        // Check for missing description
        if (string.IsNullOrWhiteSpace(txn.Description))
        {
            flags |= ReviewFlags.InvalidDescription;
        }

        return flags;
    }

    // =========================================================================
    // COMMANDS (UI Buttons)
    // =========================================================================

    /// <summary>
    /// Called when the user updates a column mapping in the UI ComboBox.
    /// Tracks the change for synonym learning.
    /// This is invoked directly from UI events, not as a command binding.
    /// </summary>
    public void OnColumnMappingChanged(string fieldName, int newColumnIndex)
    {
        if (!FieldMappings.TryGetValue(fieldName, out var field))
        {
            return;
        }

        // Only track if the user actually changed it from the original
        if (_originalPreview?.Fields.TryGetValue(fieldName, out var originalField) == true &&
            originalField.ColumnIndex != newColumnIndex)
        {
            // Remove any previous correction for this field
            _corrections.RemoveAll(c => c.TargetField == fieldName);

            // Add new correction
            _corrections.Add(new ColumnMappingCorrection
            {
                TargetField = fieldName,
                RawHeaderName = originalField.ColumnName,
                NewColumnIndex = newColumnIndex,
                Category = DetermineFieldCategory(fieldName)
            });
        }

        // Update the current mapping
        field.ColumnIndex = newColumnIndex;
        field.IsUserVerified = true;
    }

    /// <summary>
    /// Determines the category for synonym learning (TRANSACTION vs METADATA).
    /// </summary>
    private string DetermineFieldCategory(string fieldName)
    {
        var transactionFields = new[] { "Date", "Description", "Debit", "Credit", "Amount" };
        return transactionFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)
            ? "TRANSACTION"
            : "METADATA";
    }

    /// <summary>
    /// Confirms the import and commits to database under a single transaction.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmImportAsync()
    {
        if (IsBusy || _currentFileId == Guid.Empty || _originalPreview == null)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Committing to database...";

        try
        {
            // Update the preview with user-edited metadata
            if (FieldMappings.TryGetValue("EntityName", out var bankField))
            {
                bankField.ExtractedValue = BankName;
                bankField.IsUserVerified = true;
            }

            if (FieldMappings.TryGetValue("AccountNumber", out var accountField))
            {
                accountField.ExtractedValue = AccountNumber;
                accountField.IsUserVerified = true;
            }

            // Build the PreviewTracker for commit
            var tracker = new PreviewTracker
            {
                SheetName = TargetSheetName,
                FinalPreview = new StatementPreview
                {
                    FileName = CurrentFileName,
                    Fields = FieldMappings,
                    HeaderRow = _originalPreview.HeaderRow,
                    PreviewTransactions = _originalPreview.PreviewTransactions,
                    ConfidenceScore = _originalPreview.ConfidenceScore
                },
                ColumnCorrections = _corrections
            };

            // Master Transaction: Commit all sheets under a single SQLite transaction token
            await _statementManager.CommitStagedBatchAsync(_currentFileId, new[] { tracker });

            StatusText = "Import successful!";
            Broker.Send(new ToastNotificationMessage($"Successfully imported {TransactionRows.Count} transactions.", NotificationType.Success));

            // Notify parent to return to queue state
            Broker.Send(new NavigationMessage("ImportHub"));

            // Clear state
            ClearState();
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            Broker.Send(new ToastNotificationMessage($"Import Error: {ex.Message}", NotificationType.Error));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Discards the current preview and returns to the queue.
    /// </summary>
    [RelayCommand]
    public void DiscardPreview()
    {
        if (_currentFileId != Guid.Empty)
        {
            _statementManager.DiscardFile(_currentFileId);
            StatusText = "Preview discarded.";
            Broker.Send(new ToastNotificationMessage("Preview discarded.", NotificationType.Info));
            ClearState();
        }
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private void ClearState()
    {
        _currentFileId = Guid.Empty;
        _originalPreview = null;
        BankName = string.Empty;
        AccountNumber = string.Empty;
        TargetSheetName = string.Empty;
        CurrentFileName = string.Empty;
        FieldMappings.Clear();
        _corrections.Clear();
        TransactionRows.Clear();
        SelectedDateHeader = string.Empty;
        SelectedDescriptionHeader = string.Empty;
        SelectedDebitHeader = string.Empty;
        SelectedCreditHeader = string.Empty;
        SelectedAmountHeader = string.Empty;
    }
}

/// <summary>
/// Row model for the Transaction Preview DataGrid.
/// Includes bitwise ReviewFlags for error highlighting.
/// </summary>
public class TransactionPreviewRow
{
    public DateTime Date { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public decimal Amount { get; set; }
    public ReviewFlags ReviewStatus { get; set; }
}
