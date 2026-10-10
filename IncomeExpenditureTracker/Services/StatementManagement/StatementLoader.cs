using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using System.Threading;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public class StatementLoadResult : IDisposable
{
    public string FileName { get; }
    public Stream FileStream { get; }

    public StatementLoadResult(string fileName, Stream fileStream)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        FileStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
    }

    public void Dispose()
    {
        FileStream?.Dispose();
    }
}

public class StatementLoader : IStatementLoader
{
    public async Task<StatementLoadResult> LoadStatementAsync(string filePath, IProgress<LoadingProgress> progress = null!, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("[StatementLoader] File path cannot be empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"[StatementLoader] The file at {filePath} was not found.");

        progress?.Report(new LoadingProgress { Percentage = 25, Message = "Opening file stream..." });

        try
        {
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

            ct.ThrowIfCancellationRequested();
            progress?.Report(new LoadingProgress { Percentage = 100, Message = "Statement loaded." });

            return new StatementLoadResult(Path.GetFileName(filePath), stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"[StatementLoader] Could not open the statement file. Details: {ex.Message}", ex);
        }
    }

    public async Task<StatementLoadResult> LoadSpecificSheetAsync(string filePath, string sheetName, IProgress<LoadingProgress> progress = null!)
    {
        // Strategy handles sheet specificity during generation now.
        return await LoadStatementAsync(filePath, progress);
    }
}