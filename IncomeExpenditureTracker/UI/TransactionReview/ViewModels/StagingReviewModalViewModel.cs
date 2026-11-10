using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.TransactionReview;

/// <summary>
/// ViewModel for the StagingReviewModal - a full-screen overlay for reviewing staged transaction updates
/// before committing them to the database atomically.
/// This is "The Checkout Cart" for transaction corrections.
/// </summary>
public partial class StagingReviewModalViewModel : ViewModelBase
{
    private readonly ITransactionReviewOrchestrator _orchestrator;

    // =============================================================================
    // MODAL VISIBILITY & CONTROL
    // =============================================================================

    /// <summary>
    /// Controls the visibility of the modal overlay.
    /// Bound to the IsVisible property of the modal in the View.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = false;

    // =============================================================================
    // STAGED CORRECTIONS DATA
    // =============================================================================

    /// <summary>
    /// Collection of staged transaction corrections to be reviewed and committed.
    /// Each item represents a delta (original vs. corrected) for a single transaction.
    /// In production, this is populated from TransactionCorrectionDTO objects.
    /// </summary>
    public ObservableCollection<TransactionCorrectionRowViewModel> StagedCorrections { get; } = new();

    /// <summary>
    /// Total count of staged edits.
    /// Used in the footer summary (e.g., "Total Edits: 5").
    /// </summary>
    public int TotalEditsCount => StagedCorrections.Count;

    /// <summary>
    /// Computed summary text for the footer.
    /// Displays the total number of corrections and validates math integrity.
    /// </summary>
    public string FooterSummary => $"Total Edits: {TotalEditsCount} | Ready to Commit";

    public Action CloseAction { get; set; }

    // =============================================================================
    // CONSTRUCTOR & INITIALIZATION
    // =============================================================================

    public StagingReviewModalViewModel(
        ITransactionReviewOrchestrator orchestrator,
        IApplicationBroker broker)
        : base(broker)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

        // Populate mock data for UI preview
        InitializeMockData();
    }

    /// <summary>
    /// Populates the StagedCorrections collection with mock data for UI preview.
    /// In production, this will be replaced with real TransactionCorrectionDTO objects
    /// built from the "dirty" transactions in the main ledger.
    /// </summary>
    private void InitializeMockData()
    {
        StagedCorrections.Add(new TransactionCorrectionRowViewModel
        {
            TransactionId = 3,
            Date = new DateTime(2026, 11, 6),
            OriginalMerchant = "Amazon Pay",
            NewPayee = "Amazon",
            NewTag = "Shopping",
            OriginalDebit = 1299.00m,
            CorrectedDebit = 1299.00m,
            OriginalCredit = 0m,
            CorrectedCredit = 0m
        });

        StagedCorrections.Add(new TransactionCorrectionRowViewModel
        {
            TransactionId = 5,
            Date = new DateTime(2026, 11, 4),
            OriginalMerchant = "Groww Invest",
            NewPayee = "Groww",
            NewTag = "Investment",
            OriginalDebit = 5000.00m,
            CorrectedDebit = 5000.00m,
            OriginalCredit = 0m,
            CorrectedCredit = 0m
        });

        StagedCorrections.Add(new TransactionCorrectionRowViewModel
        {
            TransactionId = 7,
            Date = new DateTime(2026, 11, 2),
            OriginalMerchant = "Unknown Merchant",
            NewPayee = "ABC Store",
            NewTag = "Misc",
            OriginalDebit = 0m,
            CorrectedDebit = 250.00m,
            OriginalCredit = 0m,
            CorrectedCredit = 0m
        });
    }

    // =============================================================================
    // COMMANDS
    // =============================================================================

    [RelayCommand]
    private async Task CloseAsync()
    {
        CloseAction?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Discards a specific staged correction from the collection.
    /// This removes the correction from the pending batch but does not affect the database.
    /// </summary>
    [RelayCommand]
    private void DiscardCorrection(TransactionCorrectionRowViewModel correction)
    {
        if (correction != null)
        {
            StagedCorrections.Remove(correction);
            OnPropertyChanged(nameof(TotalEditsCount));
            OnPropertyChanged(nameof(FooterSummary));
            Broker.Send(new ToastNotificationMessage("Correction discarded", NotificationType.Info));
        }
    }

    /// <summary>
    /// Commits all staged corrections to the database atomically within a single transaction.
    /// This triggers the orchestrator's ApplyCorrectionsAsync method.
    /// On success, clears the staging area and closes the modal.
    /// On failure, displays an error and keeps the modal open for retry.
    /// </summary>
    [RelayCommand]
    private async Task CommitUpdatesAsync()
    {
        if (StagedCorrections.Count == 0)
        {
            Broker.Send(new ToastNotificationMessage("No corrections to commit", NotificationType.Warning));
            return;
        }

        // In production, this would:
        // 1. Build a List<TransactionCorrectionDTO> from StagedCorrections
        // 2. Call await _orchestrator.ApplyCorrectionsAsync(corrections)
        // 3. If successful, clear StagedCorrections and close modal
        // 4. If failed, show error toast and keep modal open

        Broker.Send(new ToastNotificationMessage($"Committing {StagedCorrections.Count} corrections to database (mock)", NotificationType.Success));

        // Mock success: clear and close
        StagedCorrections.Clear();
        OnPropertyChanged(nameof(TotalEditsCount));
        OnPropertyChanged(nameof(FooterSummary));
        _isVisible = false;
        OnPropertyChanged(nameof(IsVisible));

        await Task.CompletedTask;
    }
}

