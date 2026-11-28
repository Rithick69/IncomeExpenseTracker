using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// Defines the type of staged ledger mutation queued in the Checkout Cart.
/// Declared at namespace scope so both TransactionReviewViewModel and StagingReviewModalViewModel resolve it directly.
/// </summary>
public enum StagedOperationType
{
    Edit = 0,
    Split = 1,
    RevertSplit = 2,
    LinkTransfer = 3
}

/// <summary>
/// ViewModel for the StagingReviewModal - a full-screen overlay for reviewing staged transaction updates
/// before committing them to the database atomically.
/// This is "The Checkout Cart" for transaction corrections.
/// </summary>
public partial class StagingReviewModalViewModel : ViewModelBase
{
    private readonly ITransactionReviewOrchestrator _orchestrator;
    private readonly IMasterDataOrchestrator _masterDataOrchestrator;

    // =============================================================================
    // MODAL VISIBILITY & CONTROL
    // =============================================================================

    /// <summary>
    /// Controls the visibility of the modal overlay.
    /// Bound to the IsVisible property of the modal in the View.
    /// </summary>
    [ObservableProperty]
    private bool _isVisible = false;

    /// <summary>
    /// Guards against duplicate submissions while a batch commit is in flight.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _isCommitting = false;

    // =============================================================================
    // STAGED CORRECTIONS DATA
    // =============================================================================

    /// <summary>
    /// Collection of staged transaction corrections to be reviewed and committed.
    /// Each item represents a delta (original vs. corrected) or a staged Split/RevertSplit/Transfer action.
    /// </summary>
    public ObservableCollection<TransactionCorrectionRowViewModel> StagedCorrections { get; } = new();

    /// <summary>
    /// Total count of staged edits.
    /// Used in the footer summary (e.g., "Total Edits: 5").
    /// </summary>
    public int TotalEditsCount => StagedCorrections.Count;

    /// <summary>
    /// Controls whether the Commit button is enabled.
    /// </summary>
    public bool CanCommit => TotalEditsCount > 0 && !IsCommitting;

    /// <summary>
    /// Computed summary text for the footer.
    /// Displays the total number of corrections and validates math integrity.
    /// </summary>
    public string FooterSummary => IsCommitting
        ? $"Committing {TotalEditsCount} staged changes..."
        : $"Total Edits: {TotalEditsCount} | Ready to Commit";

    /// <summary>
    /// Callback invoked to hide the embedded overlay in TransactionReviewViewModel.
    /// </summary>
    public Action? CloseAction { get; set; }

    /// <summary>
    /// Callback invoked after a successful commit so the parent ledger refreshes grid data and balances.
    /// </summary>
    public Func<Task>? OnCommitCompletedAsync { get; set; }

    /// <summary>
    /// Callback invoked after discarding one or all staged changes so the ledger reverts in-memory row values.
    /// </summary>
    public Func<Task>? OnDiscardCompletedAsync { get; set; }

    // =============================================================================
    // CONSTRUCTOR & INITIALIZATION
    // =============================================================================

    public StagingReviewModalViewModel(
        ITransactionReviewOrchestrator orchestrator,
        IMasterDataOrchestrator masterDataOrchestrator,
        IApplicationBroker broker)
        : base(broker)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _masterDataOrchestrator = masterDataOrchestrator ?? throw new ArgumentNullException(nameof(masterDataOrchestrator));

