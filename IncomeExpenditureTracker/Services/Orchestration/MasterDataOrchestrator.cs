using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Settings;
using Microsoft.Extensions.Logging;

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

    private readonly IImportBatchService _importBatchService;

    private readonly ISynonymService _synonymService;

    private readonly IPayeeService _payeeService;

    private readonly IUserSettingsService _userSettingsService;

    private readonly IApplicationBroker _broker;

    private readonly ILogger<MasterDataOrchestrator> _logger;

    /*
    * =========================================================================
    * ARCHITECTURAL NOTE: UI FACADE & TRANSACTION BOUNDARIES
    * =========================================================================
    * Why IDbConnection and IDbTransaction are omitted from Orchestrator methods:
    *
    * 1. Separation of Concerns (No Leaky Abstractions):
    *    The UI layer (Controllers, Blazor, etc.) should only express business intent
    *    (e.g., "AddSynonym" or "DeleteCategory"). It should not be responsible for
    *    managing database primitives or knowing about the underlying data store.
    *
    * 2. Single-Step Operations (Simple CRUD):
    *    For simple operations, the Orchestrator delegates directly to the underlying
    *    services. Those services handle creating and closing their own safe,
    *    temporary connections.
    *
    * 3. Multi-Step Operations (Complex Logic):
    *    When an operation spans multiple services (e.g., DeleteCategorySafeAsync),
    *    the Orchestrator acts as the transaction manager. It creates the transaction
    *    internally and passes the connection/transaction DOWN to the underlying services,
    *    but never exposes them UP to the UI.
    * =========================================================================
    */

    public MasterDataOrchestrator(
        IDatabaseService database,
        ICategoryService categoryService,
        ISubCategoryService subCategoryService,
        ITagService tagService,
        IEntityService entityService,
        IAccountService accountService,
        ITransactionService transactionService,
        IImportBatchService importBatchService,
        ISynonymService synonymService,
        IPayeeService payeeService,
        IUserSettingsService userSettingsService,
        IApplicationBroker applicationBroker,
        ILogger<MasterDataOrchestrator> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _categoryService = categoryService ?? throw new ArgumentNullException(nameof(categoryService));
        _subCategoryService = subCategoryService ?? throw new ArgumentNullException(nameof(subCategoryService));
        _tagService = tagService ?? throw new ArgumentNullException(nameof(tagService));
        _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _importBatchService = importBatchService ?? throw new ArgumentNullException(nameof(importBatchService));
        _synonymService = synonymService ?? throw new ArgumentNullException(nameof(synonymService));
        _payeeService = payeeService ?? throw new ArgumentNullException(nameof(payeeService));
        _userSettingsService = userSettingsService ?? throw new ArgumentNullException(nameof(userSettingsService));
        _broker = applicationBroker ?? throw new ArgumentNullException(nameof(applicationBroker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Validation Services

    public async Task<bool> IsUniqueAsync(DomainEntity entityType, string propertyName, string value, int? excludeId = null, CancellationToken ct = default)
    {
        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            // 1. Safely resolve table and primary key names to prevent SQL injection
            string tableName = entityType switch
            {
                DomainEntity.Category => "Categories",
                DomainEntity.SubCategory => "SubCategories",
                DomainEntity.Tag => "Tags",
                DomainEntity.Entity => "Entities",
                DomainEntity.Account => "Accounts",
                DomainEntity.Payee => "Payees",
                _ => throw new ArgumentException($"Unsupported entity type for uniqueness check: {entityType}")
            };

            string primaryKeyColumn = entityType switch
            {
                DomainEntity.Category => "CategoryId",
                DomainEntity.SubCategory => "SubCategoryId",
                DomainEntity.Tag => "TagId",
                DomainEntity.Entity => "EntityId",
                DomainEntity.Account => "AccountId",
                DomainEntity.Payee => "PayeeId",
                _ => "Id"
            };

            // 2. We use parameterized Dapper queries to securely check the value
            var sql = $@"
            SELECT COUNT(1)
            FROM {tableName}
            WHERE {propertyName} = @Value ";

            if (excludeId.HasValue)
            {
                sql += $" AND {primaryKeyColumn} != @ExcludeId";
            }

            int count = await conn.ExecuteScalarAsync<int>(sql, new { Value = value, ExcludeId = excludeId }, transaction: tx);

            return count == 0;
        }, ct);
    }

    public async Task<bool> IsUniqueAsync(DomainEntity entityType, Dictionary<string, object> properties, int? excludeId = null, CancellationToken ct = default)
    {
        if (properties == null || properties.Count == 0)
            throw new ArgumentException("At least one property must be provided for a uniqueness check.");

        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            string tableName = entityType switch
            {
                DomainEntity.Category => "Categories",
                DomainEntity.SubCategory => "SubCategories",
                DomainEntity.Tag => "Tags",
                DomainEntity.Entity => "Entities",
                DomainEntity.Account => "Accounts",
                DomainEntity.Payee => "Payees",
                DomainEntity.Synonym => "Synonyms",
                _ => throw new ArgumentException($"Unsupported entity type: {entityType}")
            };

            string primaryKeyColumn = entityType switch
            {
                DomainEntity.Category => "CategoryId",
                DomainEntity.SubCategory => "SubCategoryId",
                DomainEntity.Tag => "TagId",
                DomainEntity.Entity => "EntityId",
                DomainEntity.Account => "AccountId",
                DomainEntity.Payee => "PayeeId",
                _ => "Id"
            };

            var parameters = new Dapper.DynamicParameters();
            var whereClauses = new List<string>();

            // Dynamically build: "FieldType = @FieldType AND Synonym = @Synonym AND Category = @Category"
            foreach (var kvp in properties)
            {
                whereClauses.Add($"{kvp.Key} = @{kvp.Key}");
                parameters.Add(kvp.Key, kvp.Value);
            }

            var sql = $"SELECT COUNT(1) FROM {tableName} WHERE " + string.Join(" AND ", whereClauses);

            if (excludeId.HasValue)
            {
                sql += $" AND {primaryKeyColumn} != @ExcludeId";
                parameters.Add("ExcludeId", excludeId.Value);
            }

            int count = await conn.ExecuteScalarAsync<int>(sql, parameters, transaction: tx);
            return count == 0;
        }, ct);
    }

    #endregion

    #region Hierarchy Retrieval

    public async Task<IEnumerable<TagHierarchyDto>> GetAllTagsWithHierarchyAsync(CancellationToken ct = default)
    {
        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            const string sql = @"
            SELECT
                t.Id AS TagId,
                t.Name AS TagName,
                s.Id AS SubCategoryId,
                s.Name AS SubCategoryName,
                c.Id AS CategoryId,
                c.Name AS CategoryName
            FROM Tags t
            LEFT JOIN SubCategories s ON t.SubCategoryId = s.Id
            LEFT JOIN Categories c ON s.Id = c.CategoryId
            ORDER BY t.Name;";

            return await conn.QueryAsync<TagHierarchyDto>(sql, transaction: tx);
        }, ct);
    }

    public async Task<IEnumerable<SubCategoryHierarchyDto>> GetAllSubCategoriesWithHierarchyAsync(CancellationToken ct = default)
    {
        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            const string sql = @"
            SELECT
                s.Id AS SubCategoryId,
                s.Name AS SubCategoryName,
                c.Id AS CategoryId,
                c.Name AS CategoryName
            FROM SubCategories s
            LEFT JOIN Categories c ON s.CategoryId = c.Id
            ORDER BY s.Name;";

            return await conn.QueryAsync<SubCategoryHierarchyDto>(sql, transaction: tx);
        }, ct);
    }

    public async Task<IEnumerable<TagRuleHierarchyDto>> GetAllTagRulesWithHierarchyAsync(CancellationToken ct = default)
    {
        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            const string sql = @"
            SELECT
                tr.TagRuleId,
                tr.Id As TagId,
                t.Name AS TagName,
                tr.Keyword,
                s.Id AS SubCategoryId,
                s.Name AS SubCategoryName,
                c.Id AS CategoryId,
                c.Name AS CategoryName,
                tr.Priority
            FROM TagRules tr
            INNER JOIN Tags t ON tr.TagId = t.Id
            LEFT JOIN SubCategories s ON t.SubCategoryId = s.Id
            LEFT JOIN Categories c ON s.CategoryId = c.Id
            ORDER BY tr.Priority DESC, tr.Keyword;";

            return await conn.QueryAsync<TagRuleHierarchyDto>(sql, transaction: tx);
        }, ct);
    }

    public async Task<IEnumerable<SynonymsHierarchyDto>> GetAllSynonymsWithHierarchyAsync(CancellationToken ct = default)
    {
        return await _dbService.ExecuteWithRetryAsync(async (conn, tx) =>
        {
            // Assuming your Synonyms table uses Id and RawString based on standard nomenclature
            const string sql = @"
            SELECT
                Id AS SynonymsId,
                Synonym AS SynonymsName,
                Category AS CategoryName,
                Priority
            FROM Synonyms
            ORDER BY Category, Synonym;";

            return await conn.QueryAsync<SynonymsHierarchyDto>(sql, transaction: tx);
        }, ct);
    }

    #endregion

    // =========================================================================
    // IMPORTBATCH SERVICES
    // =========================================================================

    #region  ImportBatch Services

    public async Task<List<ImportBatch>> GetAllImportBatchesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _importBatchService.GetAllImportBatches(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.ImportBatch,
                CrudOperation.Read,
                "Locale_Error_Get_AllImportBatch"
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // CATEGORY MANAGEMENT
    // =========================================================================

    #region  Category Management

    public async Task<List<Category>> GetAllCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _categoryService.GetAllCategories(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Category,
                CrudOperation.Read,
                "Locale_Error_Get_AllCategory"
            ));
            throw;
        }
    }

    public async Task<int> GetOrCreateCategoryAsync(string name, CancellationToken ct = default)
    {
        try
        {
            int id = await _categoryService.GetOrCreateCategory(name, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved category '{CategoryName}'.", name);
            _broker.Send(new EntitySavedMessage(
                DomainEntity.Category,
                name
            ));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create category '{CategoryName}'.", name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Category,
                CrudOperation.Create,
                "Locale_Error_Create_Category",
                [name]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
    {
        try
        {
            await _categoryService.UpdateCategory(category, ct: ct);

            _logger.LogInformation("Successfully updated category ID {CategoryId}.", category.Id);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Category, category.Id, category.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category '{CategoryName}'.", category.Name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Category,
                CrudOperation.Update,
                "Locale_Error_Update_Category",
                [category.Name]
            ));
            throw;
        }
    }

    public async Task DeleteCategorySafeAsync(int categoryId, CancellationToken ct = default)
    {
        try
        {
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {
                // 1. Float all tags under this category (SubCategoryId = NULL) to protect financial history
                await _tagService.FloatTagsByCategoryAsync(categoryId, conn, tx, ct: ct);

                // 2. Wipe the orphaned SubCategories
                await _subCategoryService.DeleteByCategoryId(categoryId, conn, tx, ct: ct);

                // 3. Delete the target Category
                await _categoryService.DeleteCategory(categoryId, conn, tx, ct: ct);
            }, ct);

            _logger.LogInformation("Successfully executed Safe-Delete for category ID {CategoryId}.", categoryId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.Category, $"ID: {categoryId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete category ID {CategoryId}.", categoryId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Category,
                CrudOperation.Delete,
                "Locale_Error_Delete_Category",
                [categoryId.ToString()]
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // SUBCATEGORY MANAGEMENT
    // =========================================================================

    #region  SubCategory Management

    public async Task<List<SubCategory>> GetAllSubCategoriesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _subCategoryService.GetAllSubCategories(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.SubCategory,
                CrudOperation.Read,
                "Locale_Error_Read_AllSubCategory"
            ));
            throw;
        }
    }

    public async Task<List<SubCategory>> GetSubCategoriesByCategoryIdAsync(int categoryId, CancellationToken ct = default)

    {
        try
        {
            var result = await _subCategoryService.GetSubCategoriesByCategoryId(categoryId, ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.SubCategory,
                CrudOperation.Read,
                "Locale_Error_Read_SubCategoryByCategory",
                [categoryId.ToString()]
            ));
            throw;
        }
    }

    public async Task<int> GetOrCreateSubCategoryAsync(string name, int categoryId, CancellationToken ct = default)

    {
        try
        {
            int id = await _subCategoryService.GetOrCreateSubCategory(name, categoryId, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved SubCategory '{SubCategoryName}'.", name);
            _broker.Send(new EntitySavedMessage(DomainEntity.SubCategory, name));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create subcategory '{SubCategoryName}'.", name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.SubCategory,
                CrudOperation.Create,
                "Locale_Error_Create_SubCategory",
                [name]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateSubCategoryAsync(SubCategory subCategory, CancellationToken ct = default)
    {
        try
        {
            await _subCategoryService.UpdateSubCategory(subCategory, ct: ct);

            _logger.LogInformation("Successfully updated SubCategory ID {SubCategoryId}.", subCategory.Id);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.SubCategory, subCategory.Id, subCategory.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update subcategory '{SubCategoryName}'.", subCategory.Name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.SubCategory,
                CrudOperation.Update,
                "Locale_Error_Update_SubCategory",
                [subCategory.Name]
            ));
            throw;

        }
    }

    public async Task DeleteSubCategorySafeAsync(int subCategoryId, CancellationToken ct = default)
    {
        try
        {
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {
                // 1. Float all tags under this subcategory (SubCategoryId = NULL)
                await _tagService.FloatTagsBySubCategoryAsync(subCategoryId, conn, tx, ct: ct);

                // 2. Delete the SubCategory
                await _subCategoryService.DeleteSubCategory(subCategoryId, conn, tx, ct: ct);
            }, ct);

            _logger.LogInformation("Successfully executed Safe-Delete for subcategory ID {SubCategoryId}.", subCategoryId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.SubCategory, $"ID: {subCategoryId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete SubCategory ID {SubCategoryId}.", subCategoryId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.SubCategory,
                CrudOperation.Delete,
                "Locale_Error_Delete_SubCategory",
                [subCategoryId.ToString()]
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // TAG MANAGEMENT
    // =========================================================================

    #region  Tag Management

    public async Task<List<Tag>> GetAllTagsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _tagService.GetAllTags(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Tag,
                CrudOperation.Read,
                "Locale_Error_Read_AllTag"
            ));
            throw;
        }
    }

    public async Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, CancellationToken ct = default)
    {
        try
        {
            int id = await _tagService.GetOrCreateTagAsync(name, subCategoryId, ct: ct);

            _logger.LogInformation("Successfully created/retrieved tag '{TagName}'.", name);
            _broker.Send(new EntitySavedMessage(DomainEntity.Tag, name));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create tag '{TagName}'.", name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Tag,
                CrudOperation.Create,
                "Locale_Error_Create_Tag",
                [name]
            ));
            throw;
        }
    }

    public async Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default)
    {
        try
        {
            await _tagService.UpdateTagAsync(tagId, name, subCategoryId, ct: ct);

            _logger.LogInformation("Successfully updated tag ID {TagId} to '{TagName}'.", tagId, name);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Tag, tagId, name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tag '{TagName}'.", name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Tag,
                CrudOperation.Update,
                "Locale_Error_Update_Tag",
                [name]
            ));
            throw;
        }
    }

    public async Task DeleteTagSafeAsync(int tagId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Prevent deletion of the ultimate system safeguard
            int? miscTagId = await _tagService.GetTagIdByName(SystemConstants.MiscTag, ct: ct);

            if (!miscTagId.HasValue)
            {
                throw new InvalidOperationException("System integrity error: Misc Tag fallback is missing from the database.");
            }

            if (tagId == miscTagId.Value)
            {
                throw new InvalidOperationException("Cannot delete the System Misc Tag.");
            }

            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {
                // 1. Move financial transactions to the Misc Tag

                await _transactionService.ReassignTransactionsToFallbackTagAsync(tagId, miscTagId.Value, conn, tx, ct: ct);


                // 2. Wipe Tag Rules (Ensures no orphaned keywords if ON DELETE CASCADE isn't enabled)
                await _tagService.DeleteRulesByTagId(tagId, conn, tx, ct: ct);

                // 3. Delete the Tag
                await _tagService.DeleteTagAsync(tagId, conn, tx, ct: ct); // Assuming standard service maps conn/tx
            }, ct);
            _logger.LogInformation("Successfully safely deleted tag ID {TagId} and reassigned transactions.", tagId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.Tag, $"ID: {tagId}"));
        }
        catch (OperationCanceledException)
        {
            // Let cancellations bubble up without sending an error message to the UI
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete tag ID {TagId}.", tagId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Tag,
                CrudOperation.Delete,
                "Locale_Error_Delete_Tag",
                [tagId.ToString()]
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // TAG RULE MANAGEMENT
    // =========================================================================

    #region Tag Rule Management

    public async Task<RuleBookSnapshot> GetRuleBookSnapshotAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _tagService.GetRuleBookSnapshotAsync(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.TagRule,
                CrudOperation.Read,
                "Locale_Error_Read_TagRule"
            ));
            throw;
        }
    }
    public async Task<int> AddTagRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default)
    {
        try
        {
            int id = await _tagService.AddRuleAsync(keyword, tagId, priority, ct: ct);

            _logger.LogInformation("Successfully inserted keyword '{keyword}' for tag id '{TagId}'.", keyword, tagId);
            _broker.Send(new EntitySavedMessage(DomainEntity.TagRule, keyword));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert keyword '{keyword}' for tag id '{TagId}'.", keyword, tagId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.TagRule,
                CrudOperation.Create,
                "Locale_Error_Create_TagRule",
                [keyword]
            ));
            throw;
        }
    }

    public async Task UpdateTagRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default)
    {
        try
        {
            await _tagService.UpdateRuleAsync(ruleId, keyword, tagId, priority, ct: ct);

            _logger.LogInformation("Successfully updated ruleId {RuleId}.", ruleId);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.TagRule, ruleId, $"ID: {ruleId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update ruleId {RuleId}.", ruleId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.TagRule,
                CrudOperation.Update,
                "Locale_Error_Update_TagRule",
                [ruleId.ToString()]
            ));
            throw;
        }
    }

    public async Task DeleteTagRuleAsync(int ruleId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.DeleteRuleAsync(ruleId, ct: ct);
            _logger.LogInformation("Successfully safely deleted tagRule ID {TagRuleId} and reassigned transactions.", ruleId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.TagRule, $"ID: {ruleId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete tagRule ID {TagRuleId}.", ruleId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.TagRule,
                CrudOperation.Delete,
                "Locale_Error_Delete_TagRule",
                [ruleId.ToString()]
            ));
            throw;
        }
    }

    public async Task DeleteTagRulesByKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.DeleteRuleKeywordsAsync(keywords, tagId, ct: ct);

            _logger.LogInformation("Successfully safely deleted keywords for tag ID {TagId} and reassigned transactions.", tagId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.TagRule, $"Deleted Keywords for TagID: {tagId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete keywords for tag ID {TagId}.", tagId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.TagRule,
                CrudOperation.Delete,
                "Locale_Error_Delete_TagRule_Keywords",
                [tagId.ToString()]
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // ENTITY MANAGEMENT (Institutions / Merchants)
    // =========================================================================

    #region ENTITY Management

    public async Task<List<Entity>> GetAllEntitiesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _entityService.GetAllEntities(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Entity,
                CrudOperation.Read,
                "Locale_Error_Read_AllEntity"
            ));
            throw;
        }
    }

    public async Task<int> GetOrCreateEntityAsync(string name, CancellationToken ct = default)
    {
        try
        {
            int id = await _entityService.GetOrCreateEntity(name, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved entity '{EntityName}'.", name);
            _broker.Send(new EntitySavedMessage(DomainEntity.Entity, name));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create entity '{EntityName}'.", name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Entity,
                CrudOperation.Create,
                "Locale_Error_Create_Entity",
                [name]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateEntityAsync(Entity entity, CancellationToken ct = default)
    {
        try
        {
            await _entityService.UpdateEntity(entity, ct: ct);

            _logger.LogInformation("Successfully updated Entity ID {EntityId}.", entity.Id);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Entity, entity.Id, entity.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category '{EntityName}'.", entity.Name);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Entity,
                CrudOperation.Update,
                "Locale_Error_Update_Entity",
                [entity.Name]
            ));
            throw;

        }
    }

    public async Task DeleteEntityAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            await _entityService.DeleteEntity(entityId, ct: ct);

            _logger.LogInformation("Successfully executed Safe-Delete for entity ID {EntityId}.", entityId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.Entity, $"ID: {entityId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete Entity ID {EntityId}.", entityId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Entity,
                CrudOperation.Delete,
                "Locale_Error_Delete_Entity",
                [entityId.ToString()]
            ));
            throw;
        }
    }

    public async Task MergeEntitiesAsync(int sourceEntityId, int targetEntityId, CancellationToken ct = default)
    {
        try
        {
            if (sourceEntityId == targetEntityId)
            {
                throw new InvalidOperationException("Source and target entities cannot be the same.");
            }

            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {
                // 1. Reassign all child accounts from source to target
                await _accountService.ReassignAccountsAsync(sourceEntityId, targetEntityId, conn, tx, ct: ct);

                // 2. Wipe the old duplicate Entity
                await _entityService.DeleteEntity(sourceEntityId, conn, tx, ct: ct);
            }, ct);

            _logger.LogInformation("Successfully executed Safe-Reassign Source Entity ID {sourceEntityId} to Target Entity ID {targetEntityId}.", sourceEntityId, targetEntityId);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Entity, null, $"SOURCE_ID: {sourceEntityId}, TARGET_ID: {targetEntityId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Reassign Source Entity ID {sourceEntityId} to Target Entity ID {targetEntityId}.", sourceEntityId, targetEntityId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Entity,
                CrudOperation.Merge,
                "Locale_Error_Merge_Entity",
                [sourceEntityId.ToString(), targetEntityId.ToString()]
            ));
            throw;
        }
    }

    #endregion

    // =========================================================================
    // ACCOUNT MANAGEMENT
    // =========================================================================

    #region  Account Management

    public async Task<List<Account>> GetAllAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _accountService.GetAllAccounts(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Account,
                CrudOperation.Read,
                "Locale_Error_Read_AllAccount"
            ));
            throw;
        }
    }

    public async Task<List<Account>> GetAccountsByEntityIdAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            var result = await _accountService.GetAccountsByEntityId(entityId, ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Account,
                CrudOperation.Read,
                "Locale_Error_Read_AccountByEntity",
                [entityId.ToString()]
            ));
            throw;
        }
    }

    public async Task<int> GetOrCreateAccountAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            int id = await _accountService.GetOrCreateAccount(account, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved Account '{AccountName}'.", account.AccountNumber);
            _broker.Send(new EntitySavedMessage(DomainEntity.Account, account.AccountNumber));

            return id;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create Account '{AccountName}'.", account.AccountNumber);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Account,
                CrudOperation.Create,
                "Locale_Error_Create_Account",
                [account.AccountNumber]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateAccountAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            await _accountService.UpdateAccount(account, ct: ct);

            _logger.LogInformation("Successfully updated Account ID {AccountId}.", account.Id);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Account, account.Id, account.AccountNumber));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Account Number'{AccountNumber}'.", account.AccountNumber);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Account,
                CrudOperation.Update,
                "Locale_Error_Update_Account",
                [account.AccountNumber]
            ));
            throw;

        }
    }

    public async Task DeleteAccountAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            bool hasTransactions = await _accountService.HasTransactionsAsync(accountId, ct: ct);

            if (hasTransactions)
            {
                throw new InvalidOperationException("Hard Block: Cannot delete this Account because it contains historical transactions. Please revert or delete the imported transaction history first.");
            }
            await _accountService.DeleteAccount(accountId, ct: ct);

            _logger.LogInformation("Successfully executed Safe-Delete for Account ID {AccountId}.", accountId);
            _broker.Send(new EntityDeletedMessage(DomainEntity.Account, $"ID: {accountId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete Account ID {AccountId}.", accountId);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Account,
                CrudOperation.Delete,
                "Locale_Error_Delete_Account",
                [accountId.ToString()]
            ));
            throw;

        }
    }

    #endregion

    // =========================================================================
    // SYNONYM MANAGEMENT
    // =========================================================================

    #region  Synonym Management

    public async Task<IEnumerable<Synonyms>> GetAllSynonymsAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _synonymService.GetAllSynonyms(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Read,
                "Locale_Error_Read_AllSynonym"
            ));
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategoryAsync(string category, CancellationToken ct = default)
    {
        try
        {
            var result = await _synonymService.GetSynonymsByCategory(category, ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Read,
                "Locale_Error_Read_SynonymByCategory",
                [category]
            ));
            throw;
        }
    }

    public async Task LearnFromCorrectionAsync(string rawSynonym, string fieldType, string category, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.LearnFromCorrectionAsync(rawSynonym, fieldType, category, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully updated Synonym details '{rawSynonym}' for field type '{fieldType}'.", rawSynonym, fieldType);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Synonym, null, rawSynonym));

        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to updated Synonym details '{rawSynonym}' for field type '{fieldType}'.", rawSynonym, fieldType);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Update,
                "Locale_Error_Update_SynonymCorrection",
                [rawSynonym, fieldType]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task AddSynonymAsync(Synonyms synonym, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.AddSynonymAsync(synonym, ct: ct);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully inserted Synonym '{Synonym}'.", synonym.Synonym);
            _broker.Send(new EntitySavedMessage(DomainEntity.Synonym, synonym.Synonym));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create Synonym '{Synonym}'.", synonym.Synonym);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Create,
                "Locale_Error_Create_Synonym",
                [synonym.Synonym]
            ));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateSynonymAsync(Synonyms synonym, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.UpdateSynonymAsync(synonym, ct: ct);

            _logger.LogInformation("Successfully updated Synonym ID {SynonymID}.", synonym.Id);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Synonym, synonym.Id, synonym.Synonym));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Synonym Number'{Synonym}'.", synonym.Synonym);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Update,
                "Locale_Error_Update_Synonym",
                [synonym.Synonym]
            ));
            throw;

        }
    }

    public async Task DeleteSynonymAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.DeleteSynonymAsync(id, ct: ct);

            _logger.LogInformation("Successfully executed Safe-Delete Synonym ID {SynonymID}.", id);
            _broker.Send(new EntityDeletedMessage(DomainEntity.Synonym, $"ID: {id}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete  Synonym ID {SynonymID}.", id);
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Synonym,
                CrudOperation.Delete,
                "Locale_Error_Delete_Synonym",
                [id.ToString()]
            ));
            throw;

        }
    }

    #endregion

    // =========================================================================
    // User Preference MANAGEMENT
    // =========================================================================
    #region User Preference Management

    public async Task<string?> GetUserSettingsAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var result = await _userSettingsService.GetSettingAsync(key, ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.UserSetting,
                CrudOperation.Read,
                "Locale_Error_Read_UserSetting",
                [key]
            ));
            throw;
        }
    }

    public async Task SetUserSettingAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            await _userSettingsService.SetSettingAsync(key, value, ct: ct);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.UserSetting, null, key));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.UserSetting,
                CrudOperation.Update,
                "Locale_Error_Update_UserSetting",
                [key]
            ));
            throw;
        }
    }

    public async Task<List<UserSetting>> GetAllSettingsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _userSettingsService.GetAllSettingsAsync(ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.UserSetting,
                CrudOperation.Read,
                "Locale_Error_Read_AllUserSettings"
            ));
            throw;
        }
    }
    #endregion

    // =========================================================================
    // PAYEE MANAGEMENT
    // =========================================================================
    #region Payee Management

    public async Task<IEnumerable<Payee>> GetAllPayeesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _payeeService.GetAllAsync(ct: ct);
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Payee,
                CrudOperation.Read,
                "Locale_Error_Read_AllPayee"
            ));
            throw;
        }
    }

    public async Task<Payee> CreatePayeeAsync(Payee payee, CancellationToken ct = default)
    {
        try
        {
            var result = await _payeeService.CreateAsync(payee, ct: ct);
            _broker.Send(new EntitySavedMessage(DomainEntity.Payee, payee.Name));
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Payee,
                CrudOperation.Create,
                "Locale_Error_Create_Payee",
                [payee.Name]
            ));
            throw;
        }
    }

    public async Task UpdatePayeeAsync(Payee payee, CancellationToken ct = default)
    {
        try
        {
            await _payeeService.UpdateAsync(payee, ct: ct);
            _broker.Send(new EntityUpdatedMessage(DomainEntity.Payee, payee.Id, payee.Name));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Payee,
                CrudOperation.Update,
                "Locale_Error_Update_Payee",
                [payee.Name]
            ));
            throw;
        }
    }

    public async Task DeletePayeeAsync(long payeeId, CancellationToken ct = default)
    {
        try
        {
            await _payeeService.DeleteAsync(payeeId, ct: ct);

            _broker.Send(new EntityDeletedMessage(DomainEntity.Payee, $"ID: {payeeId}"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Payee,
                CrudOperation.Delete,
                "Locale_Error_Delete_Payee",
                [payeeId.ToString()]
            ));
            throw;
        }
    }

    public async Task AddPayeeMappingAsync(string cleanedDescription, long payeeId, CancellationToken ct = default)
    {
        try
        {
            await _payeeService.AddMappingAsync(cleanedDescription, payeeId, ct: ct);

            _broker.Send(new EntitySavedMessage(DomainEntity.PayeeMapping, cleanedDescription));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.PayeeMapping,
                CrudOperation.Create,
                "Locale_Error_Create_PayeeMapping",
                [cleanedDescription]
            ));
            throw;
        }
    }

    public async Task MergePayeesAsync(long targetPayeeId, List<long>? sourcePayeeIds, CancellationToken ct = default)
    {
        // 1. Fail fast if the user cancelled
        ct.ThrowIfCancellationRequested();

        // 2. Guard clause: validate input before calling the mocked service
        ArgumentNullException.ThrowIfNull(sourcePayeeIds);

        if (sourcePayeeIds.Contains(targetPayeeId))
        {
            throw new InvalidOperationException("Target payee cannot be present in the source payees list.");
        }

        try
        {
            await _payeeService.MergeEntitiesAsync(targetPayeeId, sourcePayeeIds, null, null, ct: ct);

            _broker.Send(new EntityUpdatedMessage(DomainEntity.Payee, targetPayeeId, $"Merged into Target_ID: {targetPayeeId}"));
        }
        catch (OperationCanceledException)
        {
            // Let cancellations bubble up without sending an error message to the UI
            throw;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage(
                DomainEntity.Payee,
                CrudOperation.Merge,
                "Locale_Error_Merge_Payee",
                [targetPayeeId.ToString()]
            ));
            throw;
        }
    }
    #endregion

}