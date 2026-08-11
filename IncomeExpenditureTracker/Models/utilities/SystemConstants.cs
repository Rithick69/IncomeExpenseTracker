namespace IncomeExpenditureTracker.Models;

/// <summary>
/// Centralized constants to prevent magic strings and ensure exact matching
/// between database initialization, UI, and backend services.
/// </summary>

public static class SystemConstants
{
    // The only fallbacks needed to protect transaction history

    public const string MiscTag = "Misc";
}