using System;
namespace IncomeExpenditureTracker.Models;

public class ImportBatch
{
    public int Id { get; set; }

    public string FileName { get; set; } = "";

    public string Source { get; set; } = "";

    public DateTime ImportDate { get; set; } = DateTime.UtcNow;

    public int AccountId { get; set; }
}