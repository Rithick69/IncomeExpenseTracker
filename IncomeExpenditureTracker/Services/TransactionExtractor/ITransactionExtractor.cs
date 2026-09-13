using System.Collections.Generic;
using System.Threading;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.TransactionExtractor;

public interface ITransactionExtractor<in TDocument>
{
    public List<TransactionPreview> ExtractPreview(TDocument document, int headerRow, Dictionary<string, DetectedField> columnFields, CancellationToken ct = default);
    public List<Transaction> ExtractTransactions(TDocument document, int headerRow, Dictionary<string, DetectedField> previewFields, CancellationToken ct = default);
}