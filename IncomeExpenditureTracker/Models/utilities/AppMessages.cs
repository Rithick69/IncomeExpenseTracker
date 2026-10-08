using System.Threading.Tasks;
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

    // ---------------------------------------------------------
    // SYSTEM ENUMS (For strongly typed i18n mapping)
    // ---------------------------------------------------------

    public enum DomainEntity
    {
        Category,
        SubCategory,
        Tag,
        TagRule,
        Payee,
        PayeeMapping,
        Entity, // General Merchant/Institution
        Account,
        Synonym,
        ImportBatch,
        UserSetting,
        Transaction
    }

    public enum CrudOperation
    {
        Create,
        Read,
        Update,
        Delete,
        Merge
    }

    /// <summary>
    /// Enriched DTO for Tags, flattening the relational hierarchy for zero-DB-call UI filtering.
    /// </summary>
    public record TagHierarchyDto
    {
        // Dapper will automatically coerce SQLite's Int64 into these ints
        public int TagId { get; init; }
        public string TagName { get; init; } = string.Empty;

        // Nullable because of the LEFT JOIN
        public int? SubCategoryId { get; init; }
        public string? SubCategoryName { get; init; }

        public int? CategoryId { get; init; }
        public string? CategoryName { get; init; }
    };

    public record TagRuleHierarchyDto
    {
        public int TagRuleId { get; init; }
        public int TagId { get; init; }
        public string TagName { get; init; } = string.Empty;
        public string Keyword { get; init; } = string.Empty;
        public int? SubCategoryId { get; init; }
        public string? SubCategoryName { get; init; }
        public int? CategoryId { get; init; }
        public string? CategoryName { get; init; }
        public int Priority { get; init; }
    };

    public record SynonymsHierarchyDto
    {
        public int SynonymsId { get; init; }
        public string SynonymName { get; init; } = string.Empty;
        public string FieldType { get; init; } = string.Empty;
        public string CategoryName { get; init; } = string.Empty;
        public int Priority { get; init; }
    };

    /// <summary>
    /// Enriched DTO for SubCategories, mapping them to their parent Categories.
    /// </summary>
    public record SubCategoryHierarchyDto
    {
        public int SubCategoryId { get; init; }
        public string SubCategoryName { get; init; } = string.Empty;
        public int CategoryId { get; init; }
        public string CategoryName { get; init; } = string.Empty;
    };

    /// <summary>
    /// Broadcast when a new entity is successfully saved.
    /// EntityName is user-generated data (e.g., "Groceries") so it does not need translation.
    /// </summary>
    public record EntitySavedMessage(DomainEntity EntityType, string? EntityName = null);

    /// <summary>
    /// Broadcast when an entity is successfully deleted.
    /// </summary>
    public record EntityDeletedMessage(DomainEntity EntityType, string? EntityName = null);

    /// <summary>
    /// Broadcast when an entity is successfully updated.
    /// </summary>
    public record EntityUpdatedMessage(DomainEntity EntityType, int? EntityId = null, string? EntityName = null);

    /// <summary>
    /// Broadcast when a CRUD operation fails.
    /// Passes Enums and a LocaleKey so the UI can construct a localized error toast.
    /// </summary>
    public record CrudErrorMessage(DomainEntity EntityType, CrudOperation Operation, string LocaleKey, string[]? LocaleArgs = null);

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

    public enum ToastType { Success = 0, Error = 1, Info = 2, Warning = 3 }

    public record ToastNotificationMessage
    {
        public string Message { get; init; }
        public NotificationType Type { get; init; }

        // Your existing signature
        public ToastNotificationMessage(string message, NotificationType type)
        {
            Message = message;
            Type = type;
        }

        // New signature to support the generated ViewModels
        public ToastNotificationMessage(ToastType type, string message)
        {
            Message = message;
            Type = (NotificationType)type;
        }
    }

    /// <summary>
    /// Broadcasted immediately after the DatabaseService successfully swaps the physical .db connection.
    /// Commands all Singleton services to immediately flush their in-memory caches to prevent data bleed.
    /// </summary>
    public record ProfileSwappedMessage();

    /// <summary>
    /// Equivalent to a React Router navigation payload.
    /// Tells the shell which ViewModel to inject into the ContentControl.
    /// </summary>

    public record NavigationMessage(string Destination, object? Parameter = null);

    // -------------------------------------------------------------------------
    // GLOBAL DIALOG & MODAL MESSAGES
    // -------------------------------------------------------------------------

    /// <summary>
    /// Triggers a standard informational modal overlay.
    /// Used for tips, recovery keys, or read-only alerts.
    /// </summary>
    public record ShowHelperMessage(
        string Title,
        string Body,
        bool IsCritical = false,
        string? ContextLink = null, // Optional link to a help article or documentation
        TaskCompletionSource<bool>? CompletionSource = null,
        bool ShowCopyButton = false
    );

    /// <summary>
    /// Triggers a Yes/No confirmation overlay.
    /// Utilizes TaskCompletionSource so the calling ViewModel can await the user's
    /// decision without ever touching the Avalonia UI thread directly.
    /// </summary>
    public record ShowConfirmationMessage(
        string Title,
        string Body,
        TaskCompletionSource<bool> CompletionSource,
        string ConfirmText = "Confirm",
        string CancelText = "Cancel");

    // Tells the MainWindow to drop the loading curtain
    public record ShowLoadingOverlayMessage(string Message = "Loading...");

    // Tells the MainWindow to lift the loading curtain
    public record HideLoadingOverlayMessage();

    // Determines if a form is creating a new entity or editing an existing one
    public enum FormMode
    {
        Create,
        Update
    }

    /// <summary>
    /// Broadcast to open the dynamic Create/Update form modal (e.g., AccountFormView)
    /// </summary>
    public record ShowEntityFormModalMessage(DomainEntity EntityType, FormMode Mode, int? EntityId = null);

    /// <summary>
    /// Broadcast to open the specific Merge resolution modal.
    /// </summary>
    public record ShowEntityMergeModalMessage(DomainEntity EntityType, int SourceId, string SourceName);

    /// <summary>
    /// Broadcast by a modal ViewModel (Save/Cancel) to tell the MainWindow to close the overlay.
    /// </summary>
    public record CloseModalMessage();

    /// <summary>
    /// A unified boolean toggle for the loading spinner to replace separate show/hide messages.
    /// </summary>
    public record ToggleLoadingMessage(bool IsLoading, string? Message = null);
}