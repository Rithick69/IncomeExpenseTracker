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

        var sheetNames = _workbook.Worksheets.Select(w => w.Name).ToList();
        return new PendingFilePreview(_fileId, fileName, sheetNames);
    }

    public async Task<StatementPreview> GeneratePreviewAsync(string sheetName, CancellationToken ct = default)
    {
        if (_workbook == null) throw new InvalidOperationException("Workbook not loaded");

        if (!_workbook.Worksheets.TryGetWorksheet(sheetName, out var worksheet))
        {
            throw new ArgumentException($"Sheet {sheetName} not found");
        }

        return await _extractor.Analyze(worksheet, _fileName, ct: ct);
    }

    public async Task ImportConfirmedBatchAsync(IEnumerable<PreviewTracker> trackers, CancellationToken ct = default)
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
