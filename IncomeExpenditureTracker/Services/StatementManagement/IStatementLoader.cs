using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.StatementManagement;

public interface IStatementLoader
{
    // The main loading method for single/default sheet
    Task<StatementLoadResult> LoadStatementAsync(string filePath, IProgress<LoadingProgress> progress = null);

    // Retrieves metadata for all sheets to allow user selection
    Task<List<SheetMetaData>> GetAvailableSheetsAsync(string filePath);

    // Loads a specific sheet chosen by the user
    Task<StatementLoadResult> LoadSpecificSheetAsync(string filePath, string sheetName, IProgress<LoadingProgress> progress = null);
}