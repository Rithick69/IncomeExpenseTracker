using System;
using System.IO;
using ClosedXML.Excel;

// This utility uses ClosedXML to dynamically generate .xlsx bank statements in your OS temporary directory.
// It lets us test international decimals, accounting formatting, dual-column vs. single-column layouts, and intentional row corruption on demand.

namespace IncomeExpenditureTracker.Tests.Fixtures
{
    public static class ExcelStatementGenerator
    {
        public static string GenerateValidStatement(int rowCount = 10, bool useDualColumns = false)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"statement_{Guid.NewGuid():N}.xlsx");
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Statement");

            // Write Headers
            worksheet.Cell(1, 1).Value = "Date";
            worksheet.Cell(1, 2).Value = "Description";

            if (useDualColumns)
            {
                worksheet.Cell(1, 3).Value = "Debit";
                worksheet.Cell(1, 4).Value = "Credit";
            }
            else
            {
                worksheet.Cell(1, 3).Value = "Amount";
            }

            // Write Data Rows
            for (int i = 1; i <= rowCount; i++)
            {
                int rowIdx = i + 1;
                worksheet.Cell(rowIdx, 1).Value = DateTime.UtcNow.AddDays(-i).ToString("yyyy-MM-dd");
                worksheet.Cell(rowIdx, 2).Value = $"POS PURCHASE MERCHANT {i:D3}";

                if (useDualColumns)
                {
                    // Alternate between debits and credits
                    if (i % 2 == 0)
                    {
                        worksheet.Cell(rowIdx, 3).Value = (10.50m * i).ToString("F2");
                        worksheet.Cell(rowIdx, 4).Value = "";
                    }
                    else
                    {
                        worksheet.Cell(rowIdx, 3).Value = "";
                        worksheet.Cell(rowIdx, 4).Value = (100.00m * i).ToString("F2");
                    }
                }
                else
                {
                    // Single amount column with mixed positive/negative numbers
                    decimal amount = (i % 2 == 0) ? -(15.25m * i) : (25.50m * i);
                    worksheet.Cell(rowIdx, 3).Value = amount.ToString("F2");
                }
            }

            workbook.SaveAs(filePath);
            return filePath;
        }

        public static string GenerateEdgeCaseFormattingStatement()
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"statement_edge_{Guid.NewGuid():N}.xlsx");
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Statement");

            worksheet.Cell(1, 1).Value = "Date";
            worksheet.Cell(1, 2).Value = "Description";
            worksheet.Cell(1, 3).Value = "Amount";

            // Row 2: Standard positive
            worksheet.Cell(2, 1).Value = "2026-07-01";
            worksheet.Cell(2, 2).Value = "STANDARD PURCHASE";
            worksheet.Cell(2, 3).Value = "150.00";

            // Row 3: Accounting parentheses for negative ((500.00))
            worksheet.Cell(3, 1).Value = "2026-07-02";
            worksheet.Cell(3, 2).Value = "ACCOUNTING PARENTHESES";
            worksheet.Cell(3, 3).Value = "(500.00)";

            // Row 4: Trailing minus sign (250.50-)
            worksheet.Cell(4, 1).Value = "2026-07-03";
            worksheet.Cell(4, 2).Value = "TRAILING MINUS";
            worksheet.Cell(4, 3).Value = "250.50-";

            // Row 5: International thousands/decimal separator (1.250,00)
            worksheet.Cell(5, 1).Value = "2026-07-04";
            worksheet.Cell(5, 2).Value = "INTERNATIONAL FORMAT";
            worksheet.Cell(5, 3).Value = "1.250,00";

            workbook.SaveAs(filePath);
            return filePath;
        }

        public static string GenerateCorruptedStatement(int totalRows = 15, int corruptedRowIndex = 8)
        {
            var filePath = Path.Combine(Path.GetTempPath(), $"statement_corrupt_{Guid.NewGuid():N}.xlsx");
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Statement");

            worksheet.Cell(1, 1).Value = "Date";
            worksheet.Cell(1, 2).Value = "Description";
            worksheet.Cell(1, 3).Value = "Amount";

            for (int i = 1; i <= totalRows; i++)
            {
                int rowIdx = i + 1;
                if (rowIdx == corruptedRowIndex)
                {
                    // Inject completely unparseable garbage into the date and amount columns
                    worksheet.Cell(rowIdx, 1).Value = "INVALID_DATE_STRING";
                    worksheet.Cell(rowIdx, 2).Value = "CORRUPTED ROW DATA";
                    worksheet.Cell(rowIdx, 3).Value = "NOT_A_NUMBER";
                }
                else
                {
                    worksheet.Cell(rowIdx, 1).Value = "2026-07-10";
                    worksheet.Cell(rowIdx, 2).Value = $"VALID TRANSACTION {i}";
                    worksheet.Cell(rowIdx, 3).Value = "45.00";
                }
            }

            workbook.SaveAs(filePath);
            return filePath;
        }
    }
}