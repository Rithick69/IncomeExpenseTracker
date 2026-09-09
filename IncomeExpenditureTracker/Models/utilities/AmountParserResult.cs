
namespace IncomeExpenditureTracker.Models
{
    public record AccountParseResult(
        decimal Value,
        ReviewFlags ReviewStatus,
        string RawText
    )
    {
        /// <summary>
        /// Gets whether this parsed result is clean and requires no user review.
        /// </summary>
        public bool IsClean => ReviewStatus == ReviewFlags.None;

        /// <summary>
        /// Creates a successful result with the validated decimal amount and clean flags.
        /// </summary>
        public static AccountParseResult Success(decimal value, string rawText) =>
            new(value, ReviewFlags.None, rawText);

        /// <summary>
        /// Creates a failed result defaulting to 0m, preserving raw text and setting the InvalidAmount flag.
        /// </summary>
        public static AccountParseResult Failure(string rawText, ReviewFlags flag = ReviewFlags.InvalidAmount) =>
            new(0m, flag, rawText);
    }
}