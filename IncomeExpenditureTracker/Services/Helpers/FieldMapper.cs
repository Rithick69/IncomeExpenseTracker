using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Helpers;

// ------------------------------------------------------------
// FIELD MAPPER
// ------------------------------------------------------------
// Responsible for detecting:
//
// 1️⃣ Transaction columns
//    DATE
//    DESCRIPTION
//    DEBIT
//    CREDIT
//
// 2️⃣ Account metadata
//    ACCOUNT_NUMBER
//    CARD_NUMBER
//    ENTITY_NAME
//    ACCOUNT_TYPE
//    CURRENCY
//    CREDIT_LIMIT
//
// Uses a synonym dictionary stored in the database.
//
// Matching strategy:
//
// 1️⃣ Exact match (fastest)
// 2️⃣ Token index match (fast)
// 3️⃣ Similarity match using Levenshtein (slow fallback)
//
// This layered strategy keeps the parser fast while
// still supporting messy bank formats.
// ------------------------------------------------------------

public class FieldMapper : IFieldMapper<IXLWorksheet>
{
    private IEnumerable<Synonyms> _synonyms = null!;

    // Exact synonym lookup
    private Dictionary<string, Synonyms> _exactMatchMap = null!;

    // Token index
    // Example:
    // "DATE" -> [ "TRANSACTION DATE", "VALUE DATE" ]
    private Dictionary<string, List<Synonyms>> _tokenIndex = null!;

    private readonly ISynonymService _synonymService = null!;

    private bool _isInitialized = false;

    // 1. The constructor is strictly for injecting dependencies
    public FieldMapper(ISynonymService synonymService)
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
        _synonyms = await _synonymService.GetAllSynonyms();

