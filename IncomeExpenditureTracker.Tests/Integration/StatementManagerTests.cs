using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;

using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Tests.Fixtures;
using IncomeExpenditureTracker.Tests.Observability;
using ClosedXML.Excel;

namespace IncomeExpenditureTracker.Tests.Integration
{
    public class StatementManagerTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly ILogger<StatementManager> _logger;
        private readonly List<string> _tempFilesToCleanup = new();

        public StatementManagerTests(ITestOutputHelper output)
        {
            _output = output;

            // Bridge the StatementManager's internal logging directly to xUnit's console[cite: 5]
            var loggerProvider = new TestOutputLoggerProvider(_output);
            _logger = loggerProvider.CreateLogger(nameof(StatementManagerTests)) as ILogger<StatementManager>
                      ?? new LoggerFactory().CreateLogger<StatementManager>();
        }

        // =================================================================================
        // HELPER METHOD: Generates a strictly valid StatementLoadResult for our mocks
        // =================================================================================
        private StatementLoadResult CreateMockLoadResult(string filePath)
        {
            var workbook = new XLWorkbook(filePath);
            var worksheet = workbook.Worksheet(1); // Grab the first generated sheet
            var fileName = Path.GetFileName(filePath);

            // We use a dummy MemoryStream here to satisfy the constructor's strict Stream requirement
            // without holding an actual OS lock during the test assertions.
            var dummyStream = new MemoryStream();

            return new StatementLoadResult(workbook, worksheet, fileName, dummyStream);
        }

        [Fact]
        public async Task StageFilesAsync_ConcurrentExecution_IsolatesOSFileLocksWithoutCrashing()
        {
            // =================================================================================
            // OBJECTIVE: Test the Resilient Partial Staging model.
            // DECISION: We will feed the manager 3 files. We will mock the loader so that
            // 2 files succeed, but 1 file throws an IOException (simulating it being open in Excel).
            // We must prove that Task.WhenAll finishes, returning 2 successes and 1 failure.
            // =================================================================================

            // Arrange: Generate real Excel files using the provided utility[cite: 6]
            string file1 = ExcelStatementGenerator.GenerateValidStatement(5);
            string file2 = ExcelStatementGenerator.GenerateValidStatement(5);
            string file3 = ExcelStatementGenerator.GenerateValidStatement(5);

            _tempFilesToCleanup.AddRange(new[] { file1, file2, file3 });

            // Mock the Loader to simulate the OS lock on file2
            var mockLoader = new Mock<IStatementLoader>();

            // Using the new helper method to construct valid StatementLoadResults
            mockLoader.Setup(l => l.LoadStatementAsync(file1, null!))
                      .ReturnsAsync(CreateMockLoadResult(file1));

            mockLoader.Setup(l => l.LoadStatementAsync(file2, null!))
                      .ThrowsAsync(new IOException("The process cannot access the file because it is being used by another process."));

            mockLoader.Setup(l => l.LoadStatementAsync(file3, null!))
                      .ReturnsAsync(CreateMockLoadResult(file3));

            // Declare and initialize the transient mocks before using them
            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImport = new Mock<IStatementImport<IXLWorksheet>>();

            var manager = new StatementManager(
                mockLoader.Object,
                () => mockExtractor.Object,    // Func<IStatementExtractor>
                () => mockEditSession.Object,  // Func<IStatementEditSession>
                () => mockImport.Object,       // Func<IStatementImport>
                new Mock<ISynonymService>().Object,
                _logger // Injecting the xUnit bridged logger[cite: 5]
            );

            var filePaths = new List<string> { file1, file2, file3 };
            var mockProgress = new Progress<LoadingProgress>();

            // Act
            // If concurrency isolation fails, this will throw an exception and fail the test.
            // If it works, it will trap the error and return a mixed batch result.
            StagingBatchResult result = await manager.StageFilesAsync(filePaths, mockProgress);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Successes.Count); // file1 and file3 should be here
            Assert.Single(result.Failures);          // file2 should be here

