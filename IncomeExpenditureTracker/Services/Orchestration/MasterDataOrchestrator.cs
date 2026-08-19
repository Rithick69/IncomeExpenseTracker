using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Messaging;
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
        _broker = applicationBroker ?? throw new ArgumentNullException(nameof(applicationBroker));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // =========================================================================
    // IMPORTBATCH SERVICES
    // =========================================================================

    #region  ImportBatch Services

    public async Task<List<ImportBatch>> GetAllImportBatchesAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _importBatchService.GetAllImportBatches();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("ImportBatch", "Get", $"Could not fetch imported batches."));
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
            var result = await _categoryService.GetAllCategories();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Category", "Get", $"Could not fetch categories."));
            throw;
        }
    }

    public async Task<int> GetOrCreateCategoryAsync(string name, CancellationToken ct = default)
    {
        try
        {
            int id = await _categoryService.GetOrCreateCategory(name);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved category '{CategoryName}'.", name);
            _broker.Send(new EntitySavedMessage("Category", name));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create category '{CategoryName}'.", name);
            _broker.Send(new CrudErrorMessage("Category", "Create", $"Could not save '{name}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateCategoryAsync(Category category, CancellationToken ct = default)
    {
        try
        {
            await _categoryService.UpdateCategory(category);

            _logger.LogInformation("Successfully updated category ID {CategoryId}.", category.Id);
            _broker.Send(new EntityUpdatedMessage("Category", category.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category '{CategoryName}'.", category.Name);
            _broker.Send(new CrudErrorMessage("Category", "Update", $"Could not update '{category.Name}'."));
            throw;
        }
    }

    public async Task DeleteCategorySafeAsync(int categoryId, CancellationToken ct = default)
    {
        try
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

            _logger.LogInformation("Successfully executed Safe-Delete for category ID {CategoryId}.", categoryId);
            _broker.Send(new EntityDeletedMessage("Category", $"ID: {categoryId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete category ID {CategoryId}.", categoryId);
            _broker.Send(new CrudErrorMessage("Category", "Delete", "Failed to safely delete the category. Ensure no protected transactions are attached."));
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
            var result = await _subCategoryService.GetAllSubCategories();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("SubCategory", "Get", $"Could not fetch subcategories."));
            throw;
        }
    }

    public async Task<List<SubCategory>> GetSubCategoriesByCategoryIdAsync(int categoryId, CancellationToken ct = default)

    {
        try
        {
            var result = await _subCategoryService.GetSubCategoriesByCategoryId(categoryId);
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("SubCategory", "Get", $"Could not fetch subcategories for categoryID {categoryId}."));
            throw;
        }
    }

    public async Task<int> GetOrCreateSubCategoryAsync(string name, int categoryId, CancellationToken ct = default)

    {
        try
        {
            int id = await _subCategoryService.GetOrCreateSubCategory(name, categoryId);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved SubCategory '{SubCategoryName}'.", name);
            _broker.Send(new EntitySavedMessage("SubCategory", name));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create subcategory '{SubCategoryName}'.", name);
            _broker.Send(new CrudErrorMessage("SubCategory", "Create", $"Could not save '{name}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateSubCategoryAsync(SubCategory subCategory, CancellationToken ct = default)
    {
        try
        {
            await _subCategoryService.UpdateSubCategory(subCategory);

            _logger.LogInformation("Successfully updated SubCategory ID {SubCategoryId}.", subCategory.Id);
            _broker.Send(new EntityUpdatedMessage("SubCategory", subCategory.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update subcategory '{SubCategoryName}'.", subCategory.Name);
            _broker.Send(new CrudErrorMessage("SubCategory", "Update", $"Could not update '{subCategory.Name}'."));
            throw;

        }
    }

    public async Task DeleteSubCategorySafeAsync(int subCategoryId, CancellationToken ct = default)
    {
        try
        {
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
            {
                // 1. Float all tags under this subcategory (SubCategoryId = NULL)
                await _tagService.FloatTagsBySubCategoryAsync(subCategoryId, conn, tx);

                // 2. Delete the SubCategory
                await _subCategoryService.DeleteSubCategory(subCategoryId, conn, tx);
            });

            _logger.LogInformation("Successfully executed Safe-Delete for subcategory ID {SubCategoryId}.", subCategoryId);
            _broker.Send(new EntityDeletedMessage("SubCategory", $"ID: {subCategoryId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete SubCategory ID {SubCategoryId}.", subCategoryId);
            _broker.Send(new CrudErrorMessage("SubCategory", "Delete", "Failed to safely delete the SubCategory. Ensure no protected transactions are attached."));
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
            var result = await _tagService.GetAllTags();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Tag", "Get", $"Could not fetch tags."));
            throw;
        }
    }

    public async Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, CancellationToken ct = default)
    {
        try
        {
            int id = await _tagService.GetOrCreateTagAsync(name, subCategoryId);

            _logger.LogInformation("Successfully created/retrieved tag '{TagName}'.", name);
            _broker.Send(new EntitySavedMessage("Tag", name));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create tag '{TagName}'.", name);
            _broker.Send(new CrudErrorMessage("Tag", "Create", $"Could not save tag '{name}'."));
            throw;
        }
    }

    public async Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default)
    {
        try
        {
            await _tagService.UpdateTagAsync(tagId, name, subCategoryId);

            _logger.LogInformation("Successfully updated tag ID {TagId} to '{TagName}'.", tagId, name);
            _broker.Send(new EntityUpdatedMessage("Tag", name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update tag '{TagName}'.", name);
            _broker.Send(new CrudErrorMessage("Tag", "Update", $"Could not update tag '{name}'."));
            throw;
        }
    }

    public async Task DeleteTagSafeAsync(int tagId, CancellationToken ct = default)
    {
        try
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
            _logger.LogInformation("Successfully safely deleted tag ID {TagId} and reassigned transactions.", tagId);
            _broker.Send(new EntityDeletedMessage("Tag", $"ID: {tagId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete tag ID {TagId}.", tagId);
            _broker.Send(new CrudErrorMessage("Tag", "Delete", "Failed to safely delete this tag."));
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
            var result = await _tagService.GetRuleBookSnapshotAsync();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("TagRule", "Get", $"Could not fetch tag rules."));
            throw;
        }
    }
    public async Task<int> AddTagRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default)
    {
        try
        {
            int id = await _tagService.AddRuleAsync(keyword, tagId, priority);

            _logger.LogInformation("Successfully inserted keyword '{keyword}' for tag id '{TagId}'.", keyword, tagId);
            _broker.Send(new EntitySavedMessage("TagRule", keyword));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to insert keyword '{keyword}' for tag id '{TagId}'.", keyword, tagId);
            _broker.Send(new CrudErrorMessage("TagRule", "Create", $"Could not save keyword '{keyword}'."));
            throw;
        }
    }

    public async Task UpdateTagRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default)
    {
        try
        {
            await _tagService.UpdateRuleAsync(ruleId, keyword, tagId, priority);

            _logger.LogInformation("Successfully updated ruleId {RuleId}.", ruleId);
            _broker.Send(new EntityUpdatedMessage("TagRule", $"ID: {ruleId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update ruleId {RuleId}.", ruleId);
            _broker.Send(new CrudErrorMessage("TagRule", "Update", $"Could not update tagRule id'{ruleId}'."));
            throw;
        }
    }

    public async Task DeleteTagRuleAsync(int ruleId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.DeleteRuleAsync(ruleId);
            _logger.LogInformation("Successfully safely deleted tagRule ID {TagRuleId} and reassigned transactions.", ruleId);
            _broker.Send(new EntityDeletedMessage("TagRule", $"ID: {ruleId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete tagRule ID {TagRuleId}.", ruleId);
            _broker.Send(new CrudErrorMessage("TagRule", "Delete", "Failed to safely delete this tagRule."));
            throw;
        }
    }

    public async Task DeleteTagRulesByKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default)
    {
        try
        {
            await _tagService.DeleteRuleKeywordsAsync(keywords, tagId);
            _logger.LogInformation("Successfully safely deleted keywords for tag ID {TagId} and reassigned transactions.", tagId);
            _broker.Send(new EntityDeletedMessage("TagRule", $"Deleted Keywords for TagID: {tagId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to safely delete keywords for tag ID {TagId}.", tagId);
            _broker.Send(new CrudErrorMessage("TagRule", "Delete", "Failed to safely delete keywords for this tagId."));
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
            var result = await _entityService.GetAllEntities();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Entity", "Get", $"Could not fetch entities."));
            throw;
        }
    }

    public async Task<int> GetOrCreateEntityAsync(string name, CancellationToken ct = default)
    {
        try
        {
            int id = await _entityService.GetOrCreateEntity(name);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved entity '{EntityName}'.", name);
            _broker.Send(new EntitySavedMessage("Entity", name));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create entity '{EntityName}'.", name);
            _broker.Send(new CrudErrorMessage("Entity", "Create", $"Could not save '{name}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateEntityAsync(Entity entity, CancellationToken ct = default)
    {
        try
        {
            await _entityService.UpdateEntity(entity);

            _logger.LogInformation("Successfully updated Entity ID {EntityId}.", entity.Id);
            _broker.Send(new EntityUpdatedMessage("Entity", entity.Name));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category '{EntityName}'.", entity.Name);
            _broker.Send(new CrudErrorMessage("Entity", "Update", $"Could not update '{entity.Name}'."));
            throw;

        }
    }

    public async Task DeleteEntityAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            await _entityService.DeleteEntity(entityId);

            _logger.LogInformation("Successfully executed Safe-Delete for entity ID {EntityId}.", entityId);
            _broker.Send(new EntityDeletedMessage("Entity", $"ID: {entityId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete Entity ID {EntityId}.", entityId);
            _broker.Send(new CrudErrorMessage("Entity", "Delete", "Failed to safely delete the Entity. Ensure no protected transactions are attached."));
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

            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
            {
                // 1. Reassign all child accounts from source to target
                await _accountService.ReassignAccountsAsync(sourceEntityId, targetEntityId, conn, tx);

                // 2. Wipe the old duplicate Entity
                await _entityService.DeleteEntity(sourceEntityId, conn, tx);
            });

            _logger.LogInformation("Successfully executed Safe-Reassign Source Entity ID {sourceEntityId} to Target Entity ID {targetEntityId}.", sourceEntityId, targetEntityId);
            _broker.Send(new EntityUpdatedMessage("Entity", $"SOURCE_ID: {sourceEntityId}, TARGET_ID: {targetEntityId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Reassign Source Entity ID {sourceEntityId} to Target Entity ID {targetEntityId}.", sourceEntityId, targetEntityId);
            _broker.Send(new CrudErrorMessage("Entity", "Reassignment", "Failed to safely reassign to target Entity. Ensure no protected transactions are attached."));
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
            var result = await _accountService.GetAllAccounts();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Account", "Get", $"Could not fetch accounts."));
            throw;
        }
    }

    public async Task<List<Account>> GetAccountsByEntityIdAsync(int entityId, CancellationToken ct = default)
    {
        try
        {
            var result = await _accountService.GetAccountsByEntityId(entityId);
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Account", "Get", $"Could not fetch accounts for entity id {entityId}"));
            throw;
        }
    }

    public async Task<int> GetOrCreateAccountAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            int id = await _accountService.GetOrCreateAccount(account);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully created/retrieved Account '{AccountName}'.", account.AccountNumber);
            _broker.Send(new EntitySavedMessage("Account", account.AccountNumber));

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create Account '{AccountName}'.", account.AccountNumber);
            _broker.Send(new CrudErrorMessage("Account", "Create", $"Could not save '{account.AccountNumber}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateAccountAsync(Account account, CancellationToken ct = default)
    {
        try
        {
            await _accountService.UpdateAccount(account);

            _logger.LogInformation("Successfully updated Account ID {AccountId}.", account.Id);
            _broker.Send(new EntityUpdatedMessage("Account", account.AccountNumber));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Account Number'{AccountNumber}'.", account.AccountNumber);
            _broker.Send(new CrudErrorMessage("Account", "Update", $"Could not update '{account.AccountNumber}'."));
            throw;

        }
    }

    public async Task DeleteAccountAsync(int accountId, CancellationToken ct = default)
    {
        try
        {
            bool hasTransactions = await _accountService.HasTransactionsAsync(accountId);

            if (hasTransactions)
            {
                throw new InvalidOperationException("Hard Block: Cannot delete this Account because it contains historical transactions. Please revert or delete the imported transaction history first.");
            }
            await _accountService.DeleteAccount(accountId);

            _logger.LogInformation("Successfully executed Safe-Delete for Account ID {AccountId}.", accountId);
            _broker.Send(new EntityDeletedMessage("Account", $"ID: {accountId}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete Account ID {AccountId}.", accountId);
            _broker.Send(new CrudErrorMessage("Account", "Delete", "Failed to safely delete the Account. Ensure no protected transactions are attached."));
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
            var result = await _synonymService.GetAllSynonyms();
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Synonyms", "Get", $"Could not fetch synonyms."));
            throw;
        }
    }

    public async Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategoryAsync(string category, CancellationToken ct = default)
    {
        try
        {
            var result = await _synonymService.GetSynonymsByCategory(category);
            return result;
        }
        catch (Exception)
        {
            _broker.Send(new CrudErrorMessage("Synonyms", "Get", $"Could not fetch synonyms for category {category}."));
            throw;
        }
    }

    public async Task LearnFromCorrectionAsync(string rawSynonym, string fieldType, string category, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.LearnFromCorrectionAsync(rawSynonym, fieldType, category);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully updated Synonym details '{rawSynonym}' for field type '{fieldType}'.", rawSynonym, fieldType);
            _broker.Send(new EntityUpdatedMessage("Synonym", rawSynonym));

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to updated Synonym details '{rawSynonym}' for field type '{fieldType}'.", rawSynonym, fieldType);
            _broker.Send(new CrudErrorMessage("Synonym", "Update", $"Could not update '{rawSynonym}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task AddSynonymAsync(Synonyms synonym, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.AddSynonymAsync(synonym);

            // Audit History and UI Notification
            _logger.LogInformation("Successfully inserted Synonym '{Synonym}'.", synonym.Synonym);
            _broker.Send(new EntitySavedMessage("Synonym", synonym.Synonym));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database failed to create Synonym '{Synonym}'.", synonym.Synonym);
            _broker.Send(new CrudErrorMessage("Synonym", "Create", $"Could not save '{synonym.Synonym}'."));
            throw; // Rethrow so the calling code knows the operation ultimately failed
        }
    }

    public async Task UpdateSynonymAsync(Synonyms synonym, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.UpdateSynonymAsync(synonym);

            _logger.LogInformation("Successfully updated Synonym ID {SynonymID}.", synonym.Id);
            _broker.Send(new EntityUpdatedMessage("Synonym", synonym.Synonym));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Synonym Number'{Synonym}'.", synonym.Synonym);
            _broker.Send(new CrudErrorMessage("Synonym", "Update", $"Could not update '{synonym.Synonym}'."));
            throw;

        }
    }

    public async Task DeleteSynonymAsync(int id, CancellationToken ct = default)
    {
        try
        {
            await _synonymService.DeleteSynonymAsync(id);

            _logger.LogInformation("Successfully executed Safe-Delete Synonym ID {SynonymID}.", id);
            _broker.Send(new EntityDeletedMessage("Synonym", $"ID: {id}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed while attempting to Safe-Delete  Synonym ID {SynonymID}.", id);
            _broker.Send(new CrudErrorMessage("Synonym", "Delete", "Failed to safely delete the Synonym. Ensure no protected transactions are attached."));
            throw;

        }
    }

    #endregion

}