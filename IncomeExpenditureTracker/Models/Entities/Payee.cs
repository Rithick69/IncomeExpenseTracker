using System;
namespace IncomeExpenditureTracker.Models;

public class Payee
{
    public long Id { get; set; }

    /// <summary>
    /// The user-facing name of the merchant (e.g., "Infosys Ltd", "Zomato").
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tracks if this entity typically functions as an Income Source (Payer) or an Expense destination (Payee).
    /// Used by the AIS engine for intelligent reporting.
    /// </summary>
    public bool IsDefaultIncomeSource { get; set; }

    public DateTime CreatedDate { get; set; }
}