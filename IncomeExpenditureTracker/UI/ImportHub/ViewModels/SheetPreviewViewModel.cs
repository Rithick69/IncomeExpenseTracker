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

    // INJECTED CHILD VIEWMODEL
    public AccountMappingViewModel AccountMapping { get; }

    [ObservableProperty] private string _targetSheetName = string.Empty;
    [ObservableProperty] private string _currentFileName = string.Empty;

    // UI METRICS & VALIDATION
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmImport))]
    [NotifyPropertyChangedFor(nameof(ConfidenceScoreText))]
    private int _confidenceScore;
    public string ConfidenceScoreText => $"Confidence Score: {ConfidenceScore}%";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _headerValidationError = string.Empty;

    // True if Score >= 80 AND all typed headers are perfectly valid AND an Account or Card number exists
    public bool CanConfirmImport => ConfidenceScore >= 80
        && string.IsNullOrEmpty(HeaderValidationError)
        && (!string.IsNullOrWhiteSpace(AccountMapping.AccountNumber) || !string.IsNullOrWhiteSpace(AccountMapping.CardNumber));

    // Contains real headers AND injected placeholders like "[Column 3 - Blank]"
    public Dictionary<string, DetectedField> FieldMappings { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    // Binds exclusively to the exact physical headers found in the file
    public ObservableCollection<string> AvailableHeaders { get; } = new();

    // PHASE 3: RAW CONTEXT
    [ObservableProperty] private ObservableCollection<List<string>> _rawHeaderRows = new();

    // PHASE 3: METADATA LABELS (Structural targets for the AI)
    [ObservableProperty] private string _selectedEntityNameLabel = string.Empty;
    [ObservableProperty] private string _selectedAccountNumberLabel = string.Empty;
    [ObservableProperty] private string _selectedCardNumberLabel = string.Empty;

    // ACCOUNT METADATA
    [ObservableProperty] private string _bankName = string.Empty;
    [ObservableProperty] private string _accountNumber = string.Empty;
    [ObservableProperty] private string _accountType = string.Empty;
    [ObservableProperty] private string _currency = string.Empty;
    [ObservableProperty] private string _creditLimit = string.Empty;

    // COLUMN MAPPINGS
    [ObservableProperty] private string _selectedDateHeader = string.Empty;
    [ObservableProperty] private string _selectedDescriptionHeader = string.Empty;
    [ObservableProperty] private string _selectedDebitHeader = string.Empty;
    [ObservableProperty] private string _selectedCreditHeader = string.Empty;
    [ObservableProperty] private string _selectedAmountHeader = string.Empty;
    [ObservableProperty] private string _selectedTransactionTypeHeader = string.Empty;

    // UI ERROR HIGHLIGHT STATES
    [ObservableProperty] private bool _isEntityNameLabelInvalid;
    [ObservableProperty] private bool _isAccountNumberLabelInvalid;
    [ObservableProperty] private bool _isCardNumberLabelInvalid;

    // EXCLUSIVE UI TOGGLES (Debit/Credit OR Amount/TxType)
    [ObservableProperty] private bool _isAmountMappingEnabled = true;
    [ObservableProperty] private bool _isSplitMappingEnabled = true;
    public ObservableCollection<TransactionPreviewRow> TransactionRows { get; } = new();

    // Add this near your other ObservableProperties
    [ObservableProperty] private int _selectedTabIndex = 0;

    // Determines if the Helper Banner should show on the Account Details tab
    [ObservableProperty] private bool _hasMissingMetadata;

    // Add these properties to SheetPreviewViewModel.cs
    [ObservableProperty] private bool _isRawPreviewVisible;
    [RelayCommand]
    private void ToggleRawPreview()
    {
        IsRawPreviewVisible = !IsRawPreviewVisible;
    }

    // Add these command methods anywhere in the class
    [RelayCommand]
    private void NextTab() => SelectedTabIndex = 1;

    [RelayCommand]
    private void PreviousTab() => SelectedTabIndex = 0;

    // Add these near your other ObservableProperties
    [ObservableProperty] private bool _suggestAiTraining;
    [ObservableProperty] private bool _isAiTrainingExpanded;

    // Internal state to remember they acknowledged it for this session
    private bool _hasUserAcknowledgedAiTraining = false;

    // Reacts instantly when the user clicks the Expander in the UI
    partial void OnIsAiTrainingExpandedChanged(bool value)
    {
        // If they opened it, and the suggestion was active, turn it off forever.
        if (value && SuggestAiTraining)
        {
            SuggestAiTraining = false;
            _hasUserAcknowledgedAiTraining = true;
        }
    }

    // =========================================================================
    // REACTIVE DROPDOWN HOOKS (Community Toolkit Source Generators)
    // These fire automatically whenever the user changes a ComboBox in the UI.
    // =========================================================================
    partial void OnSelectedDateHeaderChanged(string value) => EvaluateMappingToggles();
    partial void OnSelectedDescriptionHeaderChanged(string value) => EvaluateMappingToggles();
    partial void OnSelectedDebitHeaderChanged(string value) => EvaluateMappingToggles();
    partial void OnSelectedCreditHeaderChanged(string value) => EvaluateMappingToggles();
    partial void OnSelectedAmountHeaderChanged(string value) => EvaluateMappingToggles();
    partial void OnSelectedTransactionTypeHeaderChanged(string value) => EvaluateMappingToggles();

    [RelayCommand]
    private void ClearSelectedDateHeader()
    {
        SelectedDateHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedDebitHeader()
    {
        SelectedDebitHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedCreditHeader()
    {
        SelectedCreditHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedAmountHeader()
    {
        SelectedAmountHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedDescHeader()
    {
        SelectedDescriptionHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedTransactionTypeHeader()
    {
        SelectedTransactionTypeHeader = string.Empty;
    }
    [RelayCommand]
    private void ClearSelectedEntityNameLabel()
    {
        SelectedEntityNameLabel = string.Empty;
        IsEntityNameLabelInvalid = false; // Removes the red border
    }

    [RelayCommand]
    private void ClearSelectedAccountNumberLabel()
    {
        SelectedAccountNumberLabel = string.Empty;
        IsAccountNumberLabelInvalid = false;
    }

    [RelayCommand]
    private void ClearSelectedCardNumberLabel()
    {
        SelectedCardNumberLabel = string.Empty;
        IsCardNumberLabelInvalid = false;
    }

    // TRANSACTION HEADER ERROR STATES
    [ObservableProperty] private bool _isDateHeaderInvalid;
    [ObservableProperty] private bool _isDescriptionHeaderInvalid;
    [ObservableProperty] private bool _isDebitHeaderInvalid;
    [ObservableProperty] private bool _isCreditHeaderInvalid;
    [ObservableProperty] private bool _isAmountHeaderInvalid;
    [ObservableProperty] private bool _isTransactionTypeHeaderInvalid;

    // AI LABEL XOR LOGIC
    public bool IsAiAccountNumberLabelEnabled => string.IsNullOrWhiteSpace(SelectedCardNumberLabel);
    public bool IsAiCardNumberLabelEnabled => string.IsNullOrWhiteSpace(SelectedAccountNumberLabel);

    partial void OnSelectedAccountNumberLabelChanged(string value) => OnPropertyChanged(nameof(IsAiCardNumberLabelEnabled));
    partial void OnSelectedCardNumberLabelChanged(string value) => OnPropertyChanged(nameof(IsAiAccountNumberLabelEnabled));


    // =========================================================================
    // CONSTRUCTOR
    // =========================================================================
    public SheetPreviewViewModel(IStatementManager statementManager, IApplicationBroker broker, AccountMappingViewModel accountMapping)
        : base(broker)
    {
        _statementManager = statementManager ?? throw new ArgumentNullException(nameof(statementManager));
        AccountMapping = accountMapping ?? throw new ArgumentNullException(nameof(accountMapping));

        // Listen to the child ViewModel to re-evaluate the Confirm button dynamically
        AccountMapping.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AccountMapping.AccountNumber) ||
                e.PropertyName == nameof(AccountMapping.CardNumber))
            {
                OnPropertyChanged(nameof(CanConfirmImport));
            }
        };
    }

    // =========================================================================
    // PUBLIC METHODS (Called by Parent ImportHubViewModel)
    // =========================================================================

    private void EvaluateMappingToggles()
    {
        bool hasSplit = !string.IsNullOrEmpty(SelectedDebitHeader) || !string.IsNullOrEmpty(SelectedCreditHeader);
        bool hasAmount = !string.IsNullOrEmpty(SelectedAmountHeader) || !string.IsNullOrEmpty(SelectedTransactionTypeHeader);

        if (hasSplit)
        {
            IsAmountMappingEnabled = false;
            IsSplitMappingEnabled = true;

            // Optionally clear the hidden fields so they don't get processed
            SelectedAmountHeader = string.Empty;
            SelectedTransactionTypeHeader = string.Empty;
        }
        else if (hasAmount)
        {
            IsSplitMappingEnabled = false;
            IsAmountMappingEnabled = true;
            SelectedDebitHeader = string.Empty;
            SelectedCreditHeader = string.Empty;
        }
        else
        {
            IsAmountMappingEnabled = true;
            IsSplitMappingEnabled = true;
        }
    }

    /// <summary>
    /// Enforces the hard rule that user-typed headers MUST exist in the physical sheet.
    /// </summary>
    private void ValidateHeaders()
    {
        var selectedHeaders = new[]
        {
            SelectedDateHeader, SelectedDescriptionHeader, SelectedDebitHeader,
            SelectedCreditHeader, SelectedAmountHeader, SelectedTransactionTypeHeader
        };

        foreach (var header in selectedHeaders)
        {
            // Case-insensitive, space-insensitive check
            if (!string.IsNullOrWhiteSpace(header) &&
                !AvailableHeaders.Any(h => string.Equals(h?.Trim(), header.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                HeaderValidationError = $"Error: Column '{header}' does not exist in the sheet.";
                return;
            }
        }

        HeaderValidationError = string.Empty;
    }

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

            // 1. Initialize dropdown data for the child ViewModel
            await AccountMapping.InitializeAsync();

            // 2. Pass the extracted fields dictionary to the child ViewModel to parse
            AccountMapping.LoadFromPreview(_originalPreview.Fields);

            // Populate the Raw Excel View
            RawHeaderRows.Clear();
            if (_originalPreview.RawHeaderRows != null)
            {
                foreach (var row in _originalPreview.RawHeaderRows)
                {
                    RawHeaderRows.Add(row);
                }
            }

            ConfidenceScore = _originalPreview.ConfidenceScore;
            TargetSheetName = targetSheet ?? _originalPreview.FileName;
            FieldMappings = new Dictionary<string, DetectedField>(_originalPreview.Fields, StringComparer.OrdinalIgnoreCase); ;

            // Populate AvailableHeaders from the detected fields (raw column names)
            // This provides the ComboBox options for column mapping
            AvailableHeaders.Clear();
            if (_originalPreview.RawHeaders != null)
            {
                foreach (var header in _originalPreview.RawHeaders)
                {
                    AvailableHeaders.Add(header);
                }
            }

            // Initialize Dropdown Selections
            if (FieldMappings.TryGetValue("Col:DATE", out var dField)) SelectedDateHeader = dField.ColumnName;
            if (FieldMappings.TryGetValue("Col:DESCRIPTION", out var descField)) SelectedDescriptionHeader = descField.ColumnName;
            if (FieldMappings.TryGetValue("Col:DEBIT", out var debField)) SelectedDebitHeader = debField.ColumnName;
            if (FieldMappings.TryGetValue("Col:CREDIT", out var credField)) SelectedCreditHeader = credField.ColumnName;
            if (FieldMappings.TryGetValue("Col:AMOUNT", out var amField)) SelectedAmountHeader = amField.ColumnName;
            if (FieldMappings.TryGetValue("Col:TRANSACTIONTYPE", out var typeField)) SelectedTransactionTypeHeader = typeField.ColumnName;

            // Initialize the UI Labels if the AI detected them
            if (FieldMappings.TryGetValue("Meta:ENTITY_NAME", out var eField)) SelectedEntityNameLabel = eField.ColumnName;
            if (FieldMappings.TryGetValue("Meta:ACCOUNT_NUMBER", out var aField)) SelectedAccountNumberLabel = aField.ColumnName;
            if (FieldMappings.TryGetValue("Meta:CARD_NUMBER", out var cnField)) SelectedCardNumberLabel = cnField.ColumnName;

            UpdateTransactionGrid(_originalPreview.PreviewTransactions);
            EvaluateMappingToggles();
            ValidateHeaders();

            // Evaluate if the missing metadata banner should be shown
            HasMissingMetadata = string.IsNullOrWhiteSpace(SelectedAccountNumberLabel) ||
                                 string.IsNullOrWhiteSpace(SelectedEntityNameLabel);

            // Only show the AI training highlight if data is missing AND they haven't acknowledged it yet
            SuggestAiTraining = HasMissingMetadata && !_hasUserAcknowledgedAiTraining;

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

    [RelayCommand]
    public async Task RegeneratePreviewAsync()
    {
        if (IsBusy || _currentFileId == Guid.Empty || _originalPreview == null) return;

        // GUARD: Check labels before hitting the backend Edit Service
        if (!ValidateAllInputs()) return;

        IsBusy = true;

        StatusText = "Regenerating preview...";

        try
        {
            // Sync user's edits from the child ViewModel back into FieldMappings
            AccountMapping.ApplyToMappings(FieldMappings);

            // PHASE 3: Sync manually typed labels into the dictionary for targeted search
            UpdateMetadataLabel("Meta:ENTITY_NAME", SelectedEntityNameLabel);
            UpdateMetadataLabel("Meta:ACCOUNT_NUMBER", SelectedAccountNumberLabel);
            UpdateMetadataLabel("Meta:CARD_NUMBER", SelectedCardNumberLabel);

            // Resolve Dropdowns to Integer Coordinates
            UpdateMapping("Col:DATE", SelectedDateHeader);
            UpdateMapping("Col:DESCRIPTION", SelectedDescriptionHeader);
            UpdateMapping("Col:DEBIT", SelectedDebitHeader);
            UpdateMapping("Col:CREDIT", SelectedCreditHeader);
            UpdateMapping("Col:AMOUNT", SelectedAmountHeader);
            UpdateMapping("Col:TRANSACTIONTYPE", SelectedTransactionTypeHeader);

            // Call Backend Re-Extraction
            var newPreview = await _statementManager.RegeneratePreviewAsync(
                _currentFileId,
                TargetSheetName,
                _originalPreview.HeaderRow,
                FieldMappings,
                AvailableHeaders.ToList()
            );

            // Refresh UI
            ConfidenceScore = newPreview.ConfidenceScore;
            UpdateTransactionGrid(newPreview.PreviewTransactions);

            Broker.Send(new ToastNotificationMessage(ToastType.Success, "Preview regenerated successfully."));

            // Evaluate if the missing metadata banner should be shown
            HasMissingMetadata = string.IsNullOrWhiteSpace(SelectedAccountNumberLabel) ||
                                 string.IsNullOrWhiteSpace(SelectedEntityNameLabel);

            // Only show the AI training highlight if data is missing AND they haven't acknowledged it yet
            SuggestAiTraining = HasMissingMetadata && !_hasUserAcknowledgedAiTraining;

            StatusText = "Regeneration complete.";
        }
        catch (Exception ex)
        {
            StatusText = $"Regeneration failed: {ex.Message}";
            Broker.Send(new ToastNotificationMessage(ToastType.Error, $"Regeneration Error: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void UpdateMetadataLabel(string fieldKey, string userTypedLabel)
    {
        if (!FieldMappings.TryGetValue(fieldKey, out var field))
        {
            field = new DetectedField { SuggestedField = fieldKey.Replace("Meta:", ""), ColumnIndex = -1 };
            FieldMappings[fieldKey] = field;
        }

        // Only update if the user actually typed something
        if (!string.IsNullOrWhiteSpace(userTypedLabel))
        {
            field.ColumnName = userTypedLabel.Trim();
        }
    }

    private void UpdateMapping(string fieldKey, string selectedHeader)
    {
        if (string.IsNullOrWhiteSpace(selectedHeader)) return;

        // Case-insensitive find to get the correct physical index
        var matchedHeader = AvailableHeaders.FirstOrDefault(h => string.Equals(h?.Trim(), selectedHeader.Trim(), StringComparison.OrdinalIgnoreCase));

        if (matchedHeader != null)
        {

            // O(1) resolution: The index in AvailableHeaders perfectly matches the physical Excel column index!
            int newIndex = AvailableHeaders.IndexOf(matchedHeader);
            if (newIndex >= 0)
            {
                if (!FieldMappings.TryGetValue(fieldKey, out var field))
                {
                    field = new DetectedField { SuggestedField = fieldKey.Replace("Col:", ""), ColumnIndex = -1 };
                    FieldMappings[fieldKey] = field;
                }

                bool isPlaceholder = !string.IsNullOrWhiteSpace(field.ColumnName) &&
                     field.ColumnName.StartsWith("[Column ") &&
                     field.ColumnName.EndsWith(" - Blank]");

                // Push the user's manual correction directly into the centralized Edit Session
                if (field.ColumnIndex != newIndex && !isPlaceholder)
                {
                    try
                    {
                        var session = _statementManager.GetEditSession(_currentFileId);
                        // Pass the newly matched user header to the tracking system
                        session.UpdateColumnMapping(fieldKey, matchedHeader, newIndex, "TRANSACTION");
                    }
                    catch (Exception)
                    {
                        // Fail silently if session dropped; UI gracefully recovers
                    }
                }

                field.ColumnName = matchedHeader;
                field.ColumnIndex = newIndex;
                field.IsUserVerified = true;
                field.ConfidenceScore = 100;
            }
        }
    }

    private void UpdateTransactionGrid(List<TransactionPreview> transactions)
    {
        TransactionRows.Clear();
        foreach (var txn in transactions)
        {
            TransactionRows.Add(new TransactionPreviewRow
            {
                Date = txn.Date,
                Description = txn.Description,
                Debit = txn.Debit,
                Credit = txn.Credit,
                Amount = txn.Amount,
                ReviewStatus = CalculateReviewFlags(txn),
                TransactionType = txn.TransactionType
            });
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

        // GUARD: Check labels before hitting the backend Edit Service
        if (!ValidateAllInputs()) return;

        IsBusy = true;
        StatusText = "Committing to database...";

        try
        {
            AccountMapping.ApplyToMappings(FieldMappings);

            // Force sync the latest dropdown choices into the Edit Session
            UpdateMapping("Col:DATE", SelectedDateHeader);
            UpdateMapping("Col:DESCRIPTION", SelectedDescriptionHeader);
            UpdateMapping("Col:DEBIT", SelectedDebitHeader);
            UpdateMapping("Col:CREDIT", SelectedCreditHeader);
            UpdateMapping("Col:AMOUNT", SelectedAmountHeader);
            UpdateMapping("Col:TRANSACTIONTYPE", SelectedTransactionTypeHeader);

            // Ask the Edit Session to bundle all deduplicated corrections for us
            var session = _statementManager.GetEditSession(_currentFileId);

            // ================================================================
            // PUSH MANUAL LABELS TO THE LEARNING ENGINE
            // Passing -1 tells SynonymService to learn the string itself,
            // bypassing physical column coordinate tracking.
            // ================================================================
            if (!string.IsNullOrWhiteSpace(SelectedEntityNameLabel))
                session.UpdateColumnMapping("Meta:ENTITY_NAME", SelectedEntityNameLabel, -1, "METADATA");

            if (!string.IsNullOrWhiteSpace(SelectedAccountNumberLabel))
                session.UpdateColumnMapping("Meta:ACCOUNT_NUMBER", SelectedAccountNumberLabel, -1, "METADATA");

            if (!string.IsNullOrWhiteSpace(SelectedCardNumberLabel))
                session.UpdateColumnMapping("Meta:CARD_NUMBER", SelectedCardNumberLabel, -1, "METADATA");

            // ================================================================

            var tracker = session.ConfirmAndPrepareForImport();

            // Overlay final UI states onto the tracker before sending to backend
            tracker.SheetName = TargetSheetName;
            tracker.FinalPreview.FileName = CurrentFileName;
            tracker.FinalPreview.Fields = FieldMappings;
            tracker.FinalPreview.HeaderRow = _originalPreview.HeaderRow;
            tracker.FinalPreview.PreviewTransactions = _originalPreview.PreviewTransactions;
            tracker.FinalPreview.ConfidenceScore = ConfidenceScore;

            await _statementManager.CommitStagedBatchAsync(_currentFileId, tracker);
            Broker.Send(new ToastNotificationMessage(ToastType.Success, $"Successfully imported {TransactionRows.Count} transactions."));
            // Clear the state FIRST so the UI's teardown event doesn't think a file is pending.
            ClearState();

            // Use the targeted queue message instead of a hard reload to preserve the other staged files.
            Broker.Send(new ReturnToQueueMessage());
        }
        catch (Exception ex)
        {
            StatusText = $"Import failed: {ex.Message}";
            Broker.Send(new ToastNotificationMessage(ToastType.Error, $"Import Error: {ex.Message}"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Validates the structural transaction headers and AI training labels.
    /// Explicitly allows primary account values to be freely entered even if not present in the sheet.
    /// </summary>
    private bool ValidateAllInputs()
    {
        bool allValid = true;

        // =========================================================
        // 1. VALIDATE TRANSACTION HEADERS
        // =========================================================
        IsDateHeaderInvalid = false;
        IsDescriptionHeaderInvalid = false;
        IsDebitHeaderInvalid = false;
        IsCreditHeaderInvalid = false;
        IsAmountHeaderInvalid = false;
        IsTransactionTypeHeaderInvalid = false;

        var headersToCheck = new List<(string Name, string Value, Action SetError)>
        {
            ("Date Column", SelectedDateHeader, () => IsDateHeaderInvalid = true),
            ("Description Column", SelectedDescriptionHeader, () => IsDescriptionHeaderInvalid = true),
            ("Debit Column", SelectedDebitHeader, () => IsDebitHeaderInvalid = true),
            ("Credit Column", SelectedCreditHeader, () => IsCreditHeaderInvalid = true),
            ("Amount Column", SelectedAmountHeader, () => IsAmountHeaderInvalid = true),
            ("Transaction Type Column", SelectedTransactionTypeHeader, () => IsTransactionTypeHeaderInvalid = true)
        };

        foreach (var header in headersToCheck)
        {
            if (!string.IsNullOrWhiteSpace(header.Value) &&
                !AvailableHeaders.Any(h => string.Equals(h?.Trim(), header.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                header.SetError();
                allValid = false;
                HeaderValidationError = $"Error: Column '{header.Value}' does not exist in the sheet.";
            }
        }

        if (allValid) HeaderValidationError = string.Empty;

        // =========================================================
        // 2. VALIDATE AI TRAINING LABELS
        // =========================================================
        IsEntityNameLabelInvalid = false;
        IsAccountNumberLabelInvalid = false;
        IsCardNumberLabelInvalid = false;

        var labelsToCheck = new List<(string Name, string Value, Action SetError)>
        {
            ("Bank Label", SelectedEntityNameLabel, () => IsEntityNameLabelInvalid = true),
            ("Account Label", SelectedAccountNumberLabel, () => IsAccountNumberLabelInvalid = true),
            ("Card Label", SelectedCardNumberLabel, () => IsCardNumberLabelInvalid = true)
        };

        foreach (var item in labelsToCheck)
        {
            if (!string.IsNullOrWhiteSpace(item.Value))
            {
                bool foundInRawGrid = false;

                foreach (var row in RawHeaderRows)
                {
                    if (row != null && row.Any(cell =>
                        !string.IsNullOrWhiteSpace(cell) &&
                        cell.Contains(item.Value.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        foundInRawGrid = true;
                        break;
                    }
                }

                if (!foundInRawGrid)
                {
                    item.SetError();
                    allValid = false;

                    Broker.Send(new ToastNotificationMessage(
                        $"Validation Error: '{item.Name}' was not found. It must match part of the text in the Raw Excel Context.",
                        NotificationType.Error
                    ));
                }
            }
        }

        return allValid;
    }

    // Safe routing back to the Queue WITHOUT deleting the file from memory
    // [RelayCommand]
    // public void NavigateBack()
    // {
    //     Broker.Send(new NavigationMessage("ImportHub"));
    // }


    /// <summary>
    /// Abandons the current preview state and returns to the queue.
    /// The file remains safely staged in memory.
    /// </summary>
    [RelayCommand]
    public async Task RequestDiscardPreview()
    {
        var confirmation = new TaskCompletionSource<bool>();
        Broker.Send(new ShowConfirmationMessage(
            "Abandon Preview?",
            "The file will remain in your queue, but all manual mappings will be lost.",
            confirmation
        ));

        if (await confirmation.Task)
            ConfirmDiscardPreview();
    }

    /// <summary>
    /// Actually abandons the preview (runs when the user clicks 'Yes' in the dialog).
    /// </summary>
    private void ConfirmDiscardPreview()
    {
        Broker.Send(new ToastNotificationMessage("Preview abandoned. File remains in queue.", NotificationType.Info));
        ClearState();
        Broker.Send(new ReturnToQueueMessage());
    }

    // =========================================================================
    // HELPER METHODS
    // =========================================================================

    private void ClearState()
    {
        _currentFileId = Guid.Empty;
        _originalPreview = null;
        FieldMappings.Clear();
        TransactionRows.Clear();
        _hasUserAcknowledgedAiTraining = false;
        IsAiTrainingExpanded = false;
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

    public string TransactionType { get; set; } = string.Empty;
}
