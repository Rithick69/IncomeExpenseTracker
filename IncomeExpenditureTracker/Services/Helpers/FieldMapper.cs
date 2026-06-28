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
    private readonly SynonymService _synonymService = null!;
    private List<Synonyms> _synonyms = null!;

    // Exact synonym lookup
    private Dictionary<string, Synonyms> _exactMatchMap = null!;

    // Token index
    // Example:
    // "DATE" -> [ "TRANSACTION DATE", "VALUE DATE" ]
    private Dictionary<string, List<Synonyms>> _tokenIndex = null!;

    private bool _isInitialized = false;

    // 1. The constructor is strictly for injecting dependencies
    public FieldMapper(SynonymService synonymService)
    {
        ArgumentNullException.ThrowIfNull(synonymService);
        _synonymService = synonymService;
    }


    // 2. The new Async Initialization method
    private async Task EnsureInitializedAsync()
    {
        // If we already built the dictionaries, skip doing it again
        if (_isInitialized) return;

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
        _tokenIndex = BuildTokenIndex(_synonyms);

        _isInitialized = true;
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
    private Dictionary<string, List<Synonyms>> BuildTokenIndex(List<Synonyms> synonyms)
    {
        var index = new Dictionary<string, List<Synonyms>>();

        foreach (var synonym in synonyms)
        {
            var tokens = Normalize(synonym.Synonym).Split(' ');

            foreach (var token in tokens)
            {
                if (!index.ContainsKey(token))
                    index[token] = new List<Synonyms>();

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
    // TransColumnMap containing detected column indices
    // ------------------------------------------------------------
    public async Task<TransColumnMap> DetectColumns(IXLWorksheet worksheet, int headerRow)
    {
        try
        {
            await EnsureInitializedAsync();

            var map = new TransColumnMap
            {
                HeaderRow = headerRow
            };

            var lastColCell = worksheet.LastColumnUsed();
            int lastColumn = lastColCell?.ColumnNumber() ?? 0;

            for (int col = 1; col <= lastColumn; col++)
            {
                var headerText = Normalize(worksheet.Cell(headerRow, col).GetString());

                if (string.IsNullOrWhiteSpace(headerText))
                    continue;

                var match = MatchSynonym(headerText);

                if (match != null)
                    AssignTransColumn(map, match.FieldType, col);
            }

            ValidateTransColumnMap(map);

            return map;
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
    public async Task<Account> DetectAccountDetails(IXLWorksheet worksheet)
    {
        try
        {
            await EnsureInitializedAsync();

            var account = new Account
            {
                CreatedDate = DateTime.UtcNow
            };

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

                    AssignAccountField(account, match.FieldType, value);
                }
            }

            ValidateAccountDetails(account);

            return account;
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
    // ASSIGN TRANSACTION COLUMN
    // ------------------------------------------------------------
    private void AssignTransColumn(TransColumnMap map, string type, int column)
    {
        switch (type)
        {
            case "DATE":
                map.DateColumn = column;
                break;

            case "DESCRIPTION":
                map.DescriptionColumn = column;
                break;

            case "DEBIT":
                map.DebitColumn = column;
                break;

            case "CREDIT":
                map.CreditColumn = column;
                break;
            case "AMOUNT":
                map.AmountColumn = column;
                break;
        }
    }

    // ------------------------------------------------------------
    // ASSIGN ACCOUNT FIELD
    // ------------------------------------------------------------
    private void AssignAccountField(Account account, string fieldType, string value)
    {
        switch (fieldType)
        {
            case "ACCOUNT_NUMBER":
                account.AccountNumber = value;
                break;

            case "CARD_NUMBER":
                account.CardNumber = value;
                break;

            case "ENTITY_NAME":
                account.EntityName = value;
                break;

            case "ACCOUNT_TYPE":
                account.AccountType = value;
                break;

            case "CURRENCY":
                account.Currency = value;
                break;

            case "CREDIT_LIMIT":
                account.CreditLimit = value;
                break;
        }
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

    // ------------------------------------------------------------
    // VALIDATE REQUIRED TRANSACTION COLUMNS
    // ------------------------------------------------------------
    private void ValidateTransColumnMap(TransColumnMap map)
    {
        if (map.DateColumn == 0)
            throw new Exception("DATE column not detected.");

        if (map.DescriptionColumn == 0)
            throw new Exception("DESCRIPTION column not detected.");

        if (map.DebitColumn == 0 && map.CreditColumn == 0 && map.AmountColumn == 0)
            throw new Exception("No amount column detected.");
    }

    // ------------------------------------------------------------
    // VALIDATE REQUIRED ACCOUNT DETAILS
    // ------------------------------------------------------------
    // Ensures that all critical account fields were detected
    // during statement parsing. If any required field is missing,
    // the parser throws an exception to prevent invalid data
    // from entering the system.
    // ------------------------------------------------------------
    private void ValidateAccountDetails(Account account)
    {
        if (string.IsNullOrWhiteSpace(account.EntityName))
            throw new InvalidOperationException("Entity Name could not be detected.");

        if (string.IsNullOrWhiteSpace(account.AccountNumber) || account.AccountNumber.Length < 4)
            throw new InvalidOperationException("Account Number could not be detected.");

        if (string.IsNullOrWhiteSpace(account.CardNumber) || account.CardNumber.Length < 4)
            throw new InvalidOperationException("Card Number could not be detected.");

        if (string.IsNullOrWhiteSpace(account.AccountType))
            throw new InvalidOperationException("Account Type could not be detected.");

        if (string.IsNullOrWhiteSpace(account.Currency))
            throw new InvalidOperationException("Currency could not be detected.");

        if (string.IsNullOrWhiteSpace(account.CreditLimit))
            throw new InvalidOperationException("Credit Limit could not be detected.");
    }
}