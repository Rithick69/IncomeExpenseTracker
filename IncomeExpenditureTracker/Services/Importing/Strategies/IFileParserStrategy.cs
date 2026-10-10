using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Importing.Strategies;

/// <summary>
/// Defines a unified strategy for parsing different statement file types (Excel, CSV, PDF).
/// Acts as a Facade to keep underlying parsing libraries (e.g., ClosedXML) hidden from the domain.
/// </summary>
public interface IFileParserStrategy : IDisposable
{
    /// <summary>
    /// Loads a file from a stream and initializes the internal parsing state.
    /// Returns a clean DTO with file metadata.
    /// </summary>
    Task<PendingFilePreview> LoadAsync(Stream stream, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Generates a preview for a specific sheet/section of the loaded file.
    /// </summary>
    Task<StatementPreview> GeneratePreviewAsync(string sheetName, CancellationToken ct = default);

    /// <summary>
    /// Imports a batch of confirmed sheets under a single transaction.
    /// </summary>
    Task ImportConfirmedBatchAsync(IEnumerable<PreviewTracker> trackers, CancellationToken ct = default);
}
