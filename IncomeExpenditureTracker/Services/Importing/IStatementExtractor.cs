using IncomeExpenditureTracker.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace IncomeExpenditureTracker.Services.Importing;

public interface IStatementExtractor<in TDocument>
{
    Task<StatementPreview> Analyze(TDocument document, string sourceFileName, bool forceReload = false, CancellationToken ct = default);

    Task<StatementPreview> ReExtractAsync(TDocument document, string sourceFileName, int headerRow, Dictionary<string, DetectedField> updatedFields, List<string> originalRawHeaders, CancellationToken ct = default);
}