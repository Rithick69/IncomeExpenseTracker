using System;
using System.Collections.Generic;
using System.Globalization;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.TransactionExtractor;

// ------------------------------------------------------------
// TRANSACTION EXTRACTOR
// ------------------------------------------------------------
// Converts Excel rows into Transaction objects.
//
// Responsibilities:
// • Extract transactions from worksheet
// • Support preview extraction
// • Handle column mapping
// • Perform safe data parsing
//
// Features:
// - Starts reading after the detected header row
// - Supports separate Debit / Credit columns
// - Supports single Amount column
// - Skips empty / invalid / noise rows
// - Normalizes common bank amount formats
//-------------------------------------------------------------
public class ExcelTransactionExtractor : ITransactionExtractor<IXLWorksheet>
{
    // ------------------------------------------------------------
    // INTERNAL PARSED ROW MODEL
    // ------------------------------------------------------------
    // Temporary container used while parsing rows.
    // Prevents duplication between preview extraction and full import.
    // ------------------------------------------------------------

    private static readonly string[] SummaryKeywords =
    {
        "OPENING BALANCE",
        "CLOSING BALANCE",
        "BALANCE B/F",
        "BALANCE C/F",
        "BALANCE BROUGHT FORWARD",
        "BALANCE CARRIED FORWARD",
        "TOTAL",
        "SUBTOTAL",
        "GRAND TOTAL"
    };

    // ------------------------------------------------------------
    // INTERNAL COORDINATE RESOLVER
    // ------------------------------------------------------------
    // Resolves column coordinates from the prefixed dictionary once upfront for performance.
    // ------------------------------------------------------------
    private readonly record struct TransactionColumnCoordinates
    {
        public int DateCol { get; init; }
        public int DescCol { get; init; }
        public int AmountCol { get; init; }
        public int DebitCol { get; init; }
        public int CreditCol { get; init; }

        // Fast factory method to resolve dictionary once upfront
        public static TransactionColumnCoordinates FromDictionary(Dictionary<string, DetectedField> fields)
        {
            return new TransactionColumnCoordinates
            {
                DateCol = GetCol(fields, "Col:DATE"),
                DescCol = GetCol(fields, "Col:DESCRIPTION"),
                AmountCol = GetCol(fields, "Col:AMOUNT"),
                DebitCol = GetCol(fields, "Col:DEBIT"),
                CreditCol = GetCol(fields, "Col:CREDIT")
            };
        }

        private static int GetCol(Dictionary<string, DetectedField> fields, string key)
        {
            // Try exact match first (e.g., "Col:Date")
            if (fields.TryGetValue(key, out var field))
                return field.ColumnIndex;

            // Fallback safety: try matching without the prefix just in case legacy keys slipped in
            var unprefixedKey = key.Replace("Col:", "", StringComparison.OrdinalIgnoreCase);
            if (fields.TryGetValue(unprefixedKey, out var fallbackField))
                return fallbackField.ColumnIndex;

            return -1;
        }
    }

    private class ParsedTransactionRow
    {
        public DateTime Date { get; set; }

        public string Description { get; set; } = string.Empty;

        public decimal Debit { get; set; }

        public decimal Credit { get; set; }

        public bool IsValid { get; set; }
    }

    // Common keywords that indicate a balance or total row, which should be ignored during transaction extraction.
    private static readonly string[] BalanceKeywords =
    {
        "OPENING BALANCE",
        "CLOSING BALANCE",
        "BALANCE BROUGHT FORWARD",
        "BALANCE CARRIED FORWARD",
        "TOTAL",
        "SUBTOTAL",
        "BALANCE B/F",
        "BALANCE C/F",
        "GRAND TOTAL"
    };

