// This file defines a simple record type to hold metadata about each sheet in an Excel workbook.
// It includes the file path, sheet name, position in the workbook, and whether the sheet
// contains any data. This information is useful for the StatementLoader to present sheet options to the user.

public record SheetMetaData
{
    public string FilePath { get; init; } = "";

    public string SheetName { get; init; } = "";

    public int Position { get; init; }

    public bool IsEmpty { get; init; }
}