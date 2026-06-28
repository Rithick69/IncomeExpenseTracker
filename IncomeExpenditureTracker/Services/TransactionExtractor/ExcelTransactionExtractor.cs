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

    private class ParsedTransactionRow
    {
        public DateTime Date { get; set; }

        public string Description { get; set; } = "";

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
    public List<TransactionPreview> ExtractPreview(IXLWorksheet worksheet, int headerRow, TransColumnMap columnMap)
    {
        try
        {

            int startRow = headerRow + 1;

            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? startRow;

            int maxRow = Math.Min(lastRow, startRow + 19); // Extract up to 20 rows for preview

            var results = new List<TransactionPreview>(maxRow - headerRow); // pre-size list for performance

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

                var parsedRow = ParseRow(sheetRow, columnMap);

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
    public List<Transaction> ExtractTransactions(IXLWorksheet worksheet, int headerRow, StatementPreview preview, int accountId)
    {
        try
        {

            int startRow = headerRow + 1;
            int lastRow = worksheet.LastRowUsed()?.RowNumber() ?? startRow;

            var results = new List<Transaction>(lastRow - headerRow);

            var map = new TransColumnMap
            {
                DateColumn = preview.DateField.ColumnIndex,
                DescriptionColumn = preview.DescriptionField.ColumnIndex,
                DebitColumn = preview.DebitField.ColumnIndex,
                CreditColumn = preview.CreditField.ColumnIndex,
                AmountColumn = preview.AmountField.ColumnIndex
            };

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

                var parsedRow = ParseRow(sheetrow, map);

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
                    AccountId = accountId,
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
    private ParsedTransactionRow ParseRow(IXLRow sheetrow, TransColumnMap map)
    {
        try
        {
            var result = new ParsedTransactionRow();

            // -----------------------------
            // DATE
            // -----------------------------
            if (map.DateColumn <= 0)
                return result;

            if (!TryGetDate(sheetrow.Cell(map.DateColumn), out var date))
                return result;

            result.Date = date;

            // -----------------------------
            // DESCRIPTION
            // -----------------------------
            if (map.DescriptionColumn <= 0)
                return result;

            var description = sheetrow.Cell(map.DescriptionColumn).GetString().Trim();

            if (string.IsNullOrWhiteSpace(description))
                return result;

            if (description.Length < 3)
                return result;

            // Skip balance rows
            if (IsBalanceRow(description))
                return result;

            result.Description = description;

            // -----------------------------
            // CASE 1: SINGLE AMOUNT COLUMN
            // -----------------------------
            if (map.AmountColumn > 0)
            {
                decimal amount = GetDecimal(sheetrow.Cell(map.AmountColumn));

                if (amount < 0)
                    result.Debit = Math.Abs(amount);
                else if (amount > 0)
                    result.Credit = amount;
            }
            else
            {
                // -----------------------------
                // CASE 2: SEPARATE DEBIT / CREDIT
                // -----------------------------
                if (map.DebitColumn > 0)
                    result.Debit = Math.Abs(GetDecimal(sheetrow.Cell(map.DebitColumn)));

                if (map.CreditColumn > 0)
                    result.Credit = Math.Abs(GetDecimal(sheetrow.Cell(map.CreditColumn)));
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
            return 0;

        // Convert (500.00) -> -500.00
        if (cell.DataType == XLDataType.Number)
            return Convert.ToDecimal(cell.GetDouble());

        var text = cell.GetString().Trim().ToUpper();

        // Handle DR / CR
        bool isDebit = text.Contains("DR");
        bool isCredit = text.Contains("CR");

        // Remove currency symbols
        text = text.Replace("₹", "")
                   .Replace("$", "")
                   .Replace(",", "")
                   .Replace("DR", "")
                   .Replace("CR", "")
                   .Trim();

        // Handle parentheses (negative values)
        if (text.StartsWith("(") && text.EndsWith(")"))
        {
            text = "-" + text.Substring(1, text.Length - 2);
        }

        // Try parsing with invariant culture first, then fallback to current culture
        // This allows us to handle amounts in formats like "1,000.00" or "1.000,00" depending on the user's locale,
        // while still supporting a standard format in the statements.

        if (!decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) &&
            !decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value))
        {
            return 0;
        }

        // If DR/CR markers exist adjust sign
        if (isDebit)
            value = -Math.Abs(value);

        if (isCredit)
            value = Math.Abs(value);

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