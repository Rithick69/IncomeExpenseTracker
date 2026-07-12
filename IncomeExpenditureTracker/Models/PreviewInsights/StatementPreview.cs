using System;
using System.Collections.Generic;
namespace IncomeExpenditureTracker.Models;

public class StatementPreview
{
    public string FileName { get; set; } = "";
    public int HeaderRow { get; set; }

    /// <summary>
    /// ONE dictionary holding ALL extracted metadata and column coordinates.
    /// Key = Standard Domain Name (e.g., "Date", "Description", "AccountNumber", "EntityName").
    /// </summary>
    public Dictionary<string, DetectedField> Fields { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string HeaderSignature { get; set; } = "";

    public List<TransactionPreview> PreviewTransactions { get; set; } = new();

    public int ConfidenceScore { get; set; }

    public bool RequiresVerification { get; set; }

    // public DetectedField DateField { get; set; } = new();

    // public DetectedField DescriptionField { get; set; } = new();

    // public DetectedField DebitField { get; set; } = new();

    // public DetectedField CreditField { get; set; } = new();

    // public DetectedField AmountField { get; set; } = new();
    // public Account AccountInfo { get; set; } = new();
}
