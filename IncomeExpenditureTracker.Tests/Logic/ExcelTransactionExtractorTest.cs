using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Tests.Logic
{
    /// <summary>
    /// Tests the ExcelTransactionExtractor service, ensuring that preview generation is lightweight
    /// and full transaction extraction rigorously enforces accounting rules and error flags.
    /// </summary>
    public class ExcelTransactionExtractorTests
    {
        private readonly Mock<IStrictAccountParser> _parserMock;
        private readonly ITransactionExtractor<IXLWorksheet> _extractor;

        public ExcelTransactionExtractorTests()
        {
            // 1. We mock the interface so we can dictate exactly what the parser returns,
            // allowing us to test the EXTRACTOR's mapping logic, not the REGEX logic.
            _parserMock = new Mock<IStrictAccountParser>();

            // 2. Inject the mock into the service.
            // (Add any other mocked dependencies like ILogger here if needed).
            _extractor = new ExcelTransactionExtractor(_parserMock.Object);
        }

        // =========================================================================
        // SECTION 1: EXTRACT TRANSACTIONS (Strict Database Mapping)
        // =========================================================================

        [Fact]
        public void ExtractTransactions_WithCleanNegativeAmount_MapsToDebitAndLeavesReviewFalse()
        {
            // ARRANGE: Setup an in-memory worksheet with a clean withdrawal
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "CLEAN WITHDRAWAL";
            worksheet.Cell(2, 3).Value = "-150.00";

            var columns = CreateSingleColumnMappings();

            // Mock the parser: Tell it to simulate a successful pure number extraction
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("-150.00"))))
                       .Returns(AccountParseResult.Success(-150.00m, "-150.00"));

            // ACT: Call the full extraction method
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT: Validate the exact entity properties destined for SQLite
            Assert.Single(results);
            var transaction = results.First();

            Assert.Equal(150.00m, transaction.Debit);  // Negative goes to debit as positive absolute
            Assert.Equal(0m, transaction.Credit);

            // Happy path means no review flags
            Assert.False(transaction.NeedsReview);
            Assert.Null(transaction.ParseErrorMessage);
        }

        [Fact]
        public void ExtractTransactions_Condition1_GarbageText_DumpsToZeroAndFlagsForReview()
        {
            // ARRANGE: An OCR error or bad CSV export dumps text in the amount column
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "BROKEN ROW";
            worksheet.Cell(2, 3).Value = "INV-8675309";

            var columns = CreateSingleColumnMappings();

            // Mock the parser: Simulate the regex firewall rejecting this garbage
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("INV-8675309"))))
                       .Returns(AccountParseResult.Failure("INV-8675309", "Contains illegal characters."));

            // ACT
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT: Ensure math is protected (0m) and UI audit flags are tripped
            var transaction = results.First();
            Assert.Equal(0m, transaction.Debit);
            Assert.Equal(0m, transaction.Credit);
            Assert.True(transaction.NeedsReview);
            Assert.Equal("Contains illegal characters.", transaction.ParseErrorMessage);
            Assert.Equal("INV-8675309", transaction.RawAmountText);
        }

        [Fact]
        public void ExtractTransactions_Condition2_LiteralZero_DumpsToZeroAndFlagsForReview()
        {
            // ARRANGE: A legitimate 0.00 entry (e.g., balance check or fee waiver)
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "FEE WAIVER";
            worksheet.Cell(2, 3).Value = "0.00";

            var columns = CreateSingleColumnMappings();

            // Mock the parser: Simulate a SUCCESSFUL parse of a literal zero
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("0.00"))))
                       .Returns(AccountParseResult.Success(0m, "0.00"));

            // ACT
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT: Even though it was valid math, the business rule demands a human verifies it
            var transaction = results.First();
            Assert.Equal(0m, transaction.Debit);
            Assert.Equal(0m, transaction.Credit);
            Assert.True(transaction.NeedsReview);
            Assert.Equal("Zero-value transaction requires verification.", transaction.ParseErrorMessage);
        }

        [Fact]
        public void ExtractTransactions_Condition3_DoubleEntryContradiction_FlagsForReview()
        {
            // ARRANGE: Both Debit and Credit columns have money (Accounting impossibility)
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "SHIFTED COLUMNS";
            worksheet.Cell(2, 3).Value = "500.00"; // Debit Col
            worksheet.Cell(2, 4).Value = "100.00"; // Credit Col

            var columns = CreateDualColumnMappings();

            // Mock the parser: Both columns parse perfectly as valid numbers
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("500")))).Returns(AccountParseResult.Success(500.00m, "500.00"));
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("100")))).Returns(AccountParseResult.Success(100.00m, "100.00"));

            // ACT
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT: The extractor's internal logic should catch the contradiction
            var transaction = results.First();
            Assert.Equal(0m, transaction.Debit);  // Dumped to zero for safety
            Assert.Equal(0m, transaction.Credit); // Dumped to zero for safety
            Assert.True(transaction.NeedsReview);
            Assert.Equal("Ambiguous row: Contains both Debit and Credit values simultaneously.", transaction.ParseErrorMessage);
        }

        // =========================================================================
        // SECTION 2: EXTRACT PREVIEW (Lightweight UI Feedback)
        // =========================================================================

        [Fact]
        public void ExtractPreview_WithGarbageText_ReturnsZeroWithoutAuditFlags()
        {
            // ARRANGE: Use the exact same garbage text scenario
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "PREVIEW BROKEN ROW";
            worksheet.Cell(2, 3).Value = "INV-8675309";

            var columns = CreateSingleColumnMappings();

            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("INV-8675309"))))
                       .Returns(AccountParseResult.Failure("INV-8675309", "Contains illegal characters."));

            // ACT: Call the PREVIEW method instead
            List<TransactionPreview> results = _extractor.ExtractPreview(worksheet, 0, columns);

            // ASSERT: The preview DTO should just show zeroes.
            // (It ignores NeedsReview and ParseErrorMessage by design)
            Assert.Single(results);
            var preview = results.First();

            Assert.Equal(0m, preview.Debit);
            Assert.Equal(0m, preview.Credit);

            // Note: Since TransactionPreview doesn't contain NeedsReview or Error properties,
            // we simply assert that it parsed safely without throwing exceptions!
        }

        // =========================================================================
        // SECTION 3: DATE PARSING FALLBACKS & EDGE CASES
        // =========================================================================

        /// <summary>
        /// Objective: Validate that unparseable date strings (e.g., garbage text or invalid calendar days)
        /// do not crash the extractor. They must default the date and explicitly flag the row for human review.
        /// </summary>
        [Fact]
        public void ExtractTransactions_InvalidDateString_DefaultsDateAndFlagsForReview()
        {
            // ARRANGE: A row with unparseable date text but a perfectly valid amount
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = "Feb 30th 2026"; // Invalid calendar date
            worksheet.Cell(2, 2).Value = "CORRUPTED DATE ROW";
            worksheet.Cell(2, 3).Value = "150.00";

            var columns = CreateSingleColumnMappings();

            // Mock the parser: The amount parses successfully, isolating the test to the Date logic
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("150.00"))))
                       .Returns(AccountParseResult.Success(150.00m, "150.00"));

            // ACT
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT: The extractor must gracefully handle the failure
            Assert.Single(results);
            var transaction = results.First();

            // The date must safely fall back to the default struct value
            Assert.Equal(default(DateTime), transaction.Date);

            // The row must be explicitly flagged for human review (Tier 1 Error strategy)
            Assert.True(transaction.NeedsReview);

            // Validate that the error message contains context about the date failure
            Assert.NotNull(transaction.ParseErrorMessage);
            Assert.Contains("date", transaction.ParseErrorMessage.ToLower());
        }

        /// <summary>
        /// Objective: Validate that empty or missing date cells are handled gracefully,
        /// defaulting the date and triggering the review flag without throwing a NullReferenceException.
        /// </summary>
        [Fact]
        public void ExtractTransactions_EmptyDateCell_DefaultsDateAndFlagsForReview()
        {
            // ARRANGE: A row missing the date entirely
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = string.Empty; // Blank date
            worksheet.Cell(2, 2).Value = "MISSING DATE ROW";
            worksheet.Cell(2, 3).Value = "75.00";

            var columns = CreateSingleColumnMappings();

            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s != null && s.Contains("75.00"))))
                       .Returns(AccountParseResult.Success(75.00m, "75.00"));

            // ACT
            List<Transaction> results = _extractor.ExtractTransactions(worksheet, 0, columns);

            // ASSERT
            var transaction = results.First();
            Assert.Equal(default(DateTime), transaction.Date);
            Assert.True(transaction.NeedsReview);
        }

        // =========================================================================
        // PRIVATE MAPPING HELPERS (Using 0-Based Indices)
        // =========================================================================

        private Dictionary<string, DetectedField> CreateSingleColumnMappings()
        {
            return new Dictionary<string, DetectedField>
            {
                { "Col:Date", new DetectedField { ColumnIndex = 0 } },
                { "Col:Description", new DetectedField { ColumnIndex = 1 } },
                { "Col:Amount", new DetectedField { ColumnIndex = 2 } }
            };
        }

        private Dictionary<string, DetectedField> CreateDualColumnMappings()
        {
            return new Dictionary<string, DetectedField>
            {
                { "Col:Date", new DetectedField { ColumnIndex = 0 } },
                { "Col:Description", new DetectedField { ColumnIndex = 1 } },
                { "Col:Debit", new DetectedField { ColumnIndex = 2 } },
                { "Col:Credit", new DetectedField { ColumnIndex = 3 } }
            };
        }
    }
}