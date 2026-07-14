using System.Data;
using System.Threading.Tasks;
using System.Collections.Generic;

using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Tagging;

public interface ITagService
{
    // Stampede Defended Snapshot Retrieval
    Task<RuleBookSnapshot> GetRuleBookSnapshotAsync();
    void InvalidateCache();

    // Atomic Tag CRUD
    Task<int> GetOrCreateTagAsync(string name, int? subCategoryId = null, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task UpdateTagAsync(int tagId, string name, int? subCategoryId = null);
    Task DeleteTagAsync(int tagId);

    // Atomic TagRule CRUD
    Task<int> AddRuleAsync(string keyword, int tagId, int priority = 10);
    Task UpdateRuleAsync(int ruleId, string keyword, int tagId, int priority);
    Task DeleteRuleAsync(int ruleId);
    Task DeleteRuleKeywordsAsync(IEnumerable<string> keywords, int tagId);

    Task LearnRuleFromOverrideAsync(string rawDescription, int targetTagId);
}