    // ------------------------------------------------------------
    // PREVIEW EXTRACTION
    // ------------------------------------------------------------
    // Extracts a small number of transactions to show
    // the user before performing the full import.
    //-------------------------------------------------------------
    public List<TransactionPreview> ExtractPreview(IXLWorksheet worksheet, int headerRow, Dictionary<string, DetectedField> columnFields)
    {
        try
        {
            // Resolve O(1) integers once before the loop
            var coords = TransactionColumnCoordinates.FromDictionary(columnFields);

            // BOUNDARY TRANSLATION (ROW):
            // headerRow is a 0-based domain index (0 = Excel Row 1).
            // Therefore, the first data row in 1-based ClosedXML coordinates is headerRow + 2.
            int startRow = headerRow + 2;

            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? startRow;

            int maxRow = Math.Min(lastRow, startRow + 19); // Extract up to 20 rows for preview

            // Pre-size list accurately based on 1-based row math
            var results = new List<TransactionPreview>(maxRow - startRow + 1); // pre-size list for performance

            int invalidRowCount = 0;
            int maxInvalidRows = 10; // Stop preview extraction if we encounter too many invalid rows, which may indicate we've gone past the transaction section of the statement.

            for (int row = startRow; row <= maxRow; row++)
            {
                var sheetRow = worksheet.Row(row);

                if (sheetRow.IsEmpty())
                {
                    invalidRowCount++;

                    if (invalidRowCount >= maxInvalidRows)
                    {
                        break;
                    }
                    continue;
                }

                var parsedRow = ParseRow(sheetRow, coords);

                if (!parsedRow.IsValid)
                {
                    invalidRowCount++;

                    if (invalidRowCount >= maxInvalidRows)
                    {
                        break;
                    }
                    continue;
                }

                results.Add(new TransactionPreview
                {
                    Date = parsedRow.Date,
                    Description = parsedRow.Description,
                    Debit = parsedRow.Debit,
                    Credit = parsedRow.Credit,
                    Amount = parsedRow.Credit > 0 ? parsedRow.Credit : -parsedRow.Debit
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting preview transactions: {ex.Message}");
            return new List<TransactionPreview>();
        }
    }

    // ------------------------------------------------------------
    // FULL TRANSACTION EXTRACTION
    // ------------------------------------------------------------
    // Extracts all transactions from the worksheet.
    //-------------------------------------------------------------
    public List<Transaction> ExtractTransactions(IXLWorksheet worksheet, int headerRow, Dictionary<string, DetectedField> previewFields)
    {
        try
        {
            // Resolve O(1) integers once before the loop
            var coords = TransactionColumnCoordinates.FromDictionary(previewFields);

            int startRow = headerRow + 2;
            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? startRow;

            var results = new List<Transaction>(lastRow - startRow + 1); // pre-size list for performance

            int invalidRowCount = 0;
            int maxInvalidRows = 10; // Stop extraction if we encounter too many invalid rows, which may indicate we've gone past the transaction section of the statement.

            for (int row = startRow; row <= lastRow; row++)
            {
                var sheetrow = worksheet.Row(row);

                if (sheetrow.IsEmpty())
                {
                    invalidRowCount++;

                    if (invalidRowCount >= maxInvalidRows)
                    {
                        break;
                    }
                    continue;
                }

                // Pass the unified dictionary directly to the row parser
                var parsedRow = ParseRow(sheetrow, coords);

                if (!parsedRow.IsValid)
                {
                    invalidRowCount++;

                    if (invalidRowCount >= maxInvalidRows)
                    {
                        break;
                    }
                    continue;
                }

                invalidRowCount = 0; // reset count after a valid row

                var transaction = new Transaction
                {
                    Date = parsedRow.Date,
                    Description = parsedRow.Description,
                    Debit = parsedRow.Debit,
                    Credit = parsedRow.Credit,
                    CreatedDate = DateTime.UtcNow
                };

                results.Add(transaction);
            }

            return results;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting transactions: {ex.Message}");
            return new List<Transaction>();
        }
    }

    // Checks if the description contains keywords that indicate this row is a balance or total row, which should be ignored.

    private bool IsBalanceRow(string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return false;

        var text = description.ToUpper();

        foreach (var keyword in BalanceKeywords)
        {
            if (text.Contains(keyword))
                return true;
        }

        return false;
    }
    // ------------------------------------------------------------
    // Parse ROW
    // ------------------------------------------------------------
    // Shared internal parser used by both preview and full import.
    //
    // Reads:
    // - Date
    // - Description
    // - Debit / Credit
    //
    // Supports:
    // 1. Separate Debit and Credit columns
    // 2. Single Amount column
    // ------------------------------------------------------------
    private ParsedTransactionRow ParseRow(IXLRow sheetrow, TransactionColumnCoordinates coords)
    {
        try
        {
            var result = new ParsedTransactionRow();

            // -------------------------------------------------------------
            // STEP 1: RESOLVE DATE & DESCRIPTION (Mandatory Fields)
            // We access the integer coordinates directly from the struct!
            // -------------------------------------------------------------
            if (coords.DateCol < 0 || coords.DescCol < 0)
            {
                return result; // IsValid defaults to false
            }

            // -----------------------------
            //  STEP 2: DATE
            // -----------------------------
            // Safely extract Date
            var dateCell = sheetrow.Cell(coords.DateCol + 1);
            if (!TryGetDate(dateCell, out var parsedDate))
            {
                return result; // Not a valid transaction row (e.g., subheader or footer)
            }
            result.Date = parsedDate;

            // -----------------------------
            //  STEP 3: DESCRIPTION
            // -----------------------------

            var description = sheetrow.Cell(coords.DescCol + 1).GetString().Trim();

            if (string.IsNullOrWhiteSpace(description))
                return result;

            if (description.Length < 3)
                return result;

            // Skip balance rows
            if (IsBalanceRow(description))
                return result;

            result.Description = description;

            // -----------------------------
            // STEP 4: PARSE AMOUNTS (Single vs. Dual Column Logic)
            // -----------------------------

            // -----------------------------
            // CASE 1: SINGLE AMOUNT COLUMN
            // -----------------------------
            if (coords.AmountCol >= 0)
            {
                decimal amount = GetDecimal(sheetrow.Cell(coords.AmountCol + 1));

                if (amount < 0)
                {
                    result.Debit = Math.Abs(amount);
                    result.Credit = 0m;
                }
                else if (amount > 0)
                {
                    result.Credit = amount;
                    result.Debit = 0m;
                }
            }
            else
            {
                // -----------------------------
                // CASE 2: SEPARATE DEBIT / CREDIT
                // -----------------------------
                decimal debitVal = 0m; // Initialize debit and credit values to zero
                decimal creditVal = 0m;

                // Zero-based column indices are used, so we add 1 when accessing ClosedXML cells.
                if (coords.DebitCol >= 0)
                    debitVal = Math.Abs(GetDecimal(sheetrow.Cell(coords.DebitCol + 1)));

                if (coords.CreditCol >= 0)
                    creditVal = Math.Abs(GetDecimal(sheetrow.Cell(coords.CreditCol + 1)));

                // Row is valid if at least one column contains money
                if (debitVal > 0 || creditVal > 0)
                {
                    result.Debit = debitVal;
                    result.Credit = creditVal;
                }
            }

            // -----------------------------
            // VALIDATION
            // -----------------------------
            result.IsValid = IsValidRow(result);

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing transaction row: {ex.Message}");
            return new ParsedTransactionRow();
        }
    }

    private bool TryGetDate(IXLCell cell, out DateTime date)
    {
        date = default;

        if (cell == null || cell.IsEmpty())
            return false;

        // ClosedXML can sometimes read dates as DateTime or as numbers (Excel date serials), so we handle both cases.
        if (cell.DataType == XLDataType.DateTime)
        {
            date = cell.GetDateTime();
            return true;
        }

        // If it's a number, it might be an Excel date serial number. We can attempt to convert it to a date.
        if (cell.DataType == XLDataType.Number)
        {
            try
            {
                date = cell.GetDateTime();
                return true;
            }
            catch
            {
                //ignore and try parsing as string
            }
        }

        var text = cell.GetString().Trim();

        if (string.IsNullOrWhiteSpace(text))
            return false;

        // Try parsing with invariant culture first, then fallback to current culture
        // This allows us to handle dates in formats like "MM/dd/yyyy" or "dd/MM/yyyy" depending on the user's locale,
        // while still supporting a standard format in the statements.
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        return DateTime.TryParse(text, out date);
    }

    private decimal GetDecimal(IXLCell cell)
    {
        if (cell == null || cell.IsEmpty())
            return 0m;

        // 1. Handle native Excel numeric cells immediately (O(1) fast path)
        if (cell.DataType == XLDataType.Number)
            return Convert.ToDecimal(cell.GetDouble());

        // 2. Extract text, replace invisible HTML non-breaking spaces (\u00A0), and normalize
        var text = cell.GetString().Replace("\u00A0", " ").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(text) || text == "-" || text == "--")
            return 0m;

        // 3. Detect accounting markers (DR, CR, DB, or standalone D/C)
        bool isDebit = text.Contains("DR") || text.Contains("DB") || text.EndsWith(" D") || text.StartsWith("D ");
        bool isCredit = text.Contains("CR") || text.EndsWith(" C") || text.StartsWith("C ");

        // 4. Strip currency symbols and text markers (Do NOT strip commas here!)
        text = text.Replace("₹", "")
                   .Replace("$", "")
                   .Replace("€", "")
                   .Replace("£", "")
                   .Replace("DR", "")
                   .Replace("DB", "")
                   .Replace("CR", "")
                   .Trim();

        // Remove lingering single-character D or C markers at the string boundaries
        if (text.EndsWith("D") || text.EndsWith("C"))
            text = text.Substring(0, text.Length - 1).Trim();
        if (text.StartsWith("D") || text.StartsWith("C"))
            text = text.Substring(1).Trim();

        // 5. Handle trailing minus signs (e.g., "500.00-" -> "-500.00")
        bool hasTrailingMinus = false;
        if (text.EndsWith("-"))
        {
            hasTrailingMinus = true;
            text = text.TrimEnd('-').Trim();
        }

        // 6. Handle accounting parentheses for negative numbers: "(500.00)" -> "-500.00"
        if (text.StartsWith("(") && text.EndsWith(")"))
        {
            text = "-" + text.Substring(1, text.Length - 2).Trim();
        }
        else if (hasTrailingMinus && !text.StartsWith("-"))
        {
            text = "-" + text;
        }

        // 7. Parse decimal with robust NumberStyles (Allows native thousands separators)
        const NumberStyles styles = NumberStyles.AllowDecimalPoint |
                                    NumberStyles.AllowThousands |
                                    NumberStyles.AllowLeadingSign |
                                    NumberStyles.AllowParentheses;

        if (!decimal.TryParse(text, styles, CultureInfo.InvariantCulture, out var value) &&
            !decimal.TryParse(text, styles, CultureInfo.CurrentCulture, out value))
        {
            return 0m;
        }

        // 8. Apply explicit debit/credit sign overrides ONLY if markers were detected
        if (isDebit)
            return -Math.Abs(value);

        if (isCredit)
            return Math.Abs(value);

        return value;
    }
    private bool IsValidRow(ParsedTransactionRow t)
    {
        if (t == null)
            return false;

        if (t.Date == default)
            return false;

        if (string.IsNullOrWhiteSpace(t.Description))
            return false;

        if (t.Debit == 0 && t.Credit == 0)
            return false;

        return true;
    }
}