        // Keep footer count and commit availability synced whenever the parent ViewModel adds/removes rows
        StagedCorrections.CollectionChanged += OnStagedCorrectionsCollectionChanged;
    }

    private void OnStagedCorrectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TotalEditsCount));
        OnPropertyChanged(nameof(FooterSummary));
        OnPropertyChanged(nameof(CanCommit));
    }

    // =============================================================================
    // COMMANDS
    // =============================================================================

    [RelayCommand]
    private async Task CloseAsync()
    {
        if (IsCommitting) return;

        IsVisible = false;
        CloseAction?.Invoke();
        await Task.CompletedTask;
    }

    /// <summary>
    /// Discards a specific staged correction and refreshes the ledger so the row reverts visually.
    /// Auto-closes the modal if no staged changes remain.
    /// </summary>
    [RelayCommand]
    private async Task DiscardCorrectionAsync(TransactionCorrectionRowViewModel? correction)
    {
        if (correction == null || IsCommitting) return;

        StagedCorrections.Remove(correction);
        Broker.Send(new ToastNotificationMessage("Staged change discarded.", NotificationType.Info));

        if (OnDiscardCompletedAsync != null)
        {
            await OnDiscardCompletedAsync();
        }

        if (StagedCorrections.Count == 0)
        {
            IsVisible = false;
            CloseAction?.Invoke();
        }
    }

    /// <summary>
    /// Discards every pending staged change, reverts the ledger view, and closes the modal.
    /// </summary>
    [RelayCommand]
    private async Task DiscardAllAsync()
    {
        if (IsCommitting || StagedCorrections.Count == 0) return;

        int discardedCount = StagedCorrections.Count;
        StagedCorrections.Clear();

        Broker.Send(new ToastNotificationMessage(
            $"Discarded all {discardedCount} staged change(s).",
            NotificationType.Info));

        if (OnDiscardCompletedAsync != null)
        {
            await OnDiscardCompletedAsync();
        }

        IsVisible = false;
        CloseAction?.Invoke();
    }

    /// <summary>
    /// Commits all staged corrections (Edits, Splits, Revert Splits, and Transfer Links) to the database.
    /// On success, clears the staging area, refreshes the ledger, and closes the modal.
    /// On failure, removes only successfully committed items and keeps remaining items open for retry.
    /// </summary>
    [RelayCommand]
    private async Task CommitUpdatesAsync()
    {
        if (IsCommitting) return;

        if (StagedCorrections.Count == 0)
        {
            Broker.Send(new ToastNotificationMessage("No corrections to commit", NotificationType.Warning));
            return;
        }

        IsCommitting = true;
        OnPropertyChanged(nameof(FooterSummary));

        try
        {
            var pendingItems = StagedCorrections.ToList();
            var committedItems = new List<TransactionCorrectionRowViewModel>();

            // 1. Batch-commit all standard Edit operations atomically
            var editRows = pendingItems.Where(x => x.OperationType == StagedOperationType.Edit).ToList();
            if (editRows.Count > 0)
            {
                var dtos = await BuildCorrectionDtosAsync(editRows);
                await _orchestrator.ApplyCorrectionsAsync(dtos);
                committedItems.AddRange(editRows);
            }

            // 2. Execute staged Split operations
            foreach (var splitRow in pendingItems.Where(x => x.OperationType == StagedOperationType.Split))
            {
                if (splitRow.SplitChildren is { Count: >= 2 })
                {
                    await _orchestrator.SplitTransactionAsync((int)splitRow.TransactionId, splitRow.SplitChildren);
                    committedItems.Add(splitRow);
                }
            }

            // 3. Execute staged Revert Split operations
            foreach (var revertRow in pendingItems.Where(x => x.OperationType == StagedOperationType.RevertSplit))
            {
                await _orchestrator.RevertSplitAsync((int)revertRow.TransactionId);
                committedItems.Add(revertRow);
            }

            // 4. Execute staged Transfer Link operations
            foreach (var transferRow in pendingItems.Where(x => x.OperationType == StagedOperationType.LinkTransfer))
            {
                if (transferRow.LinkedCreditTransactionId.HasValue && transferRow.SystemTransferTagId.HasValue)
                {
                    await _orchestrator.LinkTransferAsync(
                        (int)transferRow.TransactionId,
                        transferRow.LinkedCreditTransactionId.Value,
                        transferRow.SystemTransferTagId.Value);
                    committedItems.Add(transferRow);
                }
            }

            // Remove committed items from the cart
            foreach (var done in committedItems)
            {
                StagedCorrections.Remove(done);
            }

            Broker.Send(new ToastNotificationMessage(
                $"Committed {committedItems.Count} staged change(s) successfully.",
                NotificationType.Success));

            // Refresh parent ledger grid and balances
            if (OnCommitCompletedAsync != null)
            {
                await OnCommitCompletedAsync();
            }

            // Close the modal if everything committed cleanly
            if (StagedCorrections.Count == 0)
            {
                IsVisible = false;
                CloseAction?.Invoke();
            }
        }
        catch (Exception)
        {
            // Orchestrator logs and broadcasts the specific error; keep modal open for retry
            Broker.Send(new ToastNotificationMessage(
                "One or more staged changes failed to commit. Review and try again.",
                NotificationType.Error));
        }
        finally
        {
            IsCommitting = false;
            OnPropertyChanged(nameof(FooterSummary));
        }
    }

    /// <summary>
    /// Resolves Tag and Payee names into database IDs (creating new records if the user typed a new name)
    /// and maps staged edit rows into TransactionCorrectionDTOs.
    /// </summary>
    private async Task<List<TransactionCorrectionDTO>> BuildCorrectionDtosAsync(
        List<TransactionCorrectionRowViewModel> editRows)
    {
        var tagsTask = _masterDataOrchestrator.GetAllTagsAsync();
        var payeesTask = _masterDataOrchestrator.GetAllPayeesAsync();
        await Task.WhenAll(tagsTask, payeesTask);

        var tagIdsByName = tagsTask.Result
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var payeeIdsByName = payeesTask.Result
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var dtos = new List<TransactionCorrectionDTO>(editRows.Count);

        foreach (var row in editRows)
        {
            int? resolvedTagId = null;
            if (!string.IsNullOrWhiteSpace(row.NewTag))
            {
                var tagName = row.NewTag.Trim();
                if (!tagIdsByName.TryGetValue(tagName, out int tagId))
                {
                    tagId = await _masterDataOrchestrator.GetOrCreateTagAsync(tagName);
                    tagIdsByName[tagName] = tagId;
                }
                resolvedTagId = tagId;
            }

            int? resolvedPayeeId = null;
            if (!string.IsNullOrWhiteSpace(row.NewPayee))
            {
                var payeeName = row.NewPayee.Trim();
                if (!payeeIdsByName.TryGetValue(payeeName, out int payeeId))
                {
                    var created = await _masterDataOrchestrator.CreatePayeeAsync(new Payee { Name = payeeName });
                    payeeId = created.Id;
                    payeeIdsByName[payeeName] = payeeId;
                }
                resolvedPayeeId = payeeId;
            }

            dtos.Add(new TransactionCorrectionDTO
            {
                TransactionId = (int)row.TransactionId,
                Date = row.Date,
                Debit = row.CorrectedDebit,
                Credit = row.CorrectedCredit,
                TargetTagId = resolvedTagId,
                PayeeId = resolvedPayeeId,
                Memo = row.NewMemo ?? string.Empty,
                Source = row.Source,
                RawDescription = row.RawDescription
            });
        }

        return dtos;
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

    // =========================================================================
    // UNIFIED STAGING METADATA (Supports Edit, Split, Revert Split & Transfer)
    // =========================================================================

    public StagedOperationType OperationType { get; set; } = StagedOperationType.Edit;

    /// <summary>
    /// Human-readable summary shown in the Staging Modal (e.g., "Split into 3 lines", "Link Transfer ↔ Tx #42").
    /// </summary>
    public string ActionSummary { get; set; } = "Edit";

    /// <summary>
    /// Sanitized source string used for retroactive payee/tag sweeps during ApplyCorrectionsAsync.
    /// </summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>
    /// Raw bank description used for background tag rule learning during ApplyCorrectionsAsync.
    /// </summary>
    public string RawDescription { get; set; } = string.Empty;

    /// <summary>
    /// Persisted Memo edit for standard Edit operations.
    /// </summary>
    public string? NewMemo { get; set; }

    /// <summary>
    /// Child rows prepared for atomic insertion when OperationType == Split.
    /// </summary>
    public List<Transaction>? SplitChildren { get; set; }

    /// <summary>
    /// Counterpart Credit transaction ID when OperationType == LinkTransfer (TransactionId holds the Debit ID).
    /// </summary>
    public int? LinkedCreditTransactionId { get; set; }

    /// <summary>
    /// Resolved System Transfer Tag ID when OperationType == LinkTransfer.
    /// </summary>
    public int? SystemTransferTagId { get; set; }
}