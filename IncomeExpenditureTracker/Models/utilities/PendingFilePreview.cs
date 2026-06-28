using System;
using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

// This record represents a pending file that has been uploaded by the user and is awaiting analysis and preview generation.
// It contains the file name and the list of sheet names (for Excel files) to be displayed in the preview UI before the user confirms the import.

public record PendingFilePreview(Guid Id, string FileName, List<string> SheetNames);