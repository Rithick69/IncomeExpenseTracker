using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Entities;

namespace IncomeExpenditureTracker.Services.Helpers;

// ------------------------------------------------------------
// HEADER DETECTOR (Sliding Window)
// ------------------------------------------------------------
// Detects the transaction header row by scanning the sheet
// using a sliding window.
//
// This allows detection even when headers are spread across
// multiple rows or when blank rows exist above the table.
// ------------------------------------------------------------
public class HeaderDetector : IHeaderDetector<IXLWorksheet>
{
    private Dictionary<string, string> _synonymFieldMap = null!;
    private readonly ISynonymService _synonymService = null!;
    private IReadOnlyDictionary<string, Synonyms> _synonyms = null!;
    private bool _isInitialized = false;


    public HeaderDetector(ISynonymService synonymService)
    {
        ArgumentNullException.ThrowIfNull(synonymService);
        _synonymService = synonymService;
    }


    // 2. The new Async Initialization method
    private async Task EnsureInitializedAsync(bool forceReload = false)
    {
        // If we already built the dictionaries, skip doing it again
        if (_isInitialized && !forceReload) return; // this forces a reload if needed, e.g., if synonyms were updated in the database

        // Fetch from the database safely
        _synonyms = await _synonymService.GetSynonymsByCategory("TRANSACTION"); // Default category for transaction headers

        // _synonyms is an IReadOnlyDictionary<string, Synonyms>, so ToDictionary receives KeyValuePair entries.
        // Use kv.Value to access the Synonyms object inside each KeyValuePair.
        // FIX: Normalize database keys and enforce case-insensitive lookups
        _synonymFieldMap = _synonyms.ToDictionary(
            kv => Normalize(kv.Value.Synonym),
            kv => kv.Value.FieldType,
            StringComparer.OrdinalIgnoreCase
        );

        _isInitialized = true;
    }

    // Weights for each field type to calculate the header score
    private readonly Dictionary<string, int> _weights = new()
    {
        { "DATE", 3 },
        { "DESCRIPTION", 3 },
        { "DEBIT", 2 },
        { "CREDIT", 2 },
        { "AMOUNT", 1 }
    };

    public async Task<int> DetectHeaderRow(IXLWorksheet worksheet, bool forceReload = false)
    {
        try
        {
            await EnsureInitializedAsync(forceReload);

            int bestRow = -1; // Zero-based index of the best header row found so far
            int bestScore = 0;

            int windowSize = 1; // Set windowSize to 1 so each row is evaluated on its own merit!

            int maxRows = Math.Min(20, worksheet.LastRowUsed()?.RowNumber() ?? 20);

            for (int startRow = 1; startRow <= maxRows; startRow++)
            {

                // =========================================================================
                // Don't start a window on a completely blank row!
                // This prevents empty rows above the table from stealing credit for headers below them.
                // =========================================================================
                if (worksheet.Row(startRow).IsEmpty())
                    continue;

                int score = 0;

                // Clamp the window so it doesn't read past the last row of a small test sheet
                int endWindow = Math.Min(maxRows, startRow + windowSize - 1);

                for (int r = startRow; r <= endWindow; r++)
                {
                    int lastColumn = worksheet.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 0;

                    for (int col = 1; col <= lastColumn; col++)
                    {
                        var text = Normalize(worksheet.Cell(r, col).GetString());

                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        // FIX #2: Check the FULL cell text first to support multi-word synonyms like "TXN DATE"
                        if (_synonymFieldMap.TryGetValue(text, out var fieldType))
                        {
                            score += _weights.TryGetValue(fieldType, out var weight) ? weight : 1;
                            continue; // Move to next cell once matched
                        }

                        // Split cell text into tokens and check each token against synonyms
                        // This allows us to detect headers even if they are combined (e.g. "Date Description Amount")
                        // or if they are separated by spaces (e.g. "Date Description" in one cell and "Amount" in another)
                        // We only need to find one synonym match per cell to count it towards the score
                        // This is a simple heuristic that gives more weight to cells that contain multiple synonyms,
                        // but also allows for partial matches

                        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                        foreach (var token in tokens)
                        {
                            if (_synonymFieldMap.TryGetValue(token, out var tokenField))
                            {
                                if (_weights.TryGetValue(tokenField, out var weight))
                                {
                                    score += weight;
                                }
                                else
                                {
                                    score += 1;
                                }

                                break;
                            }
                        }

                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestRow = startRow - 1; // Convert to zero-based index
                }
            }

            if (bestRow == -1)
                throw new InvalidOperationException("Failed to detect header row.");

            return bestRow;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HeaderDetector] Failed to detect header row: {ex.Message}");
            throw;
        }
    }

    private static string Normalize(string text)
    {
        return text
            .ToUpper()
            .Replace("_", " ")
            .Replace("-", " ")
            .Trim();
    }
}