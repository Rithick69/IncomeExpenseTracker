using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

    public readonly IStrictAccountParser _strictAccountParser;
    private readonly ILogger<ExcelTransactionExtractor> _logger;

    public ExcelTransactionExtractor(IStrictAccountParser strictAccountParser, ILogger<ExcelTransactionExtractor>? logger = null)
    {
        _strictAccountParser = strictAccountParser ?? throw new ArgumentNullException(nameof(strictAccountParser));
        _logger = logger ?? NullLogger<ExcelTransactionExtractor>.Instance;
    }

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
        public int TransactionTypeCol { get; init; }

        // Fast factory method to resolve dictionary once upfront
        public static TransactionColumnCoordinates FromDictionary(Dictionary<string, DetectedField> fields)
        {
            return new TransactionColumnCoordinates
            {
                DateCol = GetCol(fields, "Col:DATE"),
                DescCol = GetCol(fields, "Col:DESCRIPTION"),
                AmountCol = GetCol(fields, "Col:AMOUNT"),
                DebitCol = GetCol(fields, "Col:DEBIT"),
                CreditCol = GetCol(fields, "Col:CREDIT"),
                TransactionTypeCol = GetCol(fields, "Col:TRANSACTIONTYPE")
            };
        }

        private static int GetCol(Dictionary<string, DetectedField> fields, string key)
        {
            // 1. Fast Path: Try direct hash lookup (O(1))
            if (fields.TryGetValue(key, out var field))
                return field.ColumnIndex;

            // Safely strip the "Col:" prefix only if it appears at the start of the string
            var unprefixedKey = key.StartsWith("Col:", StringComparison.OrdinalIgnoreCase)
                ? key.Substring(4)
                : key;

            // Fallback safety: try matching without the prefix just in case legacy keys slipped in
            if (fields.TryGetValue(unprefixedKey, out var fallbackField))
                return fallbackField.ColumnIndex;

            // 2. Safety Fallback: Case-insensitive scan (O(N))
            // This catches mismatches like "col:date" vs "Col:DATE" or "Date" vs "DATE"
            // if the dictionary was not originally initialized with StringComparer.OrdinalIgnoreCase.
            foreach (var (dictKey, dictValue) in fields)
            {
                if (string.Equals(dictKey, key, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dictKey, unprefixedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return dictValue.ColumnIndex;
                }
            }

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
        public bool IsTerminal { get; set; } // Signals the loop to break

        // UI Metadata
        public ReviewFlags ReviewStatus { get; set; }
        public string RawAmountText { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
    }

    // Common keywords that indicate a balance or total row, which should be ignored during transaction extraction.
    private static readonly string[] TerminalKeywords =
    {
        "OPENING BALANCE", "CLOSING BALANCE", "BALANCE BROUGHT FORWARD",
        "BALANCE CARRIED FORWARD", "TOTAL", "SUBTOTAL", "BALANCE B/F",
        "BALANCE C/F", "GRAND TOTAL", "END OF STATEMENT", "STATEMENT SUMMARY"
    };

    // ------------------------------------------------------------
    // PREVIEW EXTRACTION
    // ------------------------------------------------------------
    // Extracts a small number of transactions to show
    // the user before performing the full import.
    //-------------------------------------------------------------
    public List<TransactionPreview> ExtractPreview(IXLWorksheet worksheet, int headerRow, Dictionary<string, DetectedField> columnFields, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();

            if (worksheet == null)
            {
                _logger.LogError("[ExtractPreview] Preview extraction rejected: worksheet was null.");
                throw new ArgumentNullException(nameof(worksheet));
            }

            if (columnFields == null)
            {
                _logger.LogError("[ExtractPreview] Preview extraction rejected: column field map was null for worksheet '{WorksheetName}'.", worksheet.Name);
                throw new ArgumentNullException(nameof(columnFields));
            }

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
                // Halt the loop if the user cancels
                ct.ThrowIfCancellationRequested();

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

                // HALT EXTRACTION: The footer has been reached
                if (parsedRow.IsTerminal)
                {
                    _logger.LogInformation("Terminal row encountered at row {Row}. Halting extraction.", row);
                    break;
                }

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
                    Amount = parsedRow.Credit > 0 ? parsedRow.Credit : -parsedRow.Debit,
                    TransactionType = parsedRow.TransactionType
                });
            }

            _logger.LogInformation(
                "[ExtractPreview] Preview extraction completed for worksheet '{WorksheetName}' with {TransactionCount} valid preview rows.",
                worksheet?.Name ?? "Unknown",
                results.Count);

            return results;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[ExtractPreview] Preview extraction was cancelled for worksheet '{WorksheetName}'.", worksheet?.Name ?? "Unknown");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ExtractPreview] Error extracting preview transactions for worksheet '{WorksheetName}' at header row {HeaderRow}.",
                worksheet?.Name ?? "Unknown",
                headerRow);
            return new List<TransactionPreview>();
        }
    }

    // ------------------------------------------------------------
    // FULL TRANSACTION EXTRACTION
    // ------------------------------------------------------------
    // Extracts all transactions from the worksheet.
    //-------------------------------------------------------------
    public List<Transaction> ExtractTransactions(IXLWorksheet worksheet, int headerRow, Dictionary<string, DetectedField> previewFields, CancellationToken ct = default)
    {
        if (worksheet == null)
        {
            _logger.LogError("[ExtractTransactions] Full extraction rejected: worksheet was null.");
            throw new ArgumentNullException(nameof(worksheet));
        }

        if (previewFields == null)
        {
            _logger.LogError("[ExtractTransactions] Full extraction rejected: preview field map was null for worksheet '{WorksheetName}'.", worksheet.Name);
            throw new ArgumentNullException(nameof(previewFields));
        }
        try
        {
            ct.ThrowIfCancellationRequested();

            // Resolve O(1) integers once before the loop
            var coords = TransactionColumnCoordinates.FromDictionary(previewFields);

            int startRow = headerRow + 2;
            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? startRow;

            var results = new List<Transaction>(lastRow - startRow + 1); // pre-size list for performance

            int invalidRowCount = 0;
            int maxInvalidRows = 10; // Stop extraction if we encounter too many invalid rows, which may indicate we've gone past the transaction section of the statement.

            for (int row = startRow; row <= lastRow; row++)
            {
                // Halt the loop if the user cancels
                ct.ThrowIfCancellationRequested();

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

                // HALT EXTRACTION: The footer has been reached
                if (parsedRow.IsTerminal)
                {
                    _logger.LogInformation("Terminal row encountered at row {Row}. Halting extraction.", row);
                    break;
                }

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
                    CreatedDate = DateTime.UtcNow,
                    RawAmountText = parsedRow.RawAmountText,
                    ReviewStatus = parsedRow.ReviewStatus
                };

                results.Add(transaction);
            }

            _logger.LogInformation(
                "[ExtractTransactions] Full extraction completed for worksheet '{WorksheetName}' with {TransactionCount} rows extracted.",
                worksheet?.Name ?? "Unknown",
                results.Count);

            return results;
        }
        catch (OperationCanceledException)
        {
            // CRITICAL: Catch and re-throw so the parent service can release memory
            _logger.LogWarning("[ExtractTransactions] Full extraction was cancelled for worksheet '{WorksheetName}'.", worksheet.Name);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ExtractTransactions] Error extracting transactions for worksheet '{WorksheetName}' at header row {HeaderRow}.",
                worksheet?.Name ?? "Unknown",
                headerRow);
            return new List<Transaction>();
        }
    }

    // Simple helper for safe UI/DB truncation
    public static string? TruncateForDb(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength
            ? value
            : value.Substring(0, maxLength - 3) + "...";

    // Checks if the description contains keywords that indicate this row is a balance or total row, which should be ignored.

    private bool IsTerminalRow(IXLRow row)
    {
        // Scan the first 5 populated cells. Footers rarely extend beyond this.
        for (int i = 1; i <= 5; i++)
        {
            var text = row.Cell(i).GetString().Trim().ToUpper();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (var keyword in TerminalKeywords)
            {
                if (text.Contains(keyword)) return true;
            }
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

            // FAST-FAIL 1: Is this the end of the statement?
            if (IsTerminalRow(sheetrow))
            {
                result.IsTerminal = true;
                return result;
            }

            // -------------------------------------------------------------
            // STEP 1: RESOLVE DATE & DESCRIPTION (Mandatory Fields)
            // We access the integer coordinates directly from the struct!
            // -------------------------------------------------------------
            if (coords.DateCol < 0 || coords.DescCol < 0)
            {
                return result; // IsValid defaults to false
            }

            var dateCell = sheetrow.Cell(coords.DateCol + 1);
            var descCell = sheetrow.Cell(coords.DescCol + 1);

            // FAST-FAIL: If both primary columns are completely empty, safely skip (it's a blank row or footer)
            if (dateCell.IsEmpty() && string.IsNullOrWhiteSpace(descCell.GetString()))
            {
                return result;
            }

            // -----------------------------
            //  STEP 2: DATE
            // -----------------------------
            bool hasValidDate = TryGetDate(dateCell, out var parsedDate);
            result.Date = parsedDate; // Assigns default(DateTime) if false

            // Stack the InvalidDate flag if parsing failed
            if (!hasValidDate)
            {
                result.ReviewStatus |= ReviewFlags.InvalidDate;
            }

            // -----------------------------
            //  STEP 3: DESCRIPTION
            // -----------------------------
            var rawDescription = descCell.GetString().Trim();

            // Route through the new sanitization helper
            var descCheck = SanitizeDescription(rawDescription);
            result.Description = descCheck.CleanedDescription;

            if (!descCheck.IsValid)
            {
                result.ReviewStatus |= ReviewFlags.InvalidDescription;
            }

            // -----------------------------
            // STEP 4: EXTRACT AMOUNTS (SLIDING WINDOW RECOVERY)
            // -----------------------------
            int maxGridOffset = 0;
            AccountParseResult? amountResult = null, debitResult = null, creditResult = null;

            if (coords.AmountCol >= 0)
            {
                // =========================================================================
                // SINGLE AMOUNT COLUMN (FLEXIBLE)
                // Safe to use sliding window because TransactionType string governs the math.
                // =========================================================================
                amountResult = RecoverAmountWithSlidingWindow(sheetrow, coords.AmountCol, out maxGridOffset);
            }
            else
            {
                // =========================================================================
                // DUAL COLUMNS (STRICT)
                // Position IS identity. Sliding here causes Debits to steal Credits.
                // We enforce strict $O(1) coordinates. If it fails, it flags for UI review.
                // =========================================================================
                if (coords.DebitCol >= 0)
                {
                    string dText = sheetrow.Cell(coords.DebitCol + 1).GetString();
                    debitResult = _strictAccountParser.Parse(dText);
                }

                if (coords.CreditCol >= 0)
                {
                    string cText = sheetrow.Cell(coords.CreditCol + 1).GetString();
                    creditResult = _strictAccountParser.Parse(cText);
                }
            }

            // -----------------------------
            //  STEP 5: EXTRACT TRANSACTION TYPE (SLIDING WINDOW RECOVERY)
            // -----------------------------
            if (coords.TransactionTypeCol >= 0)
            {
                // Pass the discovered grid offset so it doesn't parse garbage left behind by the description
                string rawType = RecoverTransactionTypeWithSlidingWindow(sheetrow, coords.TransactionTypeCol, maxGridOffset);

                if (!string.IsNullOrWhiteSpace(rawType))
                {
                    if (rawType.Contains('<') || rawType.Contains('>'))
                    {
                        rawType = rawType.Replace("<", "").Replace(">", "");
                    }
                    result.TransactionType = TruncateForDb(rawType, 50) ?? string.Empty;
                }
            }

            // -----------------------------
            // STEP 6: APPLY MATHEMATICAL ROUTING (Single vs Dual Column)
            // -----------------------------
            if (coords.AmountCol >= 0 && amountResult != null)
            {
                // =========================================================================
                // CASE 1: SINGLE AMOUNT COLUMN
                // =========================================================================
                result.RawAmountText = TruncateForDb(amountResult.RawText ?? string.Empty, 100) ?? string.Empty;
                decimal amount = amountResult.Value;

                if (!amountResult.IsClean || amount == 0m)
                {
                    result.ReviewStatus |= amountResult.ReviewStatus;
                    if (amount == 0m) result.ReviewStatus |= ReviewFlags.InvalidAmount;
                    result.Debit = 0m;
                    result.Credit = 0m;
                }
                else
                {
                    // Evaluate the explicit TransactionType indicator column if mapped
                    bool isExplicitDebit = false;
                    if (!string.IsNullOrWhiteSpace(result.TransactionType))
                    {
                        string typeText = result.TransactionType.ToUpper();
                        if (typeText == "DR" || typeText == "DEBIT" || typeText == "D") isExplicitDebit = true;
                    }

                    // Route based on explicit indicator OR mathematical sign
                    if (amount < 0 || isExplicitDebit)
                    {
                        result.Debit = Math.Abs(amount);
                        result.Credit = 0m;
                    }
                    else
                    {
                        result.Credit = Math.Abs(amount);
                        result.Debit = 0m;
                    }
                }
            }
            else
            {
                // =========================================================================
                // CASE 2: SEPARATE DEBIT AND CREDIT COLUMNS
                // =========================================================================
                bool debitFailed = debitResult != null && !debitResult.IsClean && !string.IsNullOrWhiteSpace(debitResult.RawText);
                bool creditFailed = creditResult != null && !creditResult.IsClean && !string.IsNullOrWhiteSpace(creditResult.RawText);

                decimal debitVal = debitResult != null ? Math.Abs(debitResult.Value) : 0m;
                decimal creditVal = creditResult != null ? Math.Abs(creditResult.Value) : 0m;

                if (debitFailed || creditFailed || (debitVal == 0m && creditVal == 0m) || (debitVal > 0 && creditVal > 0))
                {
                    result.ReviewStatus |= ReviewFlags.InvalidAmount;
                    result.RawAmountText = TruncateForDb($"Dr: [{debitResult?.RawText}] | Cr: [{creditResult?.RawText}]", 100) ?? string.Empty;
                    result.Debit = 0m;
                    result.Credit = 0m;

                    if (debitFailed) result.ReviewStatus |= debitResult!.ReviewStatus;
                    if (creditFailed) result.ReviewStatus |= creditResult!.ReviewStatus;
                }
                else
                {
                    result.Debit = debitVal;
                    result.Credit = creditVal;
                    result.TransactionType = debitVal > 0 ? "DR" : "CR";
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
            _logger.LogError(ex, "[ParseRow] Error parsing transaction row {RowNumber} during extraction.", sheetrow.RowNumber());
            return new ParsedTransactionRow();
        }
    }

    // ------------------------------------------------------------
    // SLIDING WINDOW RECOVERY HELPERS
    // ------------------------------------------------------------

    /// <summary>
    /// Attempts to parse a monetary value from the primary coordinate.
    /// If it fails (e.g., due to PDF column shifts pushing text into this cell),
    /// it slides up to 3 columns to the right to rescue the displaced numeric data.
    /// </summary>
    private AccountParseResult? RecoverAmountWithSlidingWindow(IXLRow sheetrow, int baseColIndex, out int appliedOffset)
    {
        appliedOffset = 0;
        if (baseColIndex < 0) return null;

        // 1. Try Primary Coordinate O(1) Fast-Path
        string primaryText = sheetrow.Cell(baseColIndex + 1).GetString();
        var primaryResult = _strictAccountParser.Parse(primaryText);

        if (primaryResult.IsClean && primaryResult.Value != 0)
        {
            return primaryResult;
        }

        // 2. Fallback: Sliding Window (+1 to +3 columns)
        for (int offset = 1; offset <= 3; offset++)
        {
            string candidateText = sheetrow.Cell(baseColIndex + 1 + offset).GetString().Trim();
            if (string.IsNullOrWhiteSpace(candidateText)) continue;

            var candidateResult = _strictAccountParser.Parse(candidateText);

            if (candidateResult.IsClean && candidateResult.Value != 0)
            {
                _logger.LogDebug("[SlidingWindow] Rescued shifted amount '{Amount}' at offset +{Offset}", candidateText, offset);
                appliedOffset = offset;
                return candidateResult;
            }
        }

        // 3. Complete Failure: Return the original failed result so the UI gets the error flags
        return primaryResult;
    }

    /// <summary>
    /// Searches for standard Debit/Credit indicators. Utilizes the offset discovered
    /// during amount extraction to avoid parsing garbage from split descriptions.
    /// </summary>
    private string RecoverTransactionTypeWithSlidingWindow(IXLRow sheetrow, int baseColIndex, int amountOffset)
    {
        if (baseColIndex < 0) return string.Empty;

        // Start from the amount offset (if the amount shifted +2, the indicator shifted AT LEAST +2)
        int startOffset = amountOffset > 0 ? amountOffset : 0;

        for (int offset = startOffset; offset <= 3; offset++)
        {
            string rawText = sheetrow.Cell(baseColIndex + 1 + offset).GetString().Trim().ToUpper();

            if (string.IsNullOrWhiteSpace(rawText)) continue;

            // 1. Exact Keyword Match
            if (rawText == "DR" || rawText == "DEBIT" || rawText == "D" ||
                rawText == "CR" || rawText == "CREDIT" || rawText == "C")
            {
                _logger.LogDebug("[SlidingWindow] Rescued TransactionType '{Type}' at offset +{Offset}", rawText, offset);
                return rawText;
            }

            // 2. Fused Cell Rescue (e.g., PDF converter merged "2558.00 DR" into one cell)
            if (rawText.EndsWith(" DR") || rawText.EndsWith(" DEBIT") || rawText.EndsWith(" D")) return "DR";
            if (rawText.EndsWith(" CR") || rawText.EndsWith(" CREDIT") || rawText.EndsWith(" C")) return "CR";
        }

        return string.Empty;
    }

    /// <summary>
    /// Sanitizes the raw description to prevent database overflow and basic UI injection,
    /// while determining if the row requires human review.
    /// </summary>
    private (string CleanedDescription, bool IsValid, string ErrorMessage) SanitizeDescription(string rawDescription)
    {
        if (string.IsNullOrWhiteSpace(rawDescription) || rawDescription.Length < 3)
        {
            string fallback = string.IsNullOrWhiteSpace(rawDescription) ? "[UNNAMED TRANSACTION]" : rawDescription;
            return (fallback, false, "Missing or excessively short description.");
        }

        bool isValid = true;
        string errorMessage = string.Empty;
        string cleaned = rawDescription;

        // Strip suspicious characters (Basic UI XSS Protection)
        if (cleaned.Contains('<') || cleaned.Contains('>'))
        {
            isValid = false;
            errorMessage = "Description contains suspicious characters (HTML/Scripts stripped).";
            cleaned = cleaned.Replace("<", "").Replace(">", "");
        }

        // Enforce Database Column Length to prevent SQL truncation exceptions
        cleaned = TruncateForDb(cleaned, 255) ?? "[UNNAMED TRANSACTION]";

        return (cleaned, isValid, errorMessage);
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

        // Explicitly support Indian/UK (dd-MM-yyyy) and other common bank formats
        // 1. Sanitize the string to remove troublesome apostrophes (e.g., "08 Feb '26" -> "08 Feb 26")
        string cleanText = text.Replace("'", "").Replace(",", "").Trim();

        // 2. Explicitly support formats using the sanitized string
        string[] customFormats = {
            "dd-MM-yyyy", "dd/MM/yyyy", "dd.MM.yyyy", "yyyy-MM-dd",
            "dd-MMM-yyyy", "dd-MMM-yy", "dd MMM yy", "d MMM yy", "dd MM yy"
        };

        if (DateTime.TryParseExact(cleanText, customFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        // Try parsing with invariant culture first, then fallback to current culture
        // This allows us to handle dates in formats like "MM/dd/yyyy" or "dd/MM/yyyy" depending on the user's locale,
        // while still supporting a standard format in the statements.

        if (DateTime.TryParse(cleanText, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        return DateTime.TryParse(cleanText, out date);
    }

    private bool IsValidRow(ParsedTransactionRow t)
    {
        if (t == null)
            return false;

        // =========================================================================
        // TIER 1 ERROR BYPASS
        // If we deliberately flagged it for review (due to bad date, bad description,
        // or ambiguous amounts), it IS a valid extraction target.
        // We MUST keep it so the user can see the error flags!
        // =========================================================================
        if (t.ReviewStatus != ReviewFlags.None)
            return true;

        // =========================================================================
        // STANDARD VALIDATION (For rows claiming to be clean)
        // =========================================================================
        if (t.Date == default)
            return false;

        if (string.IsNullOrWhiteSpace(t.Description))
            return false;

        // =========================================================================
        // REMOVED: if (t.Debit == 0 && t.Credit == 0) return false;
        // =========================================================================
        // We NO LONGER drop zero-value rows here.
        // Garbage text, contradictions, and literal zeroes are mapped to 0m
        // so they can be safely routed to the UI with error flags.
        // =========================================================================

        return true;
    }
}