using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

/// <summary>
/// Tracks when a user changes a column mapping during verification.
/// Used by StatementManager to trigger SynonymService.LearnFromCorrectionAsync().
/// </summary>
public class ColumnMappingCorrection
{
    public string TargetField { get; set; } = ""; // e.g., "Col:Date", "Col:Description", "Col:Amount"
    public string RawHeaderName { get; set; } = ""; // The original header found in Excel (e.g., "TXN_DATE")
    public int NewColumnIndex { get; set; } = -1; // The new column index selected by the user in the UI

    public string Category { get; set; } = ""; // The domain category of the mapped field(e.g., "TRANSACTION", "METADATA"), ensuring strict isolation during synonym learning.
}

/// <summary>
/// Returned when the user confirms their edits. Contains the modified preview
/// ready for import, plus the learning instructions for the manager.
/// </summary>
public class PreviewTracker
{
    public StatementPreview FinalPreview { get; set; } = new();
    public List<ColumnMappingCorrection> ColumnCorrections { get; set; } = new();
}
