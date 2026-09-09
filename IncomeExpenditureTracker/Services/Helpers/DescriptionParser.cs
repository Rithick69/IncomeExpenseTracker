using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace IncomeExpenditureTracker.Services.Helpers;

// ------------------------------------------------------------
// DESCRIPTION PARSER
// ------------------------------------------------------------
// Converts raw transaction descriptions into tokens
// used by the TagEngine.
//
// Responsibilities:
// 1. Normalize text
// 2. Remove numbers and stop words
// 3. Generate sliding window tokens
// ------------------------------------------------------------
public class DescriptionParser : IDescriptionParser
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "PAID",
        "SENT",
        "PAYMENT",
        "TRANSFER",
        "TXN",
        "REF",
        "DR",
        "CR"
    };

    // Maximum words combined into a token
    // Prevents token explosion for long descriptions
    private const int MAX_TOKEN_WINDOW = 4;

    // -------------------------------------------------------------------------
    // SANITIZATION REGEX FIREWALL (Compiled for high-performance loops)
    // -------------------------------------------------------------------------

    // 1. Catches standard dates (DD-MM-YYYY, DD/MM/YY, etc.) to remove variable billing cycles
    private static readonly Regex DateRegex = new Regex(@"\b\d{2}[-./]\d{2}[-./]\d{2,4}\b", RegexOptions.Compiled);

    // 2. Catches transaction IDs, account numbers, and reference numbers (5+ consecutive digits)
    private static readonly Regex NumericIdRegex = new Regex(@"\b\d{5,}\b", RegexOptions.Compiled);

    // 3. Strips non-alphanumeric characters, replacing them with a space
    private static readonly Regex SpecialCharsRegex = new Regex(@"[^a-zA-Z0-9\s]", RegexOptions.Compiled);

    // 4. Collapses multiple spaces into a single space
    private static readonly Regex MultipleSpacesRegex = new Regex(@"\s{2,}", RegexOptions.Compiled);


    private readonly ILogger<DescriptionParser> _logger;

    public DescriptionParser(ILogger<DescriptionParser> logger)
    {
        _logger = logger;
    }


    /// <summary>
    /// Strips banking noise, IDs, and special characters to generate a contiguous,
    /// exact-match key for O(1) Payee mapping.
    /// </summary>
    public string SanitizeMerchantString(string rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription))
            return string.Empty;

        string cleaned = rawDescription;

        // Pass 1: Remove Dates (e.g., "01-10-2024")
        cleaned = DateRegex.Replace(cleaned, " ");

        // Pass 2: Remove long numeric IDs (e.g., "924010035959984")
        cleaned = NumericIdRegex.Replace(cleaned, " ");

        // Pass 3: Strip special characters (colons, slashes, hyphens)
        cleaned = SpecialCharsRegex.Replace(cleaned, " ");

        // Pass 4: Collapse spaces, trim, and standardize to uppercase
        cleaned = MultipleSpacesRegex.Replace(cleaned, " ").Trim().ToUpperInvariant();

        // -------------------------------------------------------------------------
        // TIER 1 GUARDRAIL
        // -------------------------------------------------------------------------
        // If the description was literally nothing but an ID or a banking keyword
        // (leaving us with an empty string after scrubbing), we fall back to the
        // raw uppercase string. This guarantees we never map a blank string as a key.
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return rawDescription.Trim().ToUpperInvariant();
        }

        return cleaned;
    }


    public List<string> ExtractTokens(string description)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(description))
                return new List<string>();

            // Normalize description
            description = description.ToUpperInvariant();

            // Replace your manual .Replace() chain with this:
            // This removes all digits (0-9) and special symbols (#, *, etc.), converting them to spaces.
            description = Regex.Replace(description, @"[^a-zA-Z\s]", " ");

            // Collapse multiple spaces into a single space and trim the edges
            description = Regex.Replace(description, @"\s+", " ").Trim();

            var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Pre-allocate capacity to prevent internal array resizing
            var baseTokens = new List<string>(words.Length);

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                // ZERO-ALLOCATION DIGIT CHECK: Replaced Regex.IsMatch
                if (word.All(char.IsDigit))
                    continue;

                if (word.Length < 2)
                    continue;

                if (StopWords.Contains(word))
                    continue;

                baseTokens.Add(word);
            }

            //----------------------------------------------------
            // SLIDING WINDOW TOKEN GENERATION
            //----------------------------------------------------
            // Example:
            //
            // STATE BANK OF INDIA
            //
            // Generates:
            // STATE
            // BANK
            // OF
            // INDIA
            // STATEBANK
            // BANKOF
            // OFINDIA
            // STATEBANKOF
            // BANKOFINDIA
            // STATEBANKOFINDIA
            //----------------------------------------------------

            int n = baseTokens.Count;

            if (n == 0)
                return new List<string>(0);

            // Estimate total tokens to avoid HashSet resizing overhead
            int estimatedTokens = n * Math.Min(n, MAX_TOKEN_WINDOW);
            var tokens = new HashSet<string>(estimatedTokens, StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder(64);

            for (int start = 0; start < n; start++)
            {
                sb.Clear();

                for (int end = start; end < n && end < start + MAX_TOKEN_WINDOW; end++)
                {
                    if (end > start)
                    {
                        sb.Append(' '); // Space-joined keywords!
                    }
                    sb.Append(baseTokens[end]);

                    if (sb.Length > 40)
                        break;

                    tokens.Add(sb.ToString());
                }
            }

            return tokens
                .ToList();
        }
        catch (Exception ex)
        {
            // ----------------------------------------------------
            // FAILSAFE
            // ----------------------------------------------------
            // Parser errors should NEVER stop transaction import.
            // We log and return empty tokens so tagging
            // falls back to Misc.
            // ----------------------------------------------------
            _logger.LogError($"[DescriptionParser] Failed to parse description: {ex.Message}");

            return new List<string>(0); ;
        }
    }


}