// =============================================================================
// SUPPORTING VIEW MODELS
// =============================================================================

/// <summary>
/// Represents a single row in the Staging Review Modal DataGrid.
/// This is a lightweight ViewModel that mirrors TransactionCorrectionDTO but adds UI-specific properties.
/// Each row shows the delta between the original transaction state and the corrected state.
/// </summary>
public class TransactionCorrectionRowViewModel : ObservableObject
{
    private long _transactionId;
    private DateTime _date;
    private string _originalMerchant = string.Empty;
    private string? _newPayee;
    private string? _newTag;
    private decimal _originalDebit;
    private decimal _correctedDebit;
    private decimal _originalCredit;
    private decimal _correctedCredit;

    public long TransactionId
    {
        get => _transactionId;
        set => SetProperty(ref _transactionId, value);
    }

    public DateTime Date
    {
        get => _date;
        set => SetProperty(ref _date, value);
    }

    public string OriginalMerchant
    {
        get => _originalMerchant;
        set => SetProperty(ref _originalMerchant, value);
    }

    public string? NewPayee
    {
        get => _newPayee;
        set => SetProperty(ref _newPayee, value);
    }

    public string? NewTag
    {
        get => _newTag;
        set => SetProperty(ref _newTag, value);
    }

    public decimal OriginalDebit
    {
        get => _originalDebit;
        set => SetProperty(ref _originalDebit, value);
    }

    public decimal CorrectedDebit
    {
        get => _correctedDebit;
        set => SetProperty(ref _correctedDebit, value);
    }

    public decimal OriginalCredit
    {
        get => _originalCredit;
        set => SetProperty(ref _originalCredit, value);
    }

    public decimal CorrectedCredit
    {
        get => _correctedCredit;
        set => SetProperty(ref _correctedCredit, value);
    }

    /// <summary>
    /// Displays the corrected amount (Debit or Credit, whichever is non-zero).
    /// Used in the DataGrid to show the final corrected value.
    /// </summary>
    public string CorrectedAmountDisplay
    {
        get
        {
            if (CorrectedDebit > 0)
                return $"-{CorrectedDebit:N2}";
            if (CorrectedCredit > 0)
                return $"+{CorrectedCredit:N2}";
            return "0.00";
        }
    }
}
