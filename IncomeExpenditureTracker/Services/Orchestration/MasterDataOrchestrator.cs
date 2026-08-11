using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Tagging;

namespace IncomeExpenditureTracker.Services.Orchestration;

/// <summary>
/// The centralized UI facade for Master Data Management.
/// Enforces Safe Re-Parenting for the Taxonomy Tree (Tag is King)
/// and Hard Blocks for the Structural Tree (Entity/Account).
/// </summary>
public class MasterDataOrchestrator : IMasterDataOrchestrator
{
    private readonly IDatabaseService _database;
    private readonly ICategoryService _categoryService;
    private readonly ISubCategoryService _subCategoryService;
    private readonly ITagService _tagService;
    private readonly IEntityService _entityService;
    private readonly IAccountService _accountService;
    private readonly ITransactionService _transactionService;

    public MasterDataOrchestrator(
        IDatabaseService database,
        ICategoryService categoryService,
        ISubCategoryService subCategoryService,
        ITagService tagService,
        IEntityService entityService,
        IAccountService accountService,
        ITransactionService transactionService)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _categoryService = categoryService ?? throw new ArgumentNullException(nameof(categoryService));
        _subCategoryService = subCategoryService ?? throw new ArgumentNullException(nameof(subCategoryService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    // =========================================================================
    // CATEGORY MANAGEMENT
    // =========================================================================

    public Task<List<Category>> GetAllCategoriesAsync(CancellationToken ct = default)
        => _categoryService.GetAllCategories();

    public Task<int> GetOrCreateCategoryAsync(string name, CancellationToken ct = default)
        => _categoryService.GetOrCreateCategory(name);

    public Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
        => _categoryService.UpdateCategory(category);

    public async Task DeleteCategorySafeAsync(int categoryId, CancellationToken ct = default)
    {
        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            // 1. Float all tags under this category (SubCategoryId = NULL) to protect financial history
            await _tagService.FloatTagsByCategoryAsync(categoryId, conn, tx);

            // 2. Wipe the orphaned SubCategories
            await _subCategoryService.DeleteByCategoryId(categoryId, conn, tx);

            // 3. Delete the target Category
            await _categoryService.DeleteCategory(categoryId, conn, tx);
        });
    }

    // =========================================================================
    // SUBCATEGORY MANAGEMENT
    // =========================================================================

    public Task<List<SubCategory>> GetAllSubCategoriesAsync(CancellationToken ct = default)
        => _subCategoryService.GetAllSubCategories();

    public Task<List<SubCategory>> GetSubCategoriesByCategoryIdAsync(int categoryId, CancellationToken ct = default)
        => _subCategoryService.GetSubCategoriesByCategoryId(categoryId);

    public Task<int> GetOrCreateSubCategoryAsync(string name, int categoryId, CancellationToken ct = default)
        => _subCategoryService.GetOrCreateSubCategory(name, categoryId);

    public Task UpdateSubCategoryAsync(SubCategory subCategory, CancellationToken ct = default)
        => _subCategoryService.UpdateSubCategory(subCategory);

