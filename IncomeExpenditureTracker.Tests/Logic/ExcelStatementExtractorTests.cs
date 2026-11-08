using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.PreviewInsights;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Logic
{
    public class ExcelStatementExtractorTests
    {
        private readonly Mock<IHeaderDetector<IXLWorksheet>> _headerDetectorMock = new();
        private readonly Mock<IFieldMapper<IXLWorksheet>> _fieldMapperMock = new();
        private readonly Mock<ITransactionExtractor<IXLWorksheet>> _transactionExtractorMock = new();
        private readonly Mock<IConfidenceService> _confidenceMock = new();
        private readonly Mock<ILogger<ExcelStatementExtractor>> _loggerMock = new();

        private ExcelStatementExtractor CreateExtractor()
        {
            return new ExcelStatementExtractor(
                _headerDetectorMock.Object,
                _fieldMapperMock.Object,
                _transactionExtractorMock.Object,
                _confidenceMock.Object,
                _loggerMock.Object);
        }

        [Fact]
        public async Task Analyze_ZeroTransactionsExtracted_Triggers20RowFallback()
        {
            // Arrange
            var extractor = CreateExtractor();
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");

            // Setup physical rows for the fallback to scoop up
            worksheet.Cell(1, 1).Value = "Header1";
            worksheet.Cell(2, 1).Value = "Fallback Data 1";
            worksheet.Cell(2, 2).Value = "Fallback Data 2";
            worksheet.Cell(3, 1).Value = "Fallback Data 3";

            _headerDetectorMock.Setup(h => h.DetectHeaderRow(It.IsAny<IXLWorksheet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0); // 0-based index (Row 1)

            _fieldMapperMock.Setup(f => f.DetectAccountDetails(It.IsAny<IXLWorksheet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, DetectedField>());

            _fieldMapperMock.Setup(f => f.DetectColumns(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, DetectedField>());

            // FORCE standard extraction to fail by returning 0 rows
            _transactionExtractorMock.Setup(t => t.ExtractPreview(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>(), It.IsAny<CancellationToken>()))
                .Returns(new List<TransactionPreview>());

            // Act
            var result = await extractor.Analyze(worksheet, "test.xlsx", false, CancellationToken.None);

            // Assert
            Assert.NotEmpty(result.PreviewTransactions);
            Assert.Contains("[RAW DATA]", result.PreviewTransactions.First().Description);
            Assert.Equal(default(DateTime), result.PreviewTransactions.First().Date); // Ensures date flags are forced
            Assert.True(result.RequiresVerification);
        }

        [Fact]
        public async Task ReExtractAsync_UsesUpdatedFields_AndRecalculatesConfidence()
        {
            // Arrange
            var extractor = CreateExtractor();
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");
            worksheet.Cell(1, 1).Value = "Test";

            var updatedFields = new Dictionary<string, DetectedField>
            {
                { "EntityName", new DetectedField { ExtractedValue = "NEW_BANK" } }
            };
            var originalHeaders = new List<string> { "Date", "Desc" };

            var newExtractedRows = new List<TransactionPreview>
            {
                new TransactionPreview { Description = "New Txn" }
            };

            // Verify the extractor is called with the user's updated fields, not the original ones
            _transactionExtractorMock.Setup(t => t.ExtractPreview(It.IsAny<IXLWorksheet>(), 0, updatedFields, It.IsAny<CancellationToken>()))
                .Returns(newExtractedRows);

            _confidenceMock.Setup(c => c.CalculateConfidence(updatedFields, newExtractedRows))
                .Returns(95);

            // Act
            var result = await extractor.ReExtractAsync(worksheet, "test.xlsx", 0, updatedFields, originalHeaders, CancellationToken.None);

            // Assert
            Assert.Equal(95, result.ConfidenceScore);
            Assert.False(result.RequiresVerification); // 95 is >= 80, should not require verification
            Assert.Single(result.PreviewTransactions);
            Assert.Equal("New Txn", result.PreviewTransactions.First().Description);
            Assert.Equal(originalHeaders, result.RawHeaders);

            // Verify ReExtractAccountDetailsAsync was executed to update metadata targets
            _fieldMapperMock.Verify(f => f.ReExtractAccountDetailsAsync(worksheet, updatedFields, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task Analyze_CatastrophicFailure_ReturnsSafeEmptyPreview()
        {
            // Arrange
            var extractor = CreateExtractor();
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");

            // Force an exception deep in the dependency chain
            _headerDetectorMock.Setup(h => h.DetectHeaderRow(It.IsAny<IXLWorksheet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Simulated catastrophic crash"));

            // Act
            var result = await extractor.Analyze(worksheet, "crash.xlsx", false, CancellationToken.None);

            // Assert: Must return a safe default instead of bubbling up the crash and destroying the SPA state
            Assert.NotNull(result);
            Assert.Equal(-1, result.HeaderRow);
            Assert.Empty(result.PreviewTransactions);
            Assert.True(result.RequiresVerification);
            Assert.Equal(0, result.ConfidenceScore);
        }

        [Fact]
        public async Task Analyze_BlankHeaderCells_InjectsDeterministicPlaceholder()
        {
            // Arrange
            var extractor = CreateExtractor();
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("Sheet1");

            // Header row (Row 1): Col 1 has data, Col 2 is empty, Col 3 has data
            worksheet.Cell(1, 1).Value = "Date";
            worksheet.Cell(1, 2).Value = "";
            worksheet.Cell(1, 3).Value = "Amount";

            _headerDetectorMock.Setup(h => h.DetectHeaderRow(It.IsAny<IXLWorksheet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(0); // Row 1 is index 0

            _fieldMapperMock.Setup(f => f.DetectAccountDetails(It.IsAny<IXLWorksheet>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, DetectedField>());

            _fieldMapperMock.Setup(f => f.DetectColumns(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Dictionary<string, DetectedField>());

            _transactionExtractorMock.Setup(t => t.ExtractPreview(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>(), It.IsAny<CancellationToken>()))
                .Returns(new List<TransactionPreview> { new TransactionPreview() });

            // Act
            var result = await extractor.Analyze(worksheet, "headers.xlsx", false, CancellationToken.None);

            // Assert
            Assert.Equal(3, result.RawHeaders.Count);
            Assert.Equal("Date", result.RawHeaders[0]);
            Assert.Equal("[Column 2 - Blank]", result.RawHeaders[1]); // Ensure empty cell is replaced
            Assert.Equal("Amount", result.RawHeaders[2]);
        }
    }
}