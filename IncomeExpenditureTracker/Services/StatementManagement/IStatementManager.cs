using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public interface IStatementManager : IDisposable
{
    IEnumerable<PendingFilePreview> GetStagedFiles();
    bool HasStagedFiles { get; }
    void DiscardAllFiles();
    void DiscardFile(Guid fileId);
    Task<StagingBatchResult> StageFilesAsync(List<string> filePaths, IProgress<LoadingProgress> progress, CancellationToken ct = default);
    Task<StatementPreview> PreviewStagedFileAsync(Guid fileId, string? targetSheetName = null, CancellationToken ct = default);
    Task CommitStagedBatchAsync(Guid fileId, PreviewTracker confirmedTrackers, CancellationToken ct = default);
    IStatementEditSession GetEditSession(Guid fileId);

    Task<StatementPreview> RegeneratePreviewAsync(Guid fileId, string targetSheetName, int headerRow, Dictionary<string, DetectedField> updatedFields, List<string> originalRawHeaders, CancellationToken ct = default);
}