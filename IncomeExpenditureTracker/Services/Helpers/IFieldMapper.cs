using IncomeExpenditureTracker.Models;
using System.Threading.Tasks;
using System.Collections.Generic;
namespace IncomeExpenditureTracker.Services.Helpers;

// This interface defines the contract for a field mapper that provides
// methods for detecting account details and column mappings from an Excel worksheet.
public interface IFieldMapper<TDocument>
{

    // Detects column mappings based on the header row and synonyms.
    public Task<Dictionary<string, DetectedField>> DetectColumns(TDocument document, int headerRow, bool forceReload = false);

    // Detects account details from the given Excel worksheet.
    public Task<Dictionary<string, DetectedField>> DetectAccountDetails(TDocument document, bool forceReload = false);
}