        // Build exact match dictionary
        _exactMatchMap = _synonyms
            .GroupBy(s => Normalize(s.Synonym))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.Priority).First()
            );

        // Build token index dictionary
        // Pass only the active, highest-priority synonyms to the token index
        _tokenIndex = BuildTokenIndex(_exactMatchMap.Values);

        _isInitialized = true;
    }

    private async Task<Dictionary<string, DetectedField>> PreseedDictionary(string category)
    {
        var dictionary = new Dictionary<string, DetectedField>(StringComparer.OrdinalIgnoreCase);

        var prefix = category == "TRANSACTION" ? "Col:" : "Meta:"; // e.g., "Col:" or "Meta:"

        var synonymMap = await _synonymService.GetSynonymsByCategory(category);

        var knownFieldTypes = synonymMap.Values
            .Select(s => s.FieldType)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // 2. PRE-SEEDING STEP: Populate default fallbacks for every expected field
        foreach (var fieldType in knownFieldTypes)
        {
            string namespacedKey = $"{prefix}{fieldType}"; // No trailing space!

            dictionary[namespacedKey] = new DetectedField
            {
                SuggestedField = fieldType,  // Pure domain concept, clean of prefixes
                ColumnName = "",             // Empty because it's not found yet
                ColumnIndex = -1,            // 0-based indexing flag for "Not Detected"
                ExtractedValue = "",
                ConfidenceScore = 0.0,
                IsUserVerified = false
            };
        }
        return dictionary;
    }

    // ------------------------------------------------------------
    // BUILD TOKEN INDEX
    // ------------------------------------------------------------
    // Creates a dictionary where each token maps to synonyms
    //
    // Example:
    // "DATE" -> ["TRANSACTION DATE", "VALUE DATE"]
    //
    // This allows us to quickly narrow down potential matches
    // without scanning every synonym.
    // ------------------------------------------------------------
    private Dictionary<string, List<Synonyms>> BuildTokenIndex(IEnumerable<Synonyms> activeSynonyms)
    {
        var index = new Dictionary<string, List<Synonyms>>();

        // Now we are only looping through the "winners" of the priority resolution
        foreach (var synonym in activeSynonyms)
        {
            var tokens = Normalize(synonym.Synonym).Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var token in tokens)
            {
                if (!index.ContainsKey(token))
                {
                    index[token] = new List<Synonyms>();
                }

                index[token].Add(synonym);
            }
        }

        return index;
    }

    // ------------------------------------------------------------
    // DETECT TRANSACTION COLUMNS
    // ------------------------------------------------------------
    // Scans the header row and maps columns to transaction fields.
    // Parameters:
    // worksheet → Excel worksheet
    // headerRow → row number containing column headers
    //
    // Returns:
    // detectedColumns containing detected column indices
    // ------------------------------------------------------------
    public async Task<Dictionary<string, DetectedField>> DetectColumns(IXLWorksheet worksheet, int headerRow, bool forceReload = false)
    {
        try
        {
            await EnsureInitializedAsync(forceReload);
            // Using case-insensitive keys so "DATE", "Date", and "date" are handled uniformly
            var detectedColumns = await PreseedDictionary("TRANSACTION"); // Pre-seed with "Col:" prefix for transaction columns

            var lastColCell = worksheet.LastColumnUsed();
            int lastColumn = lastColCell?.ColumnNumber() ?? 0;

            // BOUNDARY TRANSLATION (ROW):
            // Convert our incoming 0-based domain row coordinate back to ClosedXML's 1-based Excel coordinate.
            int excelHeaderRow = headerRow + 1;

            for (int col = 1; col <= lastColumn; col++)
            {
                var headerText = Normalize(worksheet.Cell(excelHeaderRow, col).GetString());

                if (string.IsNullOrWhiteSpace(headerText))
                    continue;

                var match = MatchSynonym(headerText);

                if (match != null)
                {

                    // BOUNDARY TRANSLATION:
                    // Convert ClosedXML's 1-based Excel coordinate to our global 0-based domain coordinate by subtracting 1.
                    int zeroBasedIndex = col - 1;

                    // DYNAMIC POPULATION: No switch statement needed!
                    // We map the domain concept (e.g., "DATE") directly to our universal container.
                    detectedColumns[$"Col:{match.FieldType}"] = new DetectedField
                    {
                        SuggestedField = match.FieldType, // The standard name (e.g., "DATE", "DESCRIPTION")
                        ColumnName = headerText,          // The raw Excel text (e.g., "TXN_DT") -> VITAL FOR LEARNING!
                        ColumnIndex = zeroBasedIndex,     // The integer coordinate for import engine math
                        ConfidenceScore = 0.95,           // High confidence since it matched our synonym engine
                        IsUserVerified = false
                    };
                }
            }

            // ValidateTransColumnMap(map);

            return detectedColumns;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FieldMapper] Column detection failed: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // DETECT ACCOUNT DETAILS
    // ------------------------------------------------------------
    // Scans the first 20 rows for account metadata.
    //
    // Example bank format:
    //
    // Account Number : 12345678
    // Currency       : INR
    // Card Number    : 4321
    //
    // Usually the value is stored in the next column.
    // ------------------------------------------------------------
    public async Task<Dictionary<string, DetectedField>> DetectAccountDetails(IXLWorksheet worksheet, bool forceReload = false)
    {
        try
        {
            await EnsureInitializedAsync(forceReload);

            var detectedMetadata = await PreseedDictionary("METADATA"); // Pre-seed with "Meta:" prefix for account metadata

            var lastColCell = worksheet.LastColumnUsed();
            int lastColumn = lastColCell?.ColumnNumber() ?? 0;

            for (int row = 1; row <= 20; row++)
            {
                for (int col = 1; col <= lastColumn; col++)
                {
                    var text = Normalize(worksheet.Cell(row, col).GetString());

                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var match = MatchSynonym(text);

                    if (match == null)
                        continue;

                    var value = ExtractFieldValue(worksheet, row, col);

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        // BOUNDARY TRANSLATION:
                        // Convert ClosedXML's 1-based Excel coordinate to our global 0-based domain coordinate by subtracting 1.
                        int zeroBasedIndex = col - 1;

                        // DYNAMIC POPULATION: No switch statement needed!
                        // Works identically for Account Number, Card Number, Currency, OR Bank Name (Entity)!
                        detectedMetadata[$"Meta:{match.FieldType}"] = new DetectedField
                        {
                            SuggestedField = match.FieldType, // e.g., "ACCOUNT_NUMBER", "ENTITY_NAME"
                            ColumnName = text,                // The raw label in Excel (e.g., "A/C NO:") -> VITAL FOR LEARNING!
                            ColumnIndex = zeroBasedIndex,     // The integer coordinate for import engine math
                            ExtractedValue = value,           // The actual data string (e.g., "987654321", "HDFC Bank")
                            ConfidenceScore = 0.90,
                            IsUserVerified = false
                        };
                    }
                }
            }

            // RELAXED VALIDATION: Commented out so missing Account/Card numbers
            // can be manually entered by the user during the StatementEditSession!
            // ValidateAccountDetails(account);

            return detectedMetadata;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FieldMapper] Account detail detection failed: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // EXTRACT FIELD VALUE
    // ------------------------------------------------------------
    // Attempts to extract the value associated with a field label.
    //
    // Supported patterns:
    //
    // 1️⃣ Account Number : 12345678
    // 2️⃣ Account Number | 12345678
    // 3️⃣ Account Number
    //     12345678
    // ------------------------------------------------------------
    private string ExtractFieldValue(IXLWorksheet worksheet, int row, int col)
    {
        var text = worksheet.Cell(row, col).GetString();

        if (string.IsNullOrWhiteSpace(text))
            return "";

        // Pattern 1: same cell with separator
        var separators = new[] { ":", "-", "=", "|" };

        foreach (var sep in separators)
        {
            if (text.Contains(sep))
            {
                var parts = text.Split(sep);

                if (parts.Length > 1)
                    return parts[1].Trim();
            }
        }

        // Pattern 2: value in next column
        var rightCell = worksheet.Cell(row, col + 1).GetString();
        if (!string.IsNullOrWhiteSpace(rightCell))
            return rightCell.Trim();

        // Pattern 3: value in next row
        var bottomCell = worksheet.Cell(row + 1, col).GetString();
        if (!string.IsNullOrWhiteSpace(bottomCell))
            return bottomCell.Trim();

        return "";
    }

    // ------------------------------------------------------------
    // SYNONYM MATCHING ENGINE
    // ------------------------------------------------------------
    // Matching pipeline:
    //
    // 1️⃣ Exact match dictionary lookup
    // 2️⃣ Token index candidate search
    // 3️⃣ Similarity scoring
    // ------------------------------------------------------------
    private Synonyms? MatchSynonym(string text)
    {
        // FASTEST — exact match
        if (_exactMatchMap.TryGetValue(text, out var exact))
            return exact;

        var tokens = text.Split(' ');

        var candidates = new HashSet<Synonyms>();

        // Gather candidates using token index
        foreach (var token in tokens)
        {
            if (_tokenIndex.TryGetValue(token, out var list))
            {
                foreach (var item in list)
                    candidates.Add(item);
            }
        }

        Synonyms? bestMatch = null;
        double bestScore = 0;

        foreach (var synonym in candidates)
        {
            var synonymText = Normalize(synonym.Synonym);

            double score = 0;

            if (text.Contains(synonymText))
                score = 0.9;
            else
                score = Similarity(text, synonymText);

            if (score < 0.7)
                continue;

            // Choose the match with the highest score, breaking ties with priority
            // This ensures that if multiple synonyms are similar, we prefer the one with higher priority

            if (score > bestScore ||
               (Math.Abs(score - bestScore) < 0.01 && synonym.Priority > (bestMatch?.Priority ?? 0)))
            {
                bestScore = score;
                bestMatch = synonym;
            }
        }

        return bestMatch;
    }

    // ------------------------------------------------------------
    // SIMILARITY CALCULATION
    // ------------------------------------------------------------
    // similarity calculation summary:
    // - Uses Levenshtein distance to measure how closely the header text matches known synonyms
    // - Converts distance to a similarity score between 0 and 1
    // - Higher score means a closer match
    // - This method is used to rank potential matches when multiple synonyms are found in the header text
    // ------------------------------------------------------------

    private double Similarity(string a, string b)
    {
        int distance = LevenshteinDistance(a, b);

        int maxLength = Math.Max(a.Length, b.Length);

        if (maxLength == 0)
            return 1.0;

        return 1.0 - ((double)distance / maxLength);
    }

    // ------------------------------------------------------------
    // LEVENSHTEIN DISTANCE
    // ------------------------------------------------------------
    // Levenshtein distance implementation summary:
    // - Creates a matrix to store distances between substrings of a and b
    // - Initializes the first row and column based on substring lengths
    // - Fills the matrix based on character matches and previous distances
    // - The final distance is found in the bottom-right cell of the matrix
    // - A distance of 0 means the strings are identical, while higher values indicate more differences
    // - This method is used to calculate how many edits (insertions, deletions, substitutions) are needed to transform one string into another
    // ------------------------------------------------------------

    private int LevenshteinDistance(string a, string b)
    {
        int[,] matrix = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++)
            matrix[i, 0] = i;

        for (int j = 0; j <= b.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost
                );
            }
        }

        return matrix[a.Length, b.Length];
    }

    // ------------------------------------------------------------
    // NORMALIZE TEXT
    // ------------------------------------------------------------
    private static string Normalize(string text)
    {
        return text
            .ToUpper()
            .Replace("_", " ")
            .Replace("-", " ")
            .Trim();
    }
}