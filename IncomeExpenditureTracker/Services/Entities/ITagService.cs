using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;

using IncomeExpenditureTracker.Models;
using System.Threading;

namespace IncomeExpenditureTracker.Services.Entities;

public interface ITagService
{
    // Stampede Defended Snapshot Retrieval
    Task<RuleBookSnapshot> GetRuleBookSnapshotAsync(CancellationToken ct = default);
    void InvalidateCache();

    // Atomic Tag CRUD
    Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

    Task<int> GetTagIdByName(string name, CancellationToken ct = default);
    Task<List<Tag>> GetAllTags(CancellationToken ct = default);
    Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null, CancellationToken ct = default);
    Task DeleteTagAsync(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

    /// <summary>
    /// Floats tags (sets SubCategoryId = NULL) for a specific SubCategory before it is deleted.
    /// </summary>
    Task FloatTagsBySubCategoryAsync(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

    /// <summary>
    /// Floats tags (sets SubCategoryId = NULL) for all SubCategories under a specific Category before it is deleted.
    /// </summary>
    Task FloatTagsByCategoryAsync(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

    // Atomic TagRule CRUD
    Task<int> AddRuleAsync(string keyword, int tagId, int priority = 10, CancellationToken ct = default);
    Task UpdateRuleAsync(int ruleId, string keyword, int tagId, int priority, CancellationToken ct = default);
    Task DeleteRuleAsync(int ruleId, CancellationToken ct = default);

    Task DeleteRulesByTagId(int tagId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
    Task DeleteRuleKeywordsAsync(IEnumerable<string> keywords, int tagId, CancellationToken ct = default);

    Task LearnRuleFromOverrideAsync(string rawDescription, int targetTagId, CancellationToken ct = default);
}