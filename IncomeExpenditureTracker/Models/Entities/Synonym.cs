namespace IncomeExpenditureTracker.Models;

public class Synonyms
{
    public int Id { get; set; }

    public string FieldType { get; set; } = "";

    public string Synonym { get; set; } = "";

    public int Priority { get; set; } = 0;

    public string Category { get; set; } = "";

}