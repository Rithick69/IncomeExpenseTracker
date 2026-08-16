using System;
using System.Collections.Generic;
using System.Data; // Added for IDbConnection and IDbTransaction
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Tagging;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Logic
{
    public class ExcelStatementImportTests
    {
        // Boilerplate setup for mocks
        private readonly Mock<IDatabaseService> _dbMock = new();
        private readonly Mock<IEntityService> _entityMock = new();
        private readonly Mock<IAccountService> _accountMock = new();
        private readonly Mock<ITransactionExtractor<IXLWorksheet>> _extractorMock = new();
        private readonly Mock<IImportBatchService> _batchMock = new();
        private readonly Mock<ITransactionService> _transactionMock = new();
        private readonly Mock<ILogger<ExcelStatementImport>> _loggerMock = new();

        private readonly Mock<IDescriptionParser> _descParserMock = new();
        private readonly Mock<ITagEngine> _tagEngineMock = new();

        private ExcelStatementImport CreateService()
        {
            return new ExcelStatementImport(
                _dbMock.Object, _entityMock.Object, _accountMock.Object,
                _extractorMock.Object, _descParserMock.Object, _tagEngineMock.Object,
                _batchMock.Object, _transactionMock.Object, _loggerMock.Object);
        }

        /// <summary>
        /// Objective: Verify that if a StatementPreview is missing explicit metadata dictionary keys,
        /// the internal GetMetaValue helper assigns and passes correct fallback values.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_MissingMetadata_AppliesSafeFallbacks()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();

            // Empty fields to force fallbacks
            var previewMap = new StatementPreview { Fields = new Dictionary<string, DetectedField>() };

            _extractorMock.Setup(x => x.ExtractTransactions(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>()))
                          .Returns(new List<Transaction> { new Transaction { Description = "Test" } }); // Must return > 0 to proceed

            // Intercept the database transaction wrapper to execute the inner action synchronously for testing
            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action =>
                   {
                       await action.Invoke(null!, null!); // Simulating execution
                   });

            // Act
            await service.ImportConfirmedStatementAsync(worksheetMock.Object, previewMap);

            // Assert
            _entityMock.Verify(x => x.GetOrCreateEntity("Unknown Entity", It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _accountMock.Verify(x => x.GetOrCreateAccount(It.Is<Account>(a =>
                a.AccountNumber == "Unknown Account" &&
                a.AccountType == "Checking" &&
                a.Currency == "INR"), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Ensure that a failure during chunked database insertion triggers a full bubble-up exception
        /// which causes ExecuteInTransactionWithRetryAsync to rollback everything.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_DatabaseFailureDuringChunk_ThrowsAndTriggersRollback()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();
            var previewMap = new StatementPreview { Fields = new Dictionary<string, DetectedField>() };

            // Generate 300 transactions to force chunking (BatchSize is 250)
            var transactions = new List<Transaction>();
            for (int i = 0; i < 300; i++) transactions.Add(new Transaction { Description = "Test" });

            _extractorMock.Setup(x => x.ExtractTransactions(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>()))
                          .Returns(transactions);

            // Simulate the transaction wrapper executing the provided payload
            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action =>
                   {
                       await action.Invoke(null!, null!);
                   });

            // Setup the chunked insert to throw an exception on the second batch
            _transactionMock.SetupSequence(x => x.InsertTransactionsAsync(It.IsAny<List<Transaction>>(), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()))
                            .Returns(Task.CompletedTask) // First chunk of 250 succeeds
                            .ThrowsAsync(new InvalidOperationException("SQLite Constraint Violation")); // Second chunk of 50 fails

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.ImportConfirmedStatementAsync(worksheetMock.Object, previewMap));
        }

        /// <summary>
        /// Objective: Verify that if no transactions are extracted, the service logs a warning and exits early
        /// without calling the database.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_EmptyExtraction_LogsWarningAndAborts()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();
            var previewMap = new StatementPreview { Fields = new Dictionary<string, DetectedField>() };

            // Return empty list
            _extractorMock.Setup(x => x.ExtractTransactions(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>()))
                          .Returns(new List<Transaction>());

            // Act
            await service.ImportConfirmedStatementAsync(worksheetMock.Object, previewMap);

            // Assert
            _dbMock.Verify(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()), Times.Never);
        }

        /// <summary>
        /// Objective: Verify dynamic filename generation when the StatementPreview filename is null or empty.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_NullFileName_AppliesDynamicFallback()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();

            var previewMap = new StatementPreview { FileName = null!, Fields = new Dictionary<string, DetectedField>() };

            _extractorMock.Setup(x => x.ExtractTransactions(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>()))
                          .Returns(new List<Transaction> { new Transaction { Description = "Test" } });

            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action => await action.Invoke(null!, null!));

            var expectedPrefix = "Statement_";

            // Act
            await service.ImportConfirmedStatementAsync(worksheetMock.Object, previewMap);

            // Assert
            _batchMock.Verify(x => x.CreateBatch(It.Is<string>(s => s.StartsWith(expectedPrefix)), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Defensive programming check to ensure null arguments throw ArgumentNullException immediately.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_NullArguments_ThrowsArgumentNullException()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();
            var previewMap = new StatementPreview();

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await service.ImportConfirmedStatementAsync(null!, previewMap));
            await Assert.ThrowsAsync<ArgumentNullException>(async () => await service.ImportConfirmedStatementAsync(worksheetMock.Object, null!));
        }

        /// <summary>
        /// Objective: Validate how the system handles invalid or garbage currency formats in metadata.
        /// It should extract the raw trimmed value and pass it to the AccountService,
        /// allowing downstream database constraints to dictate acceptance or failure, rather than crashing the import pipeline.
        /// </summary>
        [Fact]
        public async Task ImportConfirmedStatementAsync_InvalidCurrencyFormat_PassesRawValueToAccountService()
        {
            // Arrange
            var service = CreateService();
            var worksheetMock = new Mock<IXLWorksheet>();

            // Provide a blatantly invalid currency format
            var invalidCurrency = "$$$ GARBAGE $$$";
            var previewMap = new StatementPreview
            {
                Fields = new Dictionary<string, DetectedField>
                {
                    { "Meta:CURRENCY", new DetectedField { ExtractedValue = invalidCurrency } }
                }
            };

            _extractorMock.Setup(x => x.ExtractTransactions(It.IsAny<IXLWorksheet>(), It.IsAny<int>(), It.IsAny<Dictionary<string, DetectedField>>()))
                          .Returns(new List<Transaction> { new Transaction { Description = "Test" } }); // Must return > 0 to proceed

            // Intercept the database transaction wrapper
            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action =>
                   {
                       await action.Invoke(null!, null!); // Simulating execution
                   });

            // Act
            await service.ImportConfirmedStatementAsync(worksheetMock.Object, previewMap);

            // Assert
            // Verify that the AccountService receives the exact garbage string, proving the parser doesn't swallow it.
            // If this string violates DB constraints, the ExecuteInTransactionWithRetryAsync will handle the rollback.
            _accountMock.Verify(x => x.GetOrCreateAccount(It.Is<Account>(a =>
                a.Currency == invalidCurrency), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }
    }
}