    public async Task DeleteSubCategorySafeAsync(int subCategoryId, CancellationToken ct = default)
    {
        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            // 1. Float all tags under this subcategory (SubCategoryId = NULL)
            await _tagService.FloatTagsBySubCategoryAsync(subCategoryId, conn, tx);

            // 2. Delete the SubCategory
            await _subCategoryService.DeleteSubCategory(subCategoryId, conn, tx);
        });

        _tagService.InvalidateCache();
    }

    // =========================================================================
    // TAG MANAGEMENT
    // =========================================================================

    public Task<RuleBookSnapshot> GetRuleBookSnapshotAsync(CancellationToken ct = default)
        => _tagService.GetRuleBookSnapshotAsync();

    public Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, CancellationToken ct = default)
        => _tagService.GetOrCreateTagAsync(name, subCategoryId);

    public Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default)
        => _tagService.UpdateTagAsync(tagId, name, subCategoryId);

    public async Task DeleteTagSafeAsync(int tagId, CancellationToken ct = default)
    {
        // Prevent deletion of the ultimate system safeguard
        int? miscTagId = await _tagService.GetTagIdByName(SystemConstants.MiscTag);

        if (!miscTagId.HasValue)
        {
            throw new InvalidOperationException("System integrity error: Misc Tag fallback is missing from the database.");
        }

        if (tagId == miscTagId.Value)
        {
            throw new InvalidOperationException("Cannot delete the System Misc Tag.");
        }

        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            // 1. Move financial transactions to the Misc Tag

            await _transactionService.ReassignTransactionsToFallbackTagAsync(tagId, miscTagId.Value, conn, tx);


            // 2. Wipe Tag Rules (Ensures no orphaned keywords if ON DELETE CASCADE isn't enabled)
            await _tagService.DeleteRulesByTagId(tagId, conn, tx);

            // 3. Delete the Tag
            await _tagService.DeleteTagAsync(tagId, conn, tx); // Assuming standard service maps conn/tx
        });
    }

    // =========================================================================
    // TAG RULE MANAGEMENT
    // =========================================================================

    public Task<int> AddTagRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default)
        => _tagService.AddRuleAsync(keyword, tagId, priority);

    public Task UpdateTagRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default)
        => _tagService.UpdateRuleAsync(ruleId, keyword, tagId, priority);

    public Task DeleteTagRuleAsync(int ruleId, CancellationToken ct = default)
        => _tagService.DeleteRuleAsync(ruleId);

    public Task DeleteTagRulesByKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default)
        => _tagService.DeleteRuleKeywordsAsync(keywords, tagId);

    // =========================================================================
    // ENTITY MANAGEMENT (Institutions / Merchants)
    // =========================================================================

    public Task<List<Entity>> GetAllEntitiesAsync(CancellationToken ct = default)
        => _entityService.GetAllEntities();

    public Task<int> GetOrCreateEntityAsync(string name, CancellationToken ct = default)
        => _entityService.GetOrCreateEntity(name);

    public Task UpdateEntityAsync(Entity entity, CancellationToken ct = default)
        => _entityService.UpdateEntity(entity);

    public async Task DeleteEntityAsync(int entityId, CancellationToken ct = default)
    {
        await _entityService.DeleteEntity(entityId);
    }

    public async Task MergeEntitiesAsync(int sourceEntityId, int targetEntityId, CancellationToken ct = default)
    {
        if (sourceEntityId == targetEntityId)
        {
            throw new InvalidOperationException("Source and target entities cannot be the same.");
        }

        await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
        {
            // 1. Reassign all child accounts from source to target
            await _accountService.ReassignAccountsAsync(sourceEntityId, targetEntityId, conn, tx);

            // 2. Wipe the old duplicate Entity
            await _entityService.DeleteEntity(sourceEntityId, conn, tx);
        });
    }

    // =========================================================================
    // ACCOUNT MANAGEMENT
    // =========================================================================

    public Task<List<Account>> GetAllAccountsAsync(CancellationToken ct = default)
        => _accountService.GetAllAccounts();

    public Task<List<Account>> GetAccountsByEntityIdAsync(int entityId, CancellationToken ct = default)
        => _accountService.GetAccountsByEntityId(entityId);

    public Task<int> GetOrCreateAccountAsync(Account account, CancellationToken ct = default)
        => _accountService.GetOrCreateAccount(account);

    public Task UpdateAccountAsync(Account account, CancellationToken ct = default)
        => _accountService.UpdateAccount(account);

    public async Task DeleteAccountAsync(int accountId, CancellationToken ct = default)
    {
        bool hasTransactions = await _accountService.HasTransactionsAsync(accountId);

        if (hasTransactions)
        {
            throw new InvalidOperationException("Hard Block: Cannot delete this Account because it contains historical transactions. Please revert or delete the imported transaction history first.");
        }

        await _accountService.DeleteAccount(accountId);
    }
}