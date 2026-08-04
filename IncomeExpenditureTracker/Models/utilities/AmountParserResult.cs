namespace IncomeExpenditureTracker.Models;

public record AccountParseResult(
    decimal Value,
    bool NeedsReview,
    string RawText,
    string? ErrorReason = null
)
{
    /// <summary>
    /// Creates a successful result with the validated decimal amount.
    /// </summary>
    public static AccountParseResult Success(decimal value, string rawText) =>
        new(value, true, rawText, null);

    /// <summary>
    /// Creates a failed result defaulting to 0m, preserving the raw text and failure reason for UI highlighting.
    /// </summary>
    public static AccountParseResult Failure(string rawText, string reason) =>
        new(0m, false, rawText, reason);
}