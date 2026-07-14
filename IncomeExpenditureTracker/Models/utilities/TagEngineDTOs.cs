using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

// STACK-ALLOCATED STRUCT: Zero heap allocation per rule!
public readonly record struct TagRuleDTO(string Keyword, int TagId, int Priority);

// IMMUTABLE RAM SNAPSHOT: Uses lightweight arrays instead of Lists
/// <summary>
/// Immutable state container holding the active tag rule index and fallback Misc tag ID.
/// Bundling these into a single record guarantees atomic state swapping across concurrent extraction threads.
/// </summary>
public record RuleBookSnapshot(
    IReadOnlyDictionary<string, TagRuleDTO[]> RuleIndex,
    int MiscTagId
);