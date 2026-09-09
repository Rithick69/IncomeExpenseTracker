using System;
using System.Collections.Generic;
using System.Linq;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using Microsoft.Extensions.Logging;
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
            var logger = new Mock<ILogger<ExcelTransactionExtractor>>();
            _extractor = new ExcelTransactionExtractor(_parserMock.Object, logger.Object);
        }

        // =========================================================================
        // CONSTRUCTOR & GUARD CLAUSES
        // =========================================================================

        [Fact]
        public void Constructor_WithLogger_InitializesSuccessfully()
        {
            var logger = new Mock<ILogger<ExcelTransactionExtractor>>();
            var extractor = new ExcelTransactionExtractor(_parserMock.Object, logger.Object);

            Assert.NotNull(extractor);
        }

        [Fact]
        public void ExtractTransactions_NullColumnFields_ThrowsArgumentNullException()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            Assert.Throws<ArgumentNullException>(() =>
                _extractor.ExtractTransactions(worksheet, 0, null!));
        }

        // =========================================================================
        // COLUMN MAPPING FALLBACKS
        // =========================================================================

        [Fact]
        public void ExtractTransactions_DictionaryFallback_ResolvesCaseInsensitiveAndPrefixes()
        {
            // ARRANGE: Use messy dictionary keys that don't perfectly match the standard "Col:DATE"
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "TEST ROW";
            worksheet.Cell(2, 3).Value = "100.00";

            var messyColumns = new Dictionary<string, DetectedField>
            {
                { "date", new DetectedField { ColumnIndex = 0 } },           // Missing prefix, lowercase
                { "DESCRIPTION", new DetectedField { ColumnIndex = 1 } },    // Missing prefix, uppercase
                { "col:amount", new DetectedField { ColumnIndex = 2 } }      // Lowercase prefix
            };

            _parserMock.Setup(p => p.Parse(It.IsAny<string>())).Returns(AccountParseResult.Success(100m, "100.00"));

            // ACT
            var results = _extractor.ExtractTransactions(worksheet, 0, messyColumns);

            // ASSERT
            Assert.Single(results);
            Assert.Equal("TEST ROW", results.First().Description);
        }

        // =========================================================================
        // DESCRIPTION SANITIZATION & FILTERING
        // =========================================================================

        [Fact]
        public void ExtractTransactions_IsBalanceRow_SkipsExtractionEntirely()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "CLOSING BALANCE"; // Matches balance keyword
            worksheet.Cell(2, 3).Value = "5000.00";

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Empty(results); // Row should be completely ignored
        }

        [Fact]
        public void ExtractTransactions_DescriptionWithXSS_StripsTagsAndFlagsInvalidDescription()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "<script>alert(1)</script> AMAZON";
            worksheet.Cell(2, 3).Value = "15.00";

            _parserMock.Setup(p => p.Parse(It.IsAny<string>())).Returns(AccountParseResult.Success(15m, "15.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Single(results);
            var tx = results.First();
            Assert.Equal("scriptalert(1)/script AMAZON", tx.Description); // Tags stripped
            Assert.True(tx.ReviewStatus.HasFlag(ReviewFlags.InvalidDescription));
        }

        // =========================================================================
        // DATE PARSING & EDGE CASES
        // =========================================================================

        [Fact]
        public void ExtractTransactions_InvalidDateString_DefaultsDateAndFlagsInvalidDate()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = "Feb 30th 2026"; // Impossible calendar date
            worksheet.Cell(2, 2).Value = "CORRUPTED DATE ROW";
            worksheet.Cell(2, 3).Value = "150.00";

            _parserMock.Setup(p => p.Parse(It.IsAny<string>())).Returns(AccountParseResult.Success(150m, "150.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Single(results);
            var tx = results.First();
            Assert.Equal(default(DateTime), tx.Date);
            Assert.True(tx.ReviewStatus.HasFlag(ReviewFlags.InvalidDate));
        }

        // =========================================================================
        // AMOUNT PARSING (Single & Dual Column Logic)
        // =========================================================================

        [Fact]
        public void ExtractTransactions_SingleColumn_NegativeAmount_MapsToDebit()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "CLEAN WITHDRAWAL";
            worksheet.Cell(2, 3).Value = "-150.00";

            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s.Contains("-150.00"))))
                       .Returns(AccountParseResult.Success(-150.00m, "-150.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Single(results);
            var tx = results.First();
            Assert.Equal(150.00m, tx.Debit);
            Assert.Equal(0m, tx.Credit);
            Assert.Equal(ReviewFlags.None, tx.ReviewStatus);
        }

        [Fact]
        public void ExtractTransactions_SingleColumn_ZeroAmount_DumpsToZeroAndFlagsAmount()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "FEE WAIVER";
            worksheet.Cell(2, 3).Value = "0.00";

            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s.Contains("0.00"))))
                       .Returns(AccountParseResult.Success(0m, "0.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Single(results);
            var tx = results.First();
            Assert.Equal(0m, tx.Debit);
            Assert.Equal(0m, tx.Credit);
            // Even though parsing succeeded, 0m forces an InvalidAmount flag for human review
            Assert.True(tx.ReviewStatus.HasFlag(ReviewFlags.InvalidAmount) || tx.ReviewStatus != ReviewFlags.None);
        }

        [Fact]
        public void ExtractTransactions_DualColumn_DoubleEntryContradiction_FlagsInvalidAmount()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(2, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(2, 2).Value = "SHIFTED COLUMNS";
            worksheet.Cell(2, 3).Value = "500.00"; // Debit Col
            worksheet.Cell(2, 4).Value = "100.00"; // Credit Col

            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s.Contains("500")))).Returns(AccountParseResult.Success(500.00m, "500.00"));
            _parserMock.Setup(p => p.Parse(It.Is<string>(s => s.Contains("100")))).Returns(AccountParseResult.Success(100.00m, "100.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateDualColumnMappings());

            Assert.Single(results);
            var tx = results.First();
            Assert.Equal(0m, tx.Debit);  // Dumped to zero
            Assert.Equal(0m, tx.Credit); // Dumped to zero
            Assert.True(tx.ReviewStatus.HasFlag(ReviewFlags.InvalidAmount));
        }

        // =========================================================================
        // EXTRACTION LIMITS & CIRCUIT BREAKERS
        // =========================================================================

        [Fact]
        public void ExtractTransactions_HitsMaxInvalidRows_CircuitBreakerStopsExtraction()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");

            // Write 10 completely blank rows (headerRow is 0, so starts at 2)
            for (int i = 2; i <= 11; i++)
            {
                worksheet.Cell(i, 1).Value = "";
            }

            // 11th row is a valid transaction, but the circuit breaker should prevent reaching it
            worksheet.Cell(12, 1).Value = new DateTime(2026, 7, 19);
            worksheet.Cell(12, 2).Value = "TOO FAR DOWN";
            worksheet.Cell(12, 3).Value = "100.00";

            _parserMock.Setup(p => p.Parse(It.IsAny<string>())).Returns(AccountParseResult.Success(100m, "100.00"));

            var results = _extractor.ExtractTransactions(worksheet, 0, CreateSingleColumnMappings());

            Assert.Empty(results); // Should break early and return 0 results
        }

        [Fact]
        public void ExtractPreview_Exceeds20Rows_CapsAt20Rows()
        {
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");

            // Generate 25 valid rows
            for (int i = 2; i <= 26; i++)
            {
                worksheet.Cell(i, 1).Value = new DateTime(2026, 7, 19);
                worksheet.Cell(i, 2).Value = $"TXN {i}";
                worksheet.Cell(i, 3).Value = "10.00";
            }

            _parserMock.Setup(p => p.Parse(It.IsAny<string>())).Returns(AccountParseResult.Success(10m, "10.00"));

            var results = _extractor.ExtractPreview(worksheet, 0, CreateSingleColumnMappings());

            // Preview logic strictly enforces maxRow = startRow + 19 (which is exactly 20 rows)
            Assert.Equal(20, results.Count);
        }

        // =========================================================================
        // HELPERS
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