using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Importing;

namespace IncomeExpenditureTracker.Services.Importing.Strategies;

public class ExcelParserStrategy : IFileParserStrategy
{
    private IXLWorkbook? _workbook;
    private readonly IStatementExtractor<IXLWorksheet> _extractor;
    private readonly IStatementImport<IXLWorkbook> _importer;
    private string _fileName = string.Empty;
    private Guid _fileId;

    public ExcelParserStrategy(
        IStatementExtractor<IXLWorksheet> extractor,
        IStatementImport<IXLWorkbook> importer)
    {
        _extractor = extractor;
        _importer = importer;
    }

    public async Task<PendingFilePreview> LoadAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        _fileName = fileName;
        _fileId = Guid.NewGuid();

        _workbook = await Task.Run(() => new XLWorkbook(stream), ct);

        // Strictly filter out empty sheets to prevent useless UI entries
        var sheetNames = _workbook.Worksheets
            .Where(w => w.LastCellUsed() != null)
            .Select(w => w.Name)
            .ToList();

        return new PendingFilePreview(_fileId, fileName, sheetNames);
    }

    public async Task<StatementPreview> GeneratePreviewAsync(string sheetName, CancellationToken ct = default)
    {
        if (_workbook == null) throw new InvalidOperationException("Workbook not loaded");

        IXLWorksheet? worksheet = null;

        Console.WriteLine($"Requested sheet name: '{sheetName}'");

        // BUG FIX: Handle auto-routing where sheetName is null or empty
        if (string.IsNullOrWhiteSpace(sheetName))
        {
            // Default to the first sheet that actually has data
            worksheet = _workbook.Worksheets.FirstOrDefault(w => w.LastCellUsed() != null)
                        ?? _workbook.Worksheets.FirstOrDefault();
            Console.WriteLine($"Auto-selected sheet: '{worksheet?.Name}'");

            if (worksheet == null)
            {
                throw new InvalidOperationException("The workbook contains no valid worksheets.");
            }
        }
        else
        {
            Console.WriteLine($"Attempting to find sheet: '{sheetName}'");
            // Try to get the specific sheet requested by the user
            if (!_workbook.Worksheets.TryGetWorksheet(sheetName, out worksheet))
            {
                throw new ArgumentException($"Sheet '{sheetName}' not found in the workbook.");
            }
        }

        Console.WriteLine($"Using sheet: '{worksheet.Name}' for analysis.");

        // Now safely pass the resolved worksheet to the extractor
        return await _extractor.Analyze(worksheet, _fileName, ct: ct);
    }

    public async Task<StatementPreview> RegeneratePreviewAsync(string sheetName, int headerRow, Dictionary<string, DetectedField> updatedFields, List<string> originalRawHeaders, CancellationToken ct = default)
    {
        if (_workbook == null) throw new InvalidOperationException("Workbook not loaded");

        // Safely resolve the worksheet
        var worksheet = string.IsNullOrWhiteSpace(sheetName)
            ? (_workbook.Worksheets.FirstOrDefault(w => w.LastCellUsed() != null) ?? _workbook.Worksheets.First())
            : _workbook.Worksheets.Worksheet(sheetName);

        return await _extractor.ReExtractAsync(worksheet, _fileName, headerRow, updatedFields, originalRawHeaders, ct);
    }

    public async Task ImportConfirmedBatchAsync(PreviewTracker trackers, CancellationToken ct = default)
    {
        if (_workbook == null) throw new InvalidOperationException("Workbook not loaded");

        // The importer should be updated to accept multiple trackers.
        // For now, loop through them if the underlying interface isn't updated,
        // but the task asks to update it. We will call the updated method.
        await _importer.ImportConfirmedBatchAsync(_workbook, trackers, ct);
    }

    public void Dispose()
    {
        _workbook?.Dispose();
    }
}