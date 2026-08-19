using System;
namespace IncomeExpenditureTracker.Models;

public class Synonyms
{
    public int Id { get; set; }

    public string FieldType { get; set; } = "";

    public string Synonym { get; set; } = "";

    public int Priority { get; set; } = 0;

    // Here Category stands for TRANSACTION/META categorisation of header fields.
    public string Category { get; set; } = "";

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

}