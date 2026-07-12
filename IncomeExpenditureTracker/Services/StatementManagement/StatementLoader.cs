using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.StatementManagement;

// This file imports excel or csv or pdf statements to be returned to System Extractor for preview and then to be imported to the database after user confirmation.

// 1. Define the Result Object
// We return a custom object so we can pass both the Workbook and Worksheet together.
// By making it IDisposable, we ensure the caller can clean up the memory later.


// This class encapsulates the result of loading a statement file, including the workbook and worksheet objects.
// It implements IDisposable to ensure that resources are properly released after use, preventing memory leaks.
public class StatementLoadResult : IDisposable
{
    // By removing 'set', these properties become read-only after creation
    public IXLWorkbook Workbook { get; }
    public IXLWorksheet Worksheet { get; }

    public string FileName { get; set; } = string.Empty; // Store the file name for reference, useful for logging or user feedback.

    private readonly Stream _fileStream; // We keep the file stream open as long as the workbook is in use to prevent memory issues with large files.

    // The constructor guarantees they are populated immediately
    public StatementLoadResult(IXLWorkbook workbook, IXLWorksheet worksheet, string FileName, Stream fileStream)
    {
        Workbook = workbook ?? throw new ArgumentNullException(nameof(workbook));
        Worksheet = worksheet ?? throw new ArgumentNullException(nameof(worksheet));
        FileName = FileName ?? throw new ArgumentNullException(nameof(FileName));
        _fileStream = fileStream ?? throw new ArgumentNullException(nameof(fileStream));
    }

    public void Dispose()
    {
        // This safely closes the file stream and frees up memory
        Workbook?.Dispose();
        _fileStream?.Dispose();
    }
}

public class StatementLoader : IStatementLoader
{

    public async Task<StatementLoadResult> LoadStatementAsync(string filePath, IProgress<LoadingProgress> progress = null!)
    {
        // This method will return workbook and worksheet objects to be used by the StatementExtractor for analysis and preview generation.

        // The actual implementation will depend on the libraries used for reading Excel, CSV, and PDF files.

        // Step 1: Validate the file path and extension
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The file at {filePath} was not found.");

        var extension = Path.GetExtension(filePath).ToLower();
        if (extension != ".xlsx")
            throw new NotSupportedException($"Currently, only .xlsx files are supported. Provided: {extension}");

        ValidateFilePath(filePath);
        ValidateExtension(filePath);

        try
        {
            progress?.Report(new LoadingProgress { Percentage = 25, Message = "Opening file stream..." });

            // Step 2: Safely open the file
            // FileShare.ReadWrite is crucial for desktop apps. It prevents crashes
            // if the user currently has the file open in Excel.
            // Added `useAsync: true` and a buffer size of 4096 for true asynchronous disk reads
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

            progress?.Report(new LoadingProgress { Percentage = 75, Message = "Reading workbook data..." });

            // Step 3: Centralize ClosedXML access
            // ClosedXML doesn't have an async constructor, so we wrap it in Task.Run
            // to ensure it doesn't block the UI thread while parsing large files into memory.
            var workbook = await Task.Run(() => new XLWorkbook(stream));

            // Step 4: Select the worksheet
            // For now, we assume the first worksheet is the target.
            // Future improvement: check for a specific sheet name or let the user choose.
            var worksheet = workbook.Worksheets.First();

            progress?.Report(new LoadingProgress { Percentage = 100, Message = "Statement loaded." });

            // Step 5: Return the combined result
            return new StatementLoadResult(workbook, worksheet, Path.GetFileName(filePath), stream);
        }
        catch (IOException ex)
        {
            // Catching IOException specifically helps identify if the file is locked
            // by a strict external process that prevents even shared reading.
            throw new InvalidOperationException($"Could not open the statement file. Details: {ex.Message}", ex);
        }

    }

    // --- For Future Enhancement ---
    // In the future, we can expand this service to support multiple sheets and file types.
    // For example, we could add a method to list all sheets in the workbook and allow the user to select which one to analyze.public async Task<List<SheetMetaData>> GetAvailableSheetsAsync(string filePath)
    public async Task<List<SheetMetaData>> GetAvailableSheetsAsync(string filePath)
    {
        ValidateFilePath(filePath);
        ValidateExtension(filePath);

        var sheets = new List<SheetMetaData>();

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var workbook = await Task.Run(() => new XLWorkbook(stream));

            foreach (var sheet in workbook.Worksheets)
            {
                sheets.Add(new SheetMetaData
                {
                    FilePath = filePath,
                    SheetName = sheet.Name,
                    Position = sheet.Position,
                    IsEmpty = sheet.IsEmpty()
                });
            }

            return sheets;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Could not read sheets from file. Details: {ex.Message}", ex);
        }
    }

    public async Task<StatementLoadResult> LoadSpecificSheetAsync(string filePath, string sheetName, IProgress<LoadingProgress> progress = null!)
    {
        ValidateFilePath(filePath);
        ValidateExtension(filePath);

        if (string.IsNullOrWhiteSpace(sheetName))
            throw new ArgumentException("Sheet name must be provided.", nameof(sheetName));

        try
        {
            progress?.Report(new LoadingProgress { Percentage = 20, Message = "Opening file stream..." });
            var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);

            progress?.Report(new LoadingProgress { Percentage = 60, Message = "Reading workbook..." });
            var workbook = await Task.Run(() => new XLWorkbook(stream));

            if (!workbook.TryGetWorksheet(sheetName, out var worksheet))
            {
                workbook.Dispose();
                stream.Dispose();
                throw new ArgumentException($"Worksheet '{sheetName}' was not found in the workbook.");
            }

            progress?.Report(new LoadingProgress { Percentage = 100, Message = $"Sheet {sheetName} loaded." });
            return new StatementLoadResult(workbook, worksheet, Path.GetFileName(filePath), stream);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Could not open the statement file. Details: {ex.Message}", ex);
        }
    }

    // A private helper to keep things DRY (Don't Repeat Yourself)
    private void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"The file at {filePath} was not found.");
    }

    private void ValidateExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        if (extension != ".xlsx")
            throw new NotSupportedException($"Currently, only .xlsx files are supported. Provided: {extension}");
    }
}