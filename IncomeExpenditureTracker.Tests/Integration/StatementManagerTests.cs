using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Xunit.Abstractions;
using IncomeExpenditureTracker.Services.Importing.Strategies;

using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Tests.Fixtures;
using IncomeExpenditureTracker.Tests.Observability;
using IncomeExpenditureTracker.Services.Messaging;
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

            // Bridge the StatementManager's internal logging directly to xUnit's console
            var loggerProvider = new TestOutputLoggerProvider(_output);
            _logger = loggerProvider.CreateLogger(nameof(StatementManagerTests)) as ILogger<StatementManager>
                      ?? new LoggerFactory().CreateLogger<StatementManager>();
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

            // Mock the strategy
            var mockStrategy = new Mock<IFileParserStrategy>();
            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(file1), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(file1), new List<string>()));

            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(file2), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("The process cannot access the file because it is being used by another process."));

            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(file3), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(file3), new List<string>()));

            var mockEditSession = new Mock<IStatementEditSession>();
            var mockBroker = new Mock<IApplicationBroker>();
            var mockSynonymService = new Mock<ISynonymService>();

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => mockEditSession.Object,
                mockSynonymService.Object,
                _logger,
                mockBroker.Object
            );

            var filePaths = new List<string> { file1, file2, file3 };
            var mockProgress = new Progress<LoadingProgress>();

            // Act
            StagingBatchResult result = await manager.StageFilesAsync(filePaths, mockProgress, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Successes.Count); // file1 and file3 should be here
            Assert.Single(result.Failures);          // file2 should be here

            var failure = result.Failures.First();
            Assert.Equal(ErrorSeverity.Warning, failure.Severity); // OS Locks are warnings, not fatal
            Assert.Contains("locked by another program", failure.Message);
        }

        // =================================================================================
        // NEW PHASE 5 TEST: Proves the Broker actively delivers error messages to the UI!
        // =================================================================================
        [Fact]
        public async Task StageFilesAsync_WhenFileIsLocked_ShouldPublishErrorMessage()
        {
            // Arrange
            string file1 = ExcelStatementGenerator.GenerateValidStatement(2);
            _tempFilesToCleanup.Add(file1);

            var mockStrategy = new Mock<IFileParserStrategy>();
            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(file1), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new IOException("Locked by Excel"));

            var mockBroker = new Mock<IApplicationBroker>(); // Our fake postman

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => new Mock<IStatementEditSession>().Object,
                new Mock<ISynonymService>().Object,
                _logger,
                mockBroker.Object
            );

            // Act
            await manager.StageFilesAsync(new List<string> { file1 }, null!, CancellationToken.None);

            // Assert: We mathematically prove that _broker.Send(...) was called exactly ONE time
            // and that the envelope it delivered contained the correct file name!
            mockBroker.Verify(b => b.Send(It.Is<FileStagingErrorMessage>(m =>
                m.Error.FileName == Path.GetFileName(file1) &&
                m.Error.Severity == ErrorSeverity.Warning)), Times.Once);
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

            var mockStrategy = new Mock<IFileParserStrategy>();
            var mockEditSession = new Mock<IStatementEditSession>();
            var mockBroker = new Mock<IApplicationBroker>();

            var mockSynonymService = new Mock<ISynonymService>();
            mockSynonymService
                .Setup(s => s.LearnFromCorrectionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => mockEditSession.Object,
                mockSynonymService.Object,
                _logger,
                mockBroker.Object
            );

            var pendingPreview = new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(validFile), new List<string>());

            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(validFile), It.IsAny<CancellationToken>()))
                .ReturnsAsync(pendingPreview);

            mockStrategy.Setup(s => s.GeneratePreviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new StatementPreview());

            // Stage the file first so it exists in the internal ConcurrentDictionary
            var stagingResult = await manager.StageFilesAsync(new List<string> { validFile }, null!, CancellationToken.None);
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

            var trackers = new List<PreviewTracker> { confirmedTracker };

            // Simulate the UI requesting a preview, which initializes our edit session in the dictionary
            await manager.PreviewStagedFileAsync(stagedFileId, null, CancellationToken.None);

            // Act
            await manager.CommitStagedBatchAsync(stagedFileId, trackers, CancellationToken.None);

            // Assert 1: Verify Import was called (Synchronous, so we check immediately)
            mockStrategy.Verify(i => i.ImportConfirmedBatchAsync(trackers, It.IsAny<CancellationToken>()), Times.Once);

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
                        It.IsAny<string>(),
                        It.IsAny<CancellationToken>()), Times.Once);

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
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => manager.PreviewStagedFileAsync(stagedFileId, null, CancellationToken.None));
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

            var mockStrategy = new Mock<IFileParserStrategy>();
            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(validFile), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(validFile), new List<string>()));

            mockStrategy.Setup(s => s.GeneratePreviewAsync("NonExistentSheet", It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("was not found in workbook"));

            var mockEditSession = new Mock<IStatementEditSession>();
            var mockBroker = new Mock<IApplicationBroker>();

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => mockEditSession.Object,
                new Mock<ISynonymService>().Object,
                _logger,
                mockBroker.Object
            );

            var stagingResult = await manager.StageFilesAsync(new List<string> { validFile }, null!, CancellationToken.None);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            // Act & Assert
            string badSheetName = "NonExistentSheet";
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId, badSheetName, CancellationToken.None));

            Assert.Contains("Failed to analyze the document", exception.Message);
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

            var mockStrategy = new Mock<IFileParserStrategy>();
            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(corruptFile), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(corruptFile), new List<string>()));

            mockStrategy.Setup(s => s.GeneratePreviewAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new Exception("Simulated catastrophic failure."));

            var mockEditSession = new Mock<IStatementEditSession>();
            var mockBroker = new Mock<IApplicationBroker>();

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => mockEditSession.Object,
                new Mock<ISynonymService>().Object,
                _logger,
                mockBroker.Object
            );

            var stagingResult = await manager.StageFilesAsync(new List<string> { corruptFile }, null!, CancellationToken.None);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId, null, CancellationToken.None));

            Assert.Contains("Failed to analyze the document", exception.Message);

            await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                manager.PreviewStagedFileAsync(stagedFileId, null, CancellationToken.None));
        }

        [Fact]
        public async Task PreviewStagedFileAsync_Negative_FileNotFound_ThrowsKeyNotFoundException()
        {
            // =================================================================================
            // OBJECTIVE: Test the lock-free dictionary guardrails.
            // DECISION: Request a preview for a random GUID that is not in the pending dictionary.
            // EXPECTATION: It must throw a KeyNotFoundException immediately.
            // =================================================================================

            var manager = new StatementManager(
                ext => new Mock<IFileParserStrategy>().Object,
                () => new Mock<IStatementEditSession>().Object,
                new Mock<ISynonymService>().Object,
                _logger,
                new Mock<IApplicationBroker>().Object
            );

            Guid ghostFileId = Guid.NewGuid();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
                manager.PreviewStagedFileAsync(ghostFileId, null, CancellationToken.None));

            Assert.Contains("was not found", exception.Message);
        }

        [Fact]
        public async Task StageFilesAsync_Negative_ExceedsFileLimit_ThrowsInvalidOperationException()
        {
            // =================================================================================
            // OBJECTIVE: Prevent RAM exhaustion.
            // DECISION: Pass a list of 6 file paths. It must reject the batch instantly.
            // =================================================================================

            var manager = new StatementManager(
                ext => new Mock<IFileParserStrategy>().Object,
                () => new Mock<IStatementEditSession>().Object,
                new Mock<ISynonymService>().Object,
                _logger,
                new Mock<IApplicationBroker>().Object
            );

            // Create a dummy list of 6 strings
            var tooManyFiles = Enumerable.Range(1, 6).Select(i => $"file_{i}.xlsx").ToList();

            // Act & Assert
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                manager.StageFilesAsync(tooManyFiles, null!, CancellationToken.None));

            Assert.Contains("Maximum limit of 5 files", exception.Message);
        }

        // =================================================================================
        // Cancellation Token Support
        // =================================================================================

        [Fact]
        public async Task StageFilesAsync_CancellationRequested_ThrowsOperationCanceledException()
        {
            // Arrange
            var mockStrategy = new Mock<IFileParserStrategy>();
            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => new Mock<IStatementEditSession>().Object,
                new Mock<ISynonymService>().Object,
                _logger,
                new Mock<IApplicationBroker>().Object
            );

            var cts = new CancellationTokenSource();
            cts.Cancel(); // Immediately trigger cancellation

            var filePaths = new List<string> { "dummy_file.xlsx" };

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await manager.StageFilesAsync(filePaths, null!, cts.Token));

            // Verify that the Loader was never reached due to the early exit
            mockStrategy.Verify(l => l.LoadAsync(It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task PreviewStagedFileAsync_CancellationRequested_ThrowsOperationCanceledException()
        {
            // Arrange
            string validFile = ExcelStatementGenerator.GenerateValidStatement(2);
            _tempFilesToCleanup.Add(validFile);

            var mockStrategy = new Mock<IFileParserStrategy>();
            mockStrategy.Setup(s => s.LoadAsync(It.IsAny<Stream>(), Path.GetFileName(validFile), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PendingFilePreview(Guid.NewGuid(), Path.GetFileName(validFile), new List<string>()));

            var manager = new StatementManager(
                ext => mockStrategy.Object,
                () => new Mock<IStatementEditSession>().Object,
                new Mock<ISynonymService>().Object,
                _logger,
                new Mock<IApplicationBroker>().Object
            );

            var stagingResult = await manager.StageFilesAsync(new List<string> { validFile }, null!, CancellationToken.None);
            Guid stagedFileId = stagingResult.Successes.First().Id;

            var cts = new CancellationTokenSource();
            cts.Cancel(); // Cancel before initiating preview

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await manager.PreviewStagedFileAsync(stagedFileId, null, cts.Token));
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