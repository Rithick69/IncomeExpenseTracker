using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Logging;

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
public class DescriptionParser
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

    private readonly ILogger<DescriptionParser> _logger;

    public DescriptionParser(ILogger<DescriptionParser> logger)
    {
        _logger = logger;
    }

    public List<string> ExtractTokens(string description)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(description))
                return new List<string>();

            // Normalize description
            description = description.ToUpperInvariant();

            description = description.Replace("/", " ")
                                    .Replace("-", " ")
                                    .Replace(".", " ");

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