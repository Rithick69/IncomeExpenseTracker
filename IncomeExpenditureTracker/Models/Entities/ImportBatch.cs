public class ImportBatch
{
    public int Id { get; set; }

    public string FileName { get; set; } = "";

    public string Source { get; set; } = "";

    public string ImportDate { get; set; } = "";

    public int AccountId { get; set; }
}