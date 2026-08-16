using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Orchestration;

/// <summary>
/// The central UI facade for Master Data Management.
/// Abstracts all CRUD operations and coordinates cross-service referential integrity.
/// Hides underlying SQLite transactions (conn, tx) from the Avalonia ViewModels.
///
/// Future-Proofing the UI Layer
/// Orchestrators act as the strict UI facades for Avalonia ViewModels.
/// In UI development, if a user clicks "Update" but then immediately navigates away to a different screen or hits "Cancel,"
/// the ViewModel can fire a cancellation token.By having ct = default in orchestrator now,
/// UI layer can safely pass that token in.
/// Later, when you decide to pass that token all the way down to Dapper / SQLite, you won't have to break your UI code or interface contracts to do it.
/// </summary>
public interface IMasterDataOrchestrator
{
    // =========================================================================
    // CATEGORY MANAGEMENT
    // =========================================================================

    Task<List<Category>> GetAllCategoriesAsync(CancellationToken ct = default);
    Task<int> GetOrCreateCategoryAsync(string name, CancellationToken ct = default);
    Task UpdateCategoryAsync(Category category, CancellationToken ct = default);

    /// <summary>
    /// Re-parents child SubCategories to NULL or empty Category, then deletes the target Category.
    /// Executes under a single atomic SQLite transaction.
    /// </summary>
    Task DeleteCategorySafeAsync(int categoryId, CancellationToken ct = default);

    // =========================================================================
    // SUBCATEGORY MANAGEMENT
    // =========================================================================

    Task<List<SubCategory>> GetAllSubCategoriesAsync(CancellationToken ct = default);
    Task<List<SubCategory>> GetSubCategoriesByCategoryIdAsync(int categoryId, CancellationToken ct = default);
    Task<int> GetOrCreateSubCategoryAsync(string name, int categoryId, CancellationToken ct = default);
    Task UpdateSubCategoryAsync(SubCategory subCategory, CancellationToken ct = default);

    /// <summary>
    /// Re-parents child Tags to a NULL or empty SubCategory, then deletes the target SubCategory.
    /// Executes under a single atomic SQLite transaction.
    /// </summary>
    Task DeleteSubCategorySafeAsync(int subCategoryId, CancellationToken ct = default);

    // =========================================================================
    // TAG MANAGEMENT
    // =========================================================================

    Task<RuleBookSnapshot> GetRuleBookSnapshotAsync(CancellationToken ct = default);
    Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, CancellationToken ct = default);
    Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default);

    /// <summary>
    /// Re-parents historical Transactions to a fallback Tag, deletes associated TagRules,
    /// and then deletes the target Tag. Executes under a single atomic SQLite transaction.
    /// </summary>
    Task DeleteTagSafeAsync(int tagId, CancellationToken ct = default);

    // =========================================================================
    // TAG RULE MANAGEMENT
    // =========================================================================

    Task<int> AddTagRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default);
    Task UpdateTagRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default);
    Task DeleteTagRuleAsync(int ruleId, CancellationToken ct = default);
    Task DeleteTagRulesByKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default);

    // =========================================================================
    // ENTITY MANAGEMENT (Institutions / Merchants)
    // =========================================================================

    Task<List<Entity>> GetAllEntitiesAsync(CancellationToken ct = default);
    Task<int> GetOrCreateEntityAsync(string name, CancellationToken ct = default);
    Task UpdateEntityAsync(Entity entity, CancellationToken ct = default);

    /// <summary>
    /// Attempts to delete an Entity (Institution).
    /// </summary>
    Task DeleteEntityAsync(int entityId, CancellationToken ct = default);

    /// <summary>
    /// Merges a duplicate Entity into a target Entity by re-parenting all child Accounts,
    /// then deletes the source Entity. Executes atomically.
    /// </summary>
    Task MergeEntitiesAsync(int sourceEntityId, int targetEntityId, CancellationToken ct = default);

    // =========================================================================
    // ACCOUNT MANAGEMENT
    // =========================================================================

    Task<List<Account>> GetAllAccountsAsync(CancellationToken ct = default);
    Task<List<Account>> GetAccountsByEntityIdAsync(int entityId, CancellationToken ct = default);
    Task<int> GetOrCreateAccountAsync(Account account, CancellationToken ct = default);
    Task UpdateAccountAsync(Account account, CancellationToken ct = default);

    /// <summary>
    /// Attempts to delete an Account.
    /// Throws InvalidOperationException (Hard Block) if historical transactions exist.
    /// </summary>
    Task DeleteAccountAsync(int accountId, CancellationToken ct = default);
}