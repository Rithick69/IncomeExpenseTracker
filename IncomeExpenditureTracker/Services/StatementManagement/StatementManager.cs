using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Importing;
namespace IncomeExpenditureTracker.Services.StatementManagement;

// This service manages the lifecycle of uploaded statement files, including staging them for preview and allowing users to discard them if they choose not to proceed with the import.
// It ensures that memory is properly managed by disposing of loaded statements when they are no longer needed
// or when the session ends. It also enforces a limit of 5 files per session to prevent excessive memory usage.
// The StatementManager interacts with the StatementLoader to load files and keeps track of them in memory until the user decides to import or discard them.
// The StageFilesAsync method loads multiple files concurrently, providing progress updates to the UI. Each loaded file is stored in a dictionary with a unique identifier (GUID) for easy retrieval and management.
// The DiscardFile method allows users to remove a staged file from memory if they decide not to proceed with it, ensuring that resources are freed up immediately.
public class StatementManager : IDisposable
{
    private readonly IStatementLoader _statementLoader;

    private readonly IStatementExtractor<IXLWorksheet> _statementExtractor;
    private readonly IStatementEditSession _statementEditSession;
    private readonly IStatementImport<IXLWorksheet> _statementImport;

    private readonly ILogger<StatementManager> _logger;
    private readonly ISynonymService _synonymService;

    // Lock-free concurrent storage prevents thread contention between UI reads and parallel background loads
    private readonly ConcurrentDictionary<Guid, StatementLoadResult> _pendingStatements = new();

    private volatile bool _isDisposed;


    public StatementManager(
        IStatementLoader statementLoader,
        IStatementExtractor<IXLWorksheet> statementExtractor,
        IStatementEditSession statementEditSession,
        IStatementImport<IXLWorksheet> statementImport,
        ISynonymService synonymService,
        ILogger<StatementManager> logger
        )
    {
        _statementLoader = statementLoader;
        _statementExtractor = statementExtractor;
        _statementEditSession = statementEditSession;
        _statementImport = statementImport;
        _synonymService = synonymService;
        _logger = logger;
    }

