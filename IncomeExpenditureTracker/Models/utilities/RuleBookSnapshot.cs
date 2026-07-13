using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

/// <summary>
/// Immutable state container holding the active tag rule index and fallback Misc tag ID[cite: 8].
/// Bundling these into a single record guarantees atomic state swapping across concurrent extraction threads.
/// </summary>
public sealed record RuleBookSnapshot(
    IReadOnlyDictionary<string, IReadOnlyList<TagRule>> RuleIndex,
    int MiscTagId
);