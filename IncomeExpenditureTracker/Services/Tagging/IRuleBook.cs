using System.Threading.Tasks;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Tagging;

/// <summary>
/// Defines contracts for retrieving cached tagging rules and fallback classifications.
/// Registered as a Singleton to serve TagEngine in O(1) memory without database I/O.
/// </summary>
public interface IRuleBook
{
    /// <summary>
    /// Retrieves the immutable, case-insensitive rule index where keywords map to pre-sorted tag rules.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<TagRule>>> GetRuleIndexAsync();

    /// <summary>
    /// Retrieves the canonical database ID for the default fallback classification ('Misc').
    /// </summary>
    Task<int> GetMiscTagIdAsync();

    /// <summary>
    /// Evicts the cached RAM snapshot. Called when tag rules or categories are modified via UI management screens.
    /// </summary>
    void InvalidateCache();
}