using System;
using System.Collections.Generic;
using System.Linq;
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

    public List<string> ExtractTokens(string description)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(description))
                return new List<string>();

            // Normalize description
            description = description.ToUpper();

            description = description.Replace("/", " ")
                                    .Replace("-", " ")
                                    .Replace(".", " ");

            var words = description.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var baseTokens = new List<string>();

            foreach (var word in words)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;

                // Remove pure numeric values
                if (Regex.IsMatch(word, @"^\d+$"))
                    continue;

                if (word.Length <= 2)
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

            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int n = baseTokens.Count;

            for (int start = 0; start < n; start++)
            {
                string combined = "";

                for (int end = start; end < n && end < start + MAX_TOKEN_WINDOW; end++)
                {
                    combined += baseTokens[end];

                    // Prevent extremely large tokens
                    if (combined.Length > 40)
                        break;

                    tokens.Add(combined);
                }
            }

            return tokens
                .OrderByDescending(t => t.Length)
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
            Console.WriteLine($"[DescriptionParser] Failed to parse description: {ex.Message}");

            return new List<string>(); ;
        }
    }
}