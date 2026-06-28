using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClosedXML.Excel;
using System.Threading.Tasks;
using System.Collections.Generic;

using IncomeExpenditureTracker.Models;
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
    private readonly Dictionary<Guid, StatementLoadResult> _pendingStatements = new();

    private readonly IStatementImport<IXLWorksheet> _statementImport;
    private readonly IStatementExtractor<IXLWorksheet> _statementExtractor;

    public StatementManager(
        IStatementLoader statementLoader,
        IStatementExtractor<IXLWorksheet> statementExtractor,
        IStatementImport<IXLWorksheet> statementImport
        )
    {
        _statementLoader = statementLoader;
        _statementExtractor = statementExtractor;
        _statementImport = statementImport;
    }

    // This method stages multiple files for preview by loading them asynchronously and providing progress updates.
    // It enforces a maximum limit of 5 files per session to prevent excessive memory usage
    // and returns a list of PendingFilePreview objects that contain the file name and sheet names for each loaded file, which can be displayed in the UI for user confirmation before import.

    public async Task<List<PendingFilePreview>> StageFilesAsync(List<string> filePaths, IProgress<LoadingProgress> progress)
    {
        if (filePaths.Count > 5)
            throw new InvalidOperationException("Maximum limit of 5 files per session exceeded.");

        int totalFiles = filePaths.Count;
        int completedFiles = 0;

        progress?.Report(new LoadingProgress { Percentage = 0, Message = $"Staging {totalFiles} files..." });

        // Load all files concurrently and track progress
        // We use Task.WhenAll to load all files in parallel, which can significantly reduce the time taken to stage multiple files, especially if they are large.
        // Each file is loaded using the StatementLoader, and as each file is successfully loaded, we update the progress to reflect how many files have been staged so far.
        // Each loaded file is stored in the _pendingStatements dictionary with a unique GUID, allowing us to manage them effectively and provide the necessary information for the preview UI.

        var loadTasks = filePaths.Select(async path =>
        {
            // Load the file asynchronously
            var result = await _statementLoader.LoadStatementAsync(path, progress: null);

            var fileId = Guid.NewGuid();
            var fileName = Path.GetFileName(path);
            var sheetNames = new List<string> { result.Worksheet.Name }; // Or fetch all sheets if modified

            // Safely store the heavy result in memory
            lock (_pendingStatements)
            {
                _pendingStatements[fileId] = result;
            }

            // Update progress after each file is staged

            int currentCompleted = Interlocked.Increment(ref completedFiles);
            progress?.Report(new LoadingProgress
            {
                Percentage = (currentCompleted * 100) / totalFiles,
                Message = $"Staged {currentCompleted} of {totalFiles} files..."
            });

            return new PendingFilePreview(fileId, fileName, sheetNames);
        });

        var results = await Task.WhenAll(loadTasks);
        return results.ToList();
    }

    // This method allows users to discard a staged file from memory if they decide not to proceed with it, ensuring that resources are freed up immediately.
    // It checks if the file ID exists in the pending statements dictionary, and if so, it disposes of the loaded statement to free up memory and removes it from the dictionary.
    // This is crucial for managing memory effectively, especially if the user decides to discard large files that were loaded for preview.

    public void DiscardFile(Guid fileId)
    {
        if (_pendingStatements.TryGetValue(fileId, out var statement))
        {
            statement.Dispose();
            lock (_pendingStatements) { _pendingStatements.Remove(fileId); }
        }
    }

    // This method is called when the StatementManager is disposed, which typically happens at the end of a user session or when the application is closing.
    // It iterates through all pending statements that are still in memory, disposes of them to free up resources, and clears the dictionary to ensure that no references to the loaded statements remain.

    public void Dispose()
    {
        // Safety net: clean up all memory if the session manager is destroyed
        foreach (var statement in _pendingStatements.Values)
        {
            statement.Dispose();
        }
        _pendingStatements.Clear();
    }
}