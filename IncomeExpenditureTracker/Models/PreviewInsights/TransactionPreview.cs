using System;
public class TransactionPreview
{
    public DateTime Date { get; set; }

    public string Description { get; set; } = "";

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }

    public decimal Amount { get; set; }
}