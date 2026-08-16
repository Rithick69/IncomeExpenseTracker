using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;

using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Entities;

public interface ITagService
{
    // Stampede Defended Snapshot Retrieval
    Task<RuleBookSnapshot> GetRuleBookSnapshotAsync();
    void InvalidateCache();

    // Atomic Tag CRUD
    Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, IDbConnection? conn = null, IDbTransaction? tx = null);

    Task<int> GetTagIdByName(string name);
    Task<List<Tag>> GetAllTags();
    Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null);
    Task DeleteTagAsync(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null);

    /// <summary>
    /// Floats tags (sets SubCategoryId = NULL) for a specific SubCategory before it is deleted.
    /// </summary>
    Task FloatTagsBySubCategoryAsync(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null);

    /// <summary>
    /// Floats tags (sets SubCategoryId = NULL) for all SubCategories under a specific Category before it is deleted.
    /// </summary>
    Task FloatTagsByCategoryAsync(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null);

    // Atomic TagRule CRUD
    Task<int> AddRuleAsync(string keyword, int tagId, int priority = 10);
    Task UpdateRuleAsync(int ruleId, string keyword, int tagId, int priority);
    Task DeleteRuleAsync(int ruleId);

    Task DeleteRulesByTagId(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteRuleKeywordsAsync(IEnumerable<string> keywords, int tagId);

    Task LearnRuleFromOverrideAsync(string rawDescription, int targetTagId);
}