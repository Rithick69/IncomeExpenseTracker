using System;
using System.Linq;
using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

/// <summary>
/// This record represents a pending file that has been uploaded by the user and is awaiting analysis and preview generation.
/// It contains the file name and the list of sheet names (for Excel files) to be displayed in the preview UI before the user confirms the import.
/// </summary>
public record PendingFilePreview(Guid Id, string FileName, List<string> SheetNames);

/// <summary>
/// Represents a file that failed to load during the staging phase, containing details for UI notifications.
/// </summary>
public class FileStagingError
{
    public Guid FileId { get; }
    public string FileName { get; }
    public ErrorSeverity Severity { get; }
    public string Message { get; }
    public Exception? Exception { get; }

    public FileStagingError(Guid fileId, string fileName, ErrorSeverity severity, string message, Exception? exception = null)
    {
        FileId = fileId;
        FileName = fileName;
        Severity = severity;
        Message = message;
        Exception = exception;
    }
}

/// <summary>
/// A container returned by StageFilesAsync holding both successfully staged files and any loading failures.
/// </summary>
public class StagingBatchResult
{
    public List<PendingFilePreview> Successes { get; init; } = new();
    public List<FileStagingError> Failures { get; init; } = new();

    // Helper property for the UI ViewModel to check if any notifications need to be shown
    public bool HasFailures => Failures.Any();
}