            var failure = result.Failures.First();
            Assert.Equal(ErrorSeverity.Warning, failure.Severity); // OS Locks are warnings, not fatal
            Assert.Contains("locked by another program", failure.Message);
        }

        [Fact]
        public async Task CommitStagedFileAsync_DispatchesBackgroundLearning_And_DisposesStream()
        {
            // =================================================================================
            // OBJECTIVE: Validate the Commit phase and Fire-and-Forget Memory Management[cite: 2].
            // DECISION: We use a short delay to allow the background Task.Run to finish naturally
            // without risking xUnit deadlocks, then we assert the mock was called.
            // =================================================================================

            // Arrange
            string validFile = ExcelStatementGenerator.GenerateValidStatement(2);
            _tempFilesToCleanup.Add(validFile);

            var mockLoader = new Mock<IStatementLoader>();
            mockLoader.Setup(l => l.LoadStatementAsync(validFile, null!))
                      .ReturnsAsync(CreateMockLoadResult(validFile));

            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImportService = new Mock<IStatementImport<IXLWorksheet>>();
            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();

            var mockSynonymService = new Mock<ISynonymService>();
            mockSynonymService
                .Setup(s => s.LearnFromCorrectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);

            var manager = new StatementManager(
                mockLoader.Object,
                () => mockExtractor.Object,
                () => mockEditSession.Object,
                () => mockImportService.Object,
                mockSynonymService.Object,
                _logger
            );

            // Stage the file first so it exists in the internal ConcurrentDictionary
            var stagingResult = await manager.StageFilesAsync(new List<string> { validFile }, null!);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            // Create a fake confirmed tracker with 1 column correction

            var confirmedTracker = new PreviewTracker
            {
                FinalPreview = new StatementPreview(),
                ColumnCorrections = new List<ColumnMappingCorrection>
        {
            new ColumnMappingCorrection { RawHeaderName = "TXN_DATE", TargetField = "Date", Category = "TRANSACTION" }
        }
            };

            // Act
            await manager.CommitStagedFileAsync(stagedFileId, confirmedTracker);

            // Assert 1: Verify Import was called (Synchronous, so we check immediately)
            mockImportService.Verify(i => i.ImportConfirmedStatementAsync(It.IsAny<IXLWorksheet>(), confirmedTracker.FinalPreview), Times.Once);

            // Assert 2: Polling Wait for the Fire-and-Forget Background Thread
            // We give the thread pool up to 3 seconds to execute, checking every 50ms.
            bool backgroundTaskCompleted = false;
            for (int i = 0; i < 60; i++) // 60 attempts * 50ms = 3 seconds max wait
            {
                try
                {
                    // If this succeeds, the background thread finished!
                    mockSynonymService.Verify(s => s.LearnFromCorrectionAsync(
                        It.IsAny<string>(),
                        It.IsAny<string>(),
                        It.IsAny<string>()), Times.Once);

                    backgroundTaskCompleted = true;
                    break; // Exit the loop immediately to keep the test fast
                }
                catch (MockException)
                {
                    await Task.Delay(50); // Not done yet, yield for 50ms and check again
                }
            }

            // Final assert to guarantee the test fails cleanly if the loop timed out
            Assert.True(backgroundTaskCompleted, "The background task failed to invoke the synonym service within 3 seconds. Check your test runner console output for hidden exceptions inside the Task.Run block.");

            // Assert 3: Prove cleanup occurred
            mockEditSession.Verify(s => s.Clear(), Times.Once);

            // Assert 4: Prove the file was removed from the staging dictionary.
            // Since DiscardFile silently ignores missing files by design, we prove it's gone
            // by attempting to generate a preview for it, which MUST throw a KeyNotFoundException.
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.PreviewStagedFileAsync(stagedFileId));
            Assert.Contains("was not found", exception.Message);
        }

        [Fact]
        public async Task PreviewStagedFileAsync_Negative_SheetNotFound_ThrowsInvalidOperationException()
        {

            // =================================================================================
            // OBJECTIVE: Test the Target Document Resolution logic.
            // DECISION: Stage a valid file, but explicitly request a sheet name that doesn't exist.
            // EXPECTATION: The manager throws an InvalidOperationException indicating the sheet is missing.
            // =================================================================================

            // Arrange
            string validFile = ExcelStatementGenerator.GenerateValidStatement(3);
            _tempFilesToCleanup.Add(validFile);

            var mockLoader = new Mock<IStatementLoader>();

            // Using the new helper method
            mockLoader.Setup(l => l.LoadStatementAsync(validFile, null!))
                      .ReturnsAsync(CreateMockLoadResult(validFile));

            // Declare and initialize the transient mocks before using them
            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImport = new Mock<IStatementImport<IXLWorksheet>>();

            var manager = new StatementManager(
                mockLoader.Object,
                () => mockExtractor.Object,    // Func<IStatementExtractor>
                () => mockEditSession.Object,  // Func<IStatementEditSession>
                () => mockImport.Object,       // Func<IStatementImport>
                new Mock<ISynonymService>().Object,
                _logger // Injecting the xUnit bridged logger[cite: 5]
            ); ;

            var stagingResult = await manager.StageFilesAsync(new List<string> { validFile }, null!);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            // Act & Assert
            string badSheetName = "NonExistentSheet";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId, badSheetName));

            Assert.Contains($"was not found in workbook", exception.Message);
        }

        [Fact]
        public async Task PreviewStagedFileAsync_Negative_ExtractionFails_DiscardsFileAndThrows()
        {
            // =================================================================================
            // OBJECTIVE: Test Tier 2 Sink and emergency memory cleanup.
            // DECISION: Force the extractor to throw an exception. Verify that DiscardFile is called
            // to release the OS lock before the exception bubbles up to the UI.
            // =================================================================================

            // Arrange
            string corruptFile = ExcelStatementGenerator.GenerateCorruptedStatement();
            _tempFilesToCleanup.Add(corruptFile);

            var mockLoader = new Mock<IStatementLoader>();

            // Using the new helper method
            mockLoader.Setup(l => l.LoadStatementAsync(corruptFile, null!))
                      .ReturnsAsync(CreateMockLoadResult(corruptFile));

            // Declare and initialize the transient mocks before using them
            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImport = new Mock<IStatementImport<IXLWorksheet>>();

            mockExtractor.Setup(e => e.Analyze(It.IsAny<IXLWorksheet>(), It.IsAny<string>(), It.IsAny<bool>()))
                         .ThrowsAsync(new Exception("Simulated catastrophic closedXML failure."));

            var manager = new StatementManager(
                mockLoader.Object,
                () => mockExtractor.Object,    // Func<IStatementExtractor>
                () => mockEditSession.Object,  // Func<IStatementEditSession>
                () => mockImport.Object,       // Func<IStatementImport>
                new Mock<ISynonymService>().Object,
                _logger // Injecting the xUnit bridged logger
            );


            var stagingResult = await manager.StageFilesAsync(new List<string> { corruptFile }, null!);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId));

            Assert.Contains("Failed to analyze the document", exception.Message);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId));
        }

        [Fact]
        public async Task PreviewStagedFileAsync_Negative_FileNotFound_ThrowsKeyNotFoundException()
        {
            // =================================================================================
            // OBJECTIVE: Test the lock-free dictionary guardrails.
            // DECISION: Request a preview for a random GUID that is not in the pending dictionary.
            // EXPECTATION: It must throw a KeyNotFoundException immediately.
            // =================================================================================

            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImport = new Mock<IStatementImport<IXLWorksheet>>();

            var manager = new StatementManager(
                new Mock<IStatementLoader>().Object,
                () => mockExtractor.Object,    // Func<IStatementExtractor>
                () => mockEditSession.Object,  // Func<IStatementEditSession>
                () => mockImport.Object,       // Func<IStatementImport>
                new Mock<ISynonymService>().Object,
                _logger // Injecting the xUnit bridged logger
            );

            Guid ghostFileId = Guid.NewGuid();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                manager.PreviewStagedFileAsync(ghostFileId));

            Assert.Contains("was not found", exception.Message);
        }

        [Fact]
        public async Task StageFilesAsync_Negative_ExceedsFileLimit_ThrowsInvalidOperationException()
        {
            // =================================================================================
            // OBJECTIVE: Prevent RAM exhaustion.
            // DECISION: Pass a list of 6 file paths. It must reject the batch instantly.
            // =================================================================================

            var mockExtractor = new Mock<IStatementExtractor<IXLWorksheet>>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockImport = new Mock<IStatementImport<IXLWorksheet>>();

            var manager = new StatementManager(
                new Mock<IStatementLoader>().Object,
                () => mockExtractor.Object,    // Func<IStatementExtractor>
                () => mockEditSession.Object,  // Func<IStatementEditSession>
                () => mockImport.Object,       // Func<IStatementImport>
                new Mock<ISynonymService>().Object,
                _logger // Injecting the xUnit bridged logger
            );

            // Create a dummy list of 6 strings
            var tooManyFiles = Enumerable.Range(1, 6).Select(i => $"file_{i}.xlsx").ToList();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StageFilesAsync(tooManyFiles, null!));

            Assert.Contains("Maximum limit of 5 files", exception.Message);
        }

        public void Dispose()
        {
            // Clean up the dynamically generated files[cite: 6]
            foreach (var file in _tempFilesToCleanup)
            {
                if (File.Exists(file))
                {
                    try { File.Delete(file); } catch { /* Best effort cleanup */ }
                }
            }
        }
    }
}