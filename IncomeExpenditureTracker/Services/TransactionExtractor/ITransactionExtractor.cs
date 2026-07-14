using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.TransactionExtractor;

public interface ITransactionExtractor<in TDocument>
{
    public List<TransactionPreview> ExtractPreview(TDocument document, int headerRow, Dictionary<string, DetectedField> columnFields);
    public List<Transaction> ExtractTransactions(TDocument document, int headerRow, Dictionary<string, DetectedField> previewFields);
}