    // This method stages multiple files for preview by loading them asynchronously and providing progress updates.
    // It enforces a maximum limit of 5 files per session to prevent excessive memory usage
    // and returns a list of PendingFilePreview objects that contain the file name and sheet names for each loaded file, which can be displayed in the UI for user confirmation before import.
    /// <summary>
    /// Phase 1: Asynchronously stages up to 5 files concurrently into RAM using a Resilient Partial Staging model.
    /// If individual files fail (e.g., OS file lock or corruption), they are trapped and returned as UI errors,
    /// allowing successfully loaded files to remain in the staging queue without interruption.
    /// </summary>
    public async Task<StagingBatchResult> StageFilesAsync(List<string> filePaths, IProgress<LoadingProgress> progress)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePaths);

        if (filePaths.Count > 5)
        {
            _logger.LogWarning("Staging rejected: User attempted to stage {Count} files (limit is 5).", filePaths.Count);
            throw new InvalidOperationException("Maximum limit of 5 files per session exceeded.");
        }

        int totalFiles = filePaths.Count;
        int completedFiles = 0;

        // Thread-safe collections to gather results from parallel tasks simultaneously
        var successes = new ConcurrentBag<PendingFilePreview>();
        var failures = new ConcurrentBag<FileStagingError>();

        progress?.Report(new LoadingProgress { Percentage = 0, Message = $"Staging {totalFiles} files..." });

        // Load all files concurrently and track progress
        // We use Task.WhenAll to load all files in parallel, which can significantly reduce the time taken to stage multiple files, especially if they are large.
        // Each file is loaded using the StatementLoader, and as each file is successfully loaded, we update the progress to reflect how many files have been staged so far.
        // Each loaded file is stored in the _pendingStatements dictionary with a unique GUID, allowing us to manage them effectively and provide the necessary information for the preview UI.

        var loadTasks = filePaths.Select(async path =>
        {
            var fileId = Guid.NewGuid();
            var fileName = Path.GetFileName(path);

            try
            {
                // Load the file asynchronously
                var result = await _statementLoader.LoadStatementAsync(path, progress: null!);

                result.FileName = fileName; // Store the file name in the StatementLoadResult for reference

                // Defensive Guard: If the user closed the window while this async load was in-flight,
                // immediately dispose the newly loaded stream and abort silently.
                if (_isDisposed)
                {
                    _logger.LogWarning("Manager was disposed while loading '{FileName}'. Disposing stream immediately.", fileName);
                    result.Dispose();
                    return;
                }

                // Safely store the successful workbook in our lock-free RAM registry
                _pendingStatements.TryAdd(fileId, result);

                var sheetNames = result.Workbook.Worksheets.Select(w => w.Name).ToList();
                _logger.LogDebug("Successfully staged '{FileName}' ({SheetCount} worksheets found).", fileName, sheetNames.Count);

                // Add to our successful list for the UI grid
                successes.Add(new PendingFilePreview(fileId, fileName, sheetNames));
            }
            catch (Exception ex)
            {
                // RESILIENT ERROR TRAPPING: Trap failure per-file without aborting the rest of the batch!
                _logger.LogWarning(ex, "Failed to load file '{FileName}' during staging. Adding to failure list for UI notification.", fileName);

                // Defensive cleanup: If the file was partially added before crashing, pull it out and dispose it
                if (_pendingStatements.TryRemove(fileId, out var orphanedResult))
                {
                    orphanedResult.Dispose();
                }

                // Record the error so the UI ViewModel can show a toast/popup notification to the user
                failures.Add(new FileStagingError(fileName, path, ex.Message, ex));
            }
            finally
            {
                // By putting progress in a 'finally' block, the UI loading bar reliably increments
                // whether the individual file succeeded OR failed!
                int currentCompleted = Interlocked.Increment(ref completedFiles);
                progress?.Report(new LoadingProgress
                {
                    Percentage = currentCompleted * 100 / totalFiles,
                    Message = $"Processed {currentCompleted} of {totalFiles} files..."
                });
            }
        });

        // Wait for all 5 parallel loading tasks to finish their try/catch blocks
        await Task.WhenAll(loadTasks);

        _logger.LogInformation("Staging batch completed. Successes: {SuccessCount}, Failures: {FailureCount}.", successes.Count, failures.Count);

        // Return the combined hand-off bundle to the UI ViewModel
        return new StagingBatchResult
        {
            Successes = successes.ToList(),
            Failures = failures.ToList()
        };
    }

    /// <summary>
    /// Phase 2: Retrieves a staged document from memory, executes extraction analysis,
    /// and initializes the interactive editing session for UI verification.
    /// </summary>
    /// <param name="fileId">The unique GUID assigned to the file during StageFilesAsync.</param>
    /// <param name="targetSheetName">Optional specific sheet name if the workbook has multiple sheets.</param>
    /// <returns>A StatementPreview DTO containing the 20-row preview grid and detected dictionary fields.</returns>
    public async Task<StatementPreview> PreviewStagedFileAsync(Guid fileId, string? targetSheetName = null)
    {
        ThrowIfDisposed();
        _logger.LogInformation("Generating preview for Staging ID: {FileId}. Target Sheet: '{SheetName}'", fileId, targetSheetName ?? "Default Primary");

        // STEP 1: RETRIEVE STAGED FILE
        // We retrieve the staged file from the in-memory dictionary using the provided fileId.
        var stagedFile = GetStagedFileOrThrow(fileId);

        // STEP 2: TARGET DOCUMENT RESOLUTION
        // If the UI requested a specific worksheet (e.g., from a sheet-selector dropdown),
        // we resolve it by name. Otherwise, we default to the primary active worksheet.
        // Note: If your extractor's Analyse method expects the entire XLWorkbook instead of an IXLWorksheet,
        // you can pass 'stagedFile.Workbook' directly as the 'document' parameter below!
        // STEP 2: Defensive Target Document Resolution
        IXLWorksheet targetDocument;
        if (string.IsNullOrWhiteSpace(targetSheetName))
        {
            targetDocument = stagedFile.Worksheet; // Default primary worksheet
        }
        else if (!stagedFile.Workbook.Worksheets.TryGetWorksheet(targetSheetName, out targetDocument!))
        {
            _logger.LogError("Worksheet resolution failed: Sheet '{SheetName}' not found in file '{FileName}'.", targetSheetName, stagedFile.FileName);
            throw new InvalidOperationException(
                $"Worksheet '{targetSheetName}' was not found in workbook '{stagedFile.FileName}'.");
        }


        // STEP 3: EXECUTE IN-MEMORY EXTRACTION ANALYSIS
        // We call your extractor's Analyse method, passing both the in-memory document AND the FileName string.
        // Guardrail Compliance: The extractor reads cells from 'targetDocument'. The 'stagedFile.FileName' string
        // is utilized strictly as metadata (e.g., attaching the source file name to the DTO or hashing)
        // without ever performing redundant file I/O on disk.
        // This step emits the 20-row visual grid and the namespaced Dictionary<string, DetectedField> schema.
        _logger.LogDebug("Executing cell extraction and schema analysis on sheet '{SheetName}' for file '{FileName}'...", targetDocument.Name, stagedFile.FileName);
        StatementPreview preview = await _statementExtractor.Analyze(targetDocument, stagedFile.FileName);

        // STEP 4: INITIALIZE THE IN-MEMORY EDITING SCRATCHPAD
        // Before returning to the UI, we hand off the generated preview DTO to the StatementEditSession.
        // This session acts as a lightweight "shopping cart" that will hold user column re-mappings,
        // tag overrides, and row exclusions in memory until explicit commit confirmation.
        // Per our rules, this initialization executes ZERO SQLite database writes.
        _logger.LogDebug("Initializing StatementEditSession scratchpad with 0-based coordinate mappings.");
        _statementEditSession.Initialize(preview);


        // STEP 5: RETURN TO UI FOR RENDERING
        // The Avalonia UI binds to this DTO to render dropdown mappings (using 0-based integer indexing)
        // and displays warning badges if required core columns were flagged as undetected (-1 index).
        return preview;
    }

    /// <summary>
    /// Phase 3: Commits a specific staged file. Dispatches background learning and executes SQLite batch import.
    /// </summary>
    public async Task CommitStagedFileAsync(Guid fileId, PreviewTracker confirmedTracker)
    {
        ThrowIfDisposed();
        _logger.LogInformation("Committing import for Staging ID: {FileId}. Corrections to learn: {CorrectionCount}", fileId, confirmedTracker.ColumnCorrections.Count());

        // 1. Thread-safe retrieval via helper (throws KeyNotFoundException automatically if missing)
        var stagedFile = GetStagedFileOrThrow(fileId);

        try
        {
            // 1. DISPATCH NON-BLOCKING BACKGROUND LEARNING TASK
            // Fires strictly upon user confirmation without delaying the import batch
            DispatchBackgroundLearningTask(confirmedTracker.ColumnCorrections);

            // 2. EXECUTE DATABASE IMPORT
            // ExcelStatementImportService applies coordinates and writes to SQLite via ExecuteWithRetryAsync
            _logger.LogInformation("Committing import for Staging ID: {FileId}. Corrections to learn: {CorrectionCount}", fileId, confirmedTracker.ColumnCorrections.Count());
            await _statementImport.ImportConfirmedStatementAsync(stagedFile.Worksheet, confirmedTracker.FinalPreview);

            // 3. CLEAN UP EDIT SESSION
            _statementEditSession.Clear();

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database import failed for Staging ID: {FileId} ({FileName}).", fileId, stagedFile.FileName);
            throw;
        }
        finally
        {
            // 4. RELEASE STREAM & REMOVE FROM STAGING
            // Guarantees workbook streams are disposed and desktop file locks are released, even if the database insert failed!
            DiscardFile(fileId);

        }
    }


    /// <summary>
    /// Dispatches SynonymService learning to a background thread without awaiting,
    /// ensuring zero UI latency during batch import.
    /// </summary>
    private void DispatchBackgroundLearningTask(IEnumerable<ColumnMappingCorrection> corrections)
    {
        var correctionsList = corrections.ToList();
        if (!correctionsList.Any())
        {
            _logger.LogDebug("No column mapping corrections detected. Skipping background synonym learning.");
            return;
        }

        _logger.LogInformation("Dispatching background self-learning task for {Count} confirmed corrections.", correctionsList.Count);

        // Fire-and-forget background execution
        _ = Task.Run(async () =>
        {
            foreach (var correction in correctionsList)
            {
                _logger.LogDebug("Learning synonym: Raw='{Raw}' -> Target='{Target}' (Category: {Category})",
                        correction.RawHeaderName, correction.TargetField, correction.Category);
                try
                {
                    // Pass the properties directly to your service method signature:
                    // rawSynonym -> correction.RawHeaderName
                    // fieldType  -> correction.TargetField (e.g., "Col:Date", stripped inside the service)
                    // category   -> correction.Category
                    await _synonymService.LearnFromCorrectionAsync(
                        correction.RawHeaderName,
                        correction.TargetField,
                        correction.Category
                    );
                }
                catch (Exception ex)
                {
                    // Non-fatal background failure: Log as warning so it doesn't crash the application or rollback import
                    _logger.LogWarning(ex, "Background self-learning failed for raw header '{Raw}' mapped to '{Target}' ({Category}).",
                        correction.RawHeaderName, correction.TargetField, correction.Category);
                    _logger.LogDebug("Exception details: {Exception}", ex);
                }
            }


        });
    }

    /// <summary>
    /// Thread-safe retrieval helper using lock-free dictionary lookup.
    /// </summary>
    private StatementLoadResult GetStagedFileOrThrow(Guid fileId)
    {
        ThrowIfDisposed();
        if (_pendingStatements.TryGetValue(fileId, out var statement))
        {
            return statement;
        }

        _logger.LogWarning("Lookup failed: Staging ID '{FileId}' was not found in active RAM registry.", fileId);
        throw new KeyNotFoundException(
            $"Staged file with ID '{fileId}' was not found. It may have already been committed, discarded, or aborted.");
    }

    /// <summary>
    /// Thread-safe removal and disposal.
    /// This method allows users to discard a staged file from memory if they decide not to proceed with it, ensuring that resources are freed up immediately.
    /// It checks if the file ID exists in the pending statements dictionary, and if so, it disposes of the loaded statement to free up memory and removes it from the dictionary.
    /// This is crucial for managing memory effectively, especially if the user decides to discard large files that were loaded for preview.
    /// TryRemove atomically pulls the item from the dictionary without locking other threads.
    /// </summary>
    public void DiscardFile(Guid fileId)
    {
        if (_pendingStatements.TryRemove(fileId, out var statementToDispose))
        {
            _logger.LogInformation("Discarding Staging ID: {FileId} ('{FileName}'). Releasing OS file lock and RAM stream.", fileId, statementToDispose.FileName);
            statementToDispose.Dispose();
        }
        else
        {
            _logger.LogDebug("Attempted to discard Staging ID: {FileId}, but it was already removed or never existed.", fileId);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(StatementManager));
        }
    }

    /// <summary>
    /// Disposes all pending statements when the session ends or the manager is destroyed.
    /// Prevents memory leaks by ensuring all underlying ClosedXML workbooks and FileStreams are closed.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _logger.LogInformation("Disposing StatementManager. Cleaning up active staging registry...");

        // Atomically pull all remaining staged items out of the dictionary
        var keys = _pendingStatements.Keys.ToList();
        int disposedCount = 0;

        foreach (var key in keys)
        {
            if (_pendingStatements.TryRemove(key, out var statement))
            {
                statement.Dispose();
                disposedCount++;
            }
        }

        if (disposedCount > 0)
        {
            _logger.LogInformation("Disposed {Count} orphaned workbook streams during manager shutdown.", disposedCount);
        }

        _statementEditSession.Clear();
        GC.SuppressFinalize(this);
    }
}