using System;

namespace IncomeExpenditureTracker.Models;

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