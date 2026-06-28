using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

public class StatementPreview
{
    public string FilePath { get; set; } = "";

    public Account AccountInfo { get; set; } = new();

    public int HeaderRow { get; set; }

    public DetectedField DateField { get; set; } = new();

    public DetectedField DescriptionField { get; set; } = new();

    public DetectedField DebitField { get; set; } = new();

    public DetectedField CreditField { get; set; } = new();

    public DetectedField AmountField { get; set; } = new();

    public string HeaderSignature { get; set; } = "";

    public List<TransactionPreview> PreviewTransactions { get; set; } = new();

    public int ConfidenceScore { get; set; }

    public bool RequiresVerification { get; set; }
}