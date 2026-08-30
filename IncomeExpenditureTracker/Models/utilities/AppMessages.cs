
namespace IncomeExpenditureTracker.Models
{
    /* ============================================================================
     * APPLICATION MESSAGES (THE ENVELOPES)
     * These records are the exact envelopes passed by Orchestrators to the UI.
     * They are immutable (read-only) for strict thread safety.
     * ============================================================================ */
    // ---------------------------------------------------------
    // 1. FILE STAGING & IMPORT PIPELINE EVENTS
    // Handled by: StatementManager
    // ---------------------------------------------------------

    /// <summary>
    /// Broadcasts live progress updates (e.g., 45%, "Reading row 105...")
    /// </summary>
    public record StagingProgressMessage(int Percentage, string StatusMessage);

    /// <summary>
    /// Broadcast when an individual file fails to stage (e.g., locked by Excel)
    /// </summary>
    public record FileStagingErrorMessage(FileStagingError Error);

    /// <summary>
    /// Broadcast when the parallel loading phase finishes successfully
    /// </summary>
    public record StagingBatchCompletedMessage(int TotalSuccess, int TotalFailures);

    /// <summary>
    /// Broadcast when an entire statement batch is successfully committed to SQLite
    /// </summary>
    public record ImportBatchCompletedMessage(int TotalTransactions);

    /// <summary>
    /// Broadcast if the final SQLite commit fails catastrophically
    /// </summary>
    public record ImportBatchFailedMessage(string UserFriendlyReason);


    // ---------------------------------------------------------
    // 2. MASTER DATA CRUD EVENTS (Categories, Tags, Accounts)
    // Handled by: MasterDataOrchestrator
    // ---------------------------------------------------------

    /// <summary>
    /// Broadcast when a new category, tag, or account is successfully saved
    /// </summary>
    public record EntitySavedMessage(string EntityType, string Name);

    /// <summary>
    /// Broadcast when a category, tag, or account is successfully deleted
    /// </summary>
    public record EntityDeletedMessage(string EntityType, string Name);

    /// <summary>
    /// Broadcast when a category, tag, or account is successfully updated
    /// </summary>
    public record EntityUpdatedMessage(string EntityType, string Name);

    /// <summary>
    /// Broadcast when a CRUD operation fails (Catches the error without exposing stack traces)
    /// </summary>
    public record CrudErrorMessage(string EntityType, string Operation, string UserFriendlyMessage);


    // ---------------------------------------------------------
    // 3. TRANSACTION REVIEW EVENTS
    // Handled by: TransactionReviewOrchestrator
    // ---------------------------------------------------------

    /// <summary>
    /// Broadcast when a bulk mapping update (e.g., applying a category to 50 items) succeeds
    /// </summary>
    public record BatchUpdateCompletedMessage(int UpdatedRowCount);

    // =========================================================================
    // ENUM: Notification Severity
    // Defines the visual styling the Avalonia UI will apply to the toast.
    // =========================================================================
    public enum NotificationType
    {
        Success,
        Error,
        Info,
        Warning
    }

    // =========================================================================
    // BROKER MESSAGE: The global envelope for triggering a notification
    // Any Orchestrator or ViewModel can broadcast this message.
    // =========================================================================
    public record ToastNotificationMessage(string Message, NotificationType Type);

    /// <summary>
    /// Broadcasted immediately after the DatabaseService successfully swaps the physical .db connection.
    /// Commands all Singleton services to immediately flush their in-memory caches to prevent data bleed.
    /// </summary>
    public record ProfileSwappedMessage();
}