using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Helpers;

public class StrictAccountParser : IStrictAccountParser
{
    private static readonly Regex DebitRegex = new Regex(@"(?<!\p{L})(DR|DB|D)(?!\p{L})", RegexOptions.Compiled);
    private static readonly Regex CreditRegex = new Regex(@"(?<!\p{L})(CR|C)(?!\p{L})", RegexOptions.Compiled);
    private static readonly Regex WhitelistedTokensRegex = new Regex(@"\b(USD|EUR|GBP|INR|CAD|AUD|JPY|DR|DB|CR)\b|[₹$€£¥]", RegexOptions.Compiled);
    private static readonly Regex IllegalCharacterRegex = new Regex(@"[^0-9.,\-() ]", RegexOptions.Compiled);

    public AccountParseResult Parse(string? rawText)
    {
        // 1. Guard against empty/null data from any file type
        if (string.IsNullOrWhiteSpace(rawText))
            return AccountParseResult.Failure(rawText ?? "", "Value is blank or whitespace.");

        string cleaned = rawText.Replace("\u00A0", " ").Trim();

        // 2. Universal File Guard: Catch spreadsheet formula errors (works for Excel and CSVs)
        if (cleaned.StartsWith("#") && (cleaned.Contains("!") || cleaned.Contains("N/A") || cleaned.Contains("NAME?")))
            return AccountParseResult.Failure(cleaned, $"Spreadsheet formula error detected: {cleaned}");

        // 3. String Fast-Path: Instantly parse clean numbers without Regex overhead
        const NumberStyles fastStyles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign;
        if (decimal.TryParse(cleaned, fastStyles, CultureInfo.InvariantCulture, out var fastDecimal) ||
            decimal.TryParse(cleaned, fastStyles, CultureInfo.CurrentCulture, out fastDecimal))
        {
            return AccountParseResult.Success(fastDecimal, cleaned);
        }

        // 4. Delegate to the Layered Regex Firewall for messy/formatted strings
        return ParseStrictString(cleaned);
    }

    public static AccountParseResult ParseStrictString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return AccountParseResult.Failure("", "Value is blank or whitespace.");

        string normalized = text.Trim().ToUpperInvariant();

        // ---------------------------------------------------------------------
        // LAYER 1: Contradiction & Ambiguity Guard
        // ---------------------------------------------------------------------
        bool isDebit = DebitRegex.IsMatch(normalized);
        bool isCredit = CreditRegex.IsMatch(normalized);

        if (isDebit && isCredit)
            return AccountParseResult.Failure(text, "Ambiguous: Contains both Debit (DR) and Credit (CR) markers.");

        // ---------------------------------------------------------------------
        // LAYER 2: Whitelist Token Removal
        // ---------------------------------------------------------------------
        string candidate = WhitelistedTokensRegex.Replace(normalized, "");

        if (candidate.EndsWith("D") || candidate.EndsWith("C"))
            candidate = candidate.Substring(0, candidate.Length - 1);
        if (candidate.StartsWith("D") || candidate.StartsWith("C"))
            candidate = candidate.Substring(1);

        candidate = candidate.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
            return AccountParseResult.Failure(text, "Contains only currency symbols or accounting markers with no numeric value.");

        // ---------------------------------------------------------------------
        // LAYER 3: Zero-Tolerance Character Guard
        // ---------------------------------------------------------------------
        if (IllegalCharacterRegex.IsMatch(candidate))
            return AccountParseResult.Failure(text, "Contains illegal characters (unrecognized letters, symbols, or code).");

        // ---------------------------------------------------------------------
        // LAYER 4: Structural Grammar Guard
        // ---------------------------------------------------------------------
        int hyphenCount = candidate.Count(c => c == '-');
        if (hyphenCount > 1)
            return AccountParseResult.Failure(text, "Invalid structure: Multiple hyphens detected (possible date or range).");

        if (hyphenCount == 1 && !candidate.StartsWith("-") && !candidate.EndsWith("-"))
            return AccountParseResult.Failure(text, "Invalid structure: Hyphen appears in the middle of the number.");

        if (candidate.Count(c => c == '.') > 1)
            return AccountParseResult.Failure(text, "Invalid structure: Multiple decimal points detected (possible IP or version number).");

        int openParen = candidate.Count(c => c == '(');
        int closeParen = candidate.Count(c => c == ')');
        if (openParen != closeParen || openParen > 1)
            return AccountParseResult.Failure(text, "Invalid structure: Unbalanced or nested parentheses.");

        if (openParen == 1 && (!candidate.StartsWith("(") || !candidate.EndsWith(")")))
            return AccountParseResult.Failure(text, "Invalid structure: Parentheses must wrap the entire outside of the number.");

        // ---------------------------------------------------------------------
        // LAYER 5: Clean Exact Extraction
        // ---------------------------------------------------------------------
        bool isNegative = candidate.Contains('-') || openParen == 1;
        string pureNumber = candidate.Trim('(', ')', '-', ' ');

        if (string.IsNullOrWhiteSpace(pureNumber))
            return AccountParseResult.Failure(text, "No digits remained after formatting cleanup.");

        const NumberStyles styles = NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;

        if (!decimal.TryParse(pureNumber, styles, CultureInfo.InvariantCulture, out var absoluteValue) &&
            !decimal.TryParse(pureNumber, styles, CultureInfo.CurrentCulture, out absoluteValue))
        {
            return AccountParseResult.Failure(text, "Number format is unrecognized or out of numeric bounds.");
        }

        // Apply Debit/Credit sign overrides if detected
        decimal finalValue = absoluteValue;
        if (isDebit)
            finalValue = -Math.Abs(absoluteValue);
        else if (isCredit)
            finalValue = Math.Abs(absoluteValue);
        else if (isNegative)
            finalValue = -absoluteValue;

        return AccountParseResult.Success(finalValue, text);
    }
}