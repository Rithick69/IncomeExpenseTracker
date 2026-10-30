using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Importing.Strategies;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public class StatementManager : IStatementManager
{
    private readonly Func<string, IFileParserStrategy> _strategyFactory;
    private readonly Func<IStatementEditSession> _editSessionFactory;
    private readonly ILogger<StatementManager> _logger;
    private readonly IApplicationBroker _broker;
    private readonly ISynonymService _synonymService;
    private readonly IImportBatchService _importBatchService;

    // Concurrency control for limiting max concurrent file operations (Load/Preview)
    private readonly SemaphoreSlim _concurrencySemaphore = new SemaphoreSlim(5, 5);

    // Lock-free concurrent storage for active parsing strategies
    private readonly ConcurrentDictionary<Guid, IFileParserStrategy> _activeStrategies = new();

    // Dictionary to keep track of the transient edit sessions per file
    private readonly ConcurrentDictionary<Guid, IStatementEditSession> _activeSessions = new();

    private readonly ConcurrentDictionary<Guid, PendingFilePreview> _stagedPreviews = new();

    private volatile bool _isDisposed;

    public StatementManager(
        Func<string, IFileParserStrategy> strategyFactory,
        Func<IStatementEditSession> editSessionFactory,
        ISynonymService synonymService,
        IImportBatchService importBatchService,
        ILogger<StatementManager> logger,
        IApplicationBroker broker)
    {
        _strategyFactory = strategyFactory ?? throw new ArgumentNullException(nameof(strategyFactory));
        _editSessionFactory = editSessionFactory ?? throw new ArgumentNullException(nameof(editSessionFactory));
        _synonymService = synonymService ?? throw new ArgumentNullException(nameof(synonymService));
        _importBatchService = importBatchService ?? throw new ArgumentNullException(nameof(importBatchService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
    }

    public IEnumerable<PendingFilePreview> GetStagedFiles()
    {
        return _stagedPreviews.Values.ToList();
    }

    public async Task<StagingBatchResult> StageFilesAsync(List<string> filePaths, IProgress<LoadingProgress> progress, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(filePaths);

        // Guard against empty lists to prevent DivideByZeroException
        if (filePaths.Count == 0)
        {
            return new StagingBatchResult
            {
                Successes = new List<PendingFilePreview>(),
                Failures = new List<FileStagingError>()
            };
        }

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

        string startMessage = $"Staging {totalFiles} files...";
        progress?.Report(new LoadingProgress { Percentage = 0, Message = startMessage });

        // Broadcast the start of the process to the UI
        _broker.Send(new StagingProgressMessage(0, startMessage));

        var loadTasks = filePaths.Select(async path =>
        {
            ct.ThrowIfCancellationRequested();
            await _concurrencySemaphore.WaitAsync(ct);

            var fileId = Guid.NewGuid();
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            IFileParserStrategy strategy = null!;
            FileStream stream = null!;

            try
            {
                // 1. CHECK FOR DUPLICATES FIRST
                bool isDuplicate = await _importBatchService.GetIdByFileName(fileName, ct) > 0;

                if (isDuplicate)
                {
                    var errorMsg = $"An import file named '{fileName}' has already been processed.";
                    _logger.LogWarning(errorMsg);

                    var error = new FileStagingError(fileId, fileName, ErrorSeverity.Fatal, errorMsg, null);
                    failures.Add(error);

                    // Broadcast both a file-specific error and a general UI toast notification
                    _broker.Send(new FileStagingErrorMessage(error));

                    return; // Skip loading this duplicate file, let it count as a failure natively
                }


                strategy = _strategyFactory(extension);

                // Open stream
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

                var preview = await strategy.LoadAsync(stream, fileName, ct);

                // Override fileId with the one generated by the strategy
                fileId = preview.Id;

                _activeStrategies.TryAdd(fileId, strategy);
                _stagedPreviews.TryAdd(fileId, preview);

                if (_isDisposed)
                {
                    _logger.LogWarning("Manager was disposed while loading '{FileName}'. Disposing strategy immediately.", fileName);
                    DiscardFile(fileId);
                    return;
                }

                _logger.LogDebug("Successfully staged '{FileName}'.", fileName);
                successes.Add(preview);
            }
            catch (OperationCanceledException)
            {
                stream?.Dispose();
                strategy?.Dispose();
                DiscardFile(fileId);
                throw;
            }
            catch (IOException ex)
            {
                stream?.Dispose();
                strategy?.Dispose();
                _logger.LogWarning(ex, "OS file lock encountered on '{FileName}'.", fileName);
                var error = new FileStagingError(fileId, fileName, ErrorSeverity.Warning,
                    "This file is currently locked by another program (e.g., Excel). Please close it and try again.", ex);
                failures.Add(error);
                _broker.Send(new FileStagingErrorMessage(error));
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                strategy?.Dispose();
                _logger.LogError(ex, "Catastrophic failure staging '{FileName}'.", fileName);
                var error = new FileStagingError(fileId, fileName, ErrorSeverity.Fatal,
                    "An unexpected error occurred while reading the file. It may be corrupted or unsupported.", ex);
                failures.Add(error);
                _broker.Send(new FileStagingErrorMessage(error));
            }
            finally
            {
                _concurrencySemaphore.Release();

                int currentCompleted = Interlocked.Increment(ref completedFiles);
                int currentPercentage = currentCompleted * 100 / totalFiles;
                string progressMsg = $"Processed {currentCompleted} of {totalFiles} files...";

                progress?.Report(new LoadingProgress
                {
                    Percentage = currentPercentage,
                    Message = progressMsg
                });

                _broker.Send(new StagingProgressMessage(currentPercentage, progressMsg));
            }
        });

        try
        {
            await Task.WhenAll(loadTasks);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Staging operation was cancelled. Purging partially loaded files from memory.");
            foreach (var success in successes)
            {
                DiscardFile(success.Id);
            }
            throw;
        }

        _logger.LogInformation("Staging batch completed. Successes: {SuccessCount}, Failures: {FailureCount}.", successes.Count, failures.Count);
        _broker.Send(new StagingBatchCompletedMessage(successes.Count, failures.Count));

        return new StagingBatchResult
        {
            Successes = successes.ToList(),
            Failures = failures.ToList()
        };
    }

    public async Task<StatementPreview> PreviewStagedFileAsync(Guid fileId, string? targetSheetName = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        _logger.LogInformation("Generating preview for Staging ID: {FileId}. Target Sheet: '{SheetName}'", fileId, targetSheetName ?? "Default Primary");

        var strategy = GetStrategyOrThrow(fileId);
        var _statementEditSession = _editSessionFactory();
        _activeSessions[fileId] = _statementEditSession;

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            return await Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested();

                _logger.LogDebug("Starting preview generation for Staging ID: {FileId}.", fileId);

                StatementPreview preview = await strategy.GeneratePreviewAsync(targetSheetName ?? string.Empty, ct);

                ct.ThrowIfCancellationRequested();

                // INITIALIZE THE IN-MEMORY EDITING SCRATCHPAD
                // Before returning to the UI, we hand off the generated preview DTO to the StatementEditSession.
                // This session acts as a lightweight "shopping cart" that will hold user column re-mappings,
                // tag overrides, and row exclusions in memory until explicit commit confirmation.
                // Per our rules, this initialization executes ZERO SQLite database writes.
                _statementEditSession.Initialize(preview, preserveCorrections: false);

                return preview;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            DiscardFile(fileId);
            throw;
        }
        catch (Exception ex)
        {
            DiscardFile(fileId);
            var error = new FileStagingError(fileId, "Unknown", ErrorSeverity.Fatal,
                $"Failed to analyze the document. The file structure may be severely corrupted.", ex);
            _broker.Send(new FileStagingErrorMessage(error));
            throw new InvalidOperationException($"Failed to analyze the document.", ex);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    // Add this new method so the UI can retrieve the active scratchpad
    public IStatementEditSession GetEditSession(Guid fileId)
    {
        ThrowIfDisposed();
        if (_activeSessions.TryGetValue(fileId, out var session))
        {
            return session;
        }
        throw new KeyNotFoundException($"No active edit session found for Staging ID '{fileId}'.");
    }

    public async Task<StatementPreview> RegeneratePreviewAsync(Guid fileId, string targetSheetName, int headerRow, Dictionary<string, DetectedField> updatedFields, List<string> originalRawHeaders, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var strategy = GetStrategyOrThrow(fileId);

        await _concurrencySemaphore.WaitAsync(ct);
        try
        {
            var newPreview = await strategy.RegeneratePreviewAsync(targetSheetName, headerRow, updatedFields, originalRawHeaders, ct);

            // Update the active session scratchpad with the new results
            if (_activeSessions.TryGetValue(fileId, out var session))
            {
                session.Initialize(newPreview, preserveCorrections: true);
            }

            return newPreview;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Regeneration was cancelled for Staging ID: {FileId}.", fileId);
            DiscardFile(fileId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Catastrophic failure during preview regeneration for Staging ID: {FileId}.", fileId);
            DiscardFile(fileId);

            var error = new FileStagingError(fileId, "Unknown", ErrorSeverity.Fatal,
                "Failed to regenerate the document preview. The file may have been moved or corrupted.", ex);

            _broker.Send(new FileStagingErrorMessage(error));
            throw new InvalidOperationException("Failed to regenerate the document preview.", ex);
        }
        finally
        {
            _concurrencySemaphore.Release();
        }
    }

    public async Task CommitStagedBatchAsync(Guid fileId, PreviewTracker confirmedTrackers, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var strategy = GetStrategyOrThrow(fileId);

        if (!_activeSessions.TryGetValue(fileId, out IStatementEditSession? _statementEditSession))
        {
            _logger.LogWarning("No active edit session found for Staging ID: {FileId}.", fileId);
            throw new InvalidOperationException("Cannot commit because the edit session was lost or never initialized.");
        }

        try
        {

            DispatchBackgroundLearningTask(confirmedTrackers.ColumnCorrections);


            await strategy.ImportConfirmedBatchAsync(confirmedTrackers, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Database import was cancelled for Staging ID: {FileId}.", fileId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database import failed for Staging ID: {FileId}.", fileId);
            throw;
        }
        finally
        {
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
        _logger.LogInformation("Count: {Count}", correctionsList.Count());
        if (!correctionsList.Any()) return;

        _ = Task.Run(async () =>
        {
            foreach (var correction in correctionsList)
            {
                _logger.LogInformation("Correction1: {correction}", correction);
                try
                {
                    // 1. HARD BYPASS FOR PLACEHOLDERS
                    // Do not learn synonyms for our dynamically injected blank columns.
                    if (!string.IsNullOrWhiteSpace(correction.RawHeaderName) &&
                        correction.RawHeaderName.StartsWith("[Column ") &&
                        correction.RawHeaderName.EndsWith(" - Blank]"))
                    {
                        continue;
                    }

                    // 2. Normal synonym learning logic continues below...
                    // Pass the properties directly to your service method signature:
                    // rawSynonym -> correction.RawHeaderName
                    // fieldType  -> correction.TargetField (e.g., "Col:Date", stripped inside the service)
                    // category   -> correction.Category
                    _logger.LogInformation("Corrections: {RawHeaderName}, {TargetField}, {Category}", correction.RawHeaderName,
                        correction.TargetField,
                        correction.Category);
                    await _synonymService.LearnFromCorrectionAsync(
                        correction.RawHeaderName,
                        correction.TargetField,
                        correction.Category,
                        CancellationToken.None
                    );
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Background self-learning failed.");
                }
            }
        });
    }

    private IFileParserStrategy GetStrategyOrThrow(Guid fileId)
    {
        ThrowIfDisposed();
        if (_activeStrategies.TryGetValue(fileId, out var strategy))
        {
            return strategy;
        }

        throw new KeyNotFoundException($"Staged file with ID '{fileId}' was not found.");
    }

    public void DiscardFile(Guid fileId)
    {
        if (_activeSessions.TryRemove(fileId, out var sessionToDispose))
        {
            sessionToDispose.Clear();
        }

        if (_activeStrategies.TryRemove(fileId, out var strategy))
        {
            strategy.Dispose();
        }

        _stagedPreviews.TryRemove(fileId, out _);
    }

    public bool HasStagedFiles => !_activeStrategies.IsEmpty;

    public void DiscardAllFiles()
    {
        foreach (var key in _activeStrategies.Keys.ToList())
        {
            DiscardFile(key);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(StatementManager));
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var key in _activeStrategies.Keys.ToList())
        {
            if (_activeStrategies.TryRemove(key, out var strategy))
            {
                strategy.Dispose();
            }
        }

        foreach (var session in _activeSessions.Values)
        {
            session.Clear();
        }
        _activeSessions.Clear();
        _concurrencySemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}