using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.PreviewInsights;

public interface IConfidenceService
{
    int CalculateConfidence(
        Dictionary<string, DetectedField> unifiedFields,
        List<TransactionPreview> previewTransactions);
}