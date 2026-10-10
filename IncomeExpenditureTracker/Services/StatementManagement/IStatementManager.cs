using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public interface IStatementManager : IDisposable
{
    bool HasStagedFiles { get; }
    void DiscardAllFiles();
    void DiscardFile(Guid fileId);
    Task<StagingBatchResult> StageFilesAsync(List<string> filePaths, IProgress<LoadingProgress> progress, CancellationToken ct = default);
    Task<StatementPreview> PreviewStagedFileAsync(Guid fileId, string? targetSheetName = null, CancellationToken ct = default);
    Task CommitStagedBatchAsync(Guid fileId, IEnumerable<PreviewTracker> confirmedTrackers, CancellationToken ct = default);
}