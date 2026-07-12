using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public interface IStatementEditSession
{
    /// <summary>
    /// Seeds the edit session with the object returned by ExcelStatementExtractor.
    /// </summary>
    void Initialize(StatementPreview preview);

    /// <summary>
    /// Returns the live, currently-edited preview for UI binding.
    /// </summary>
    StatementPreview GetCurrentPreview();

    /// <summary>
    /// Updates a DetectedField in the dictionary by its namespaced key(e.g., "Col:Date")
    /// in the live preview and logs a ColumnMappingCorrection for self-learning upon confirmation.
    /// </summary>
    void UpdateColumnMapping(string targetField, string rawHeaderName, int newColumnIndex, string category);

    /// <summary>
    /// Bundles the modified StatementPreview and all confirmed corrections for StatementManager.
    /// </summary>
    PreviewTracker ConfirmAndPrepareForImport();

    /// <summary>
    /// Clears the session state from memory when cancelled or completed.
    /// </summary>
    void Clear();
}
