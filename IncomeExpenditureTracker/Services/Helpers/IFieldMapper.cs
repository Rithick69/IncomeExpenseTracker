using IncomeExpenditureTracker.Models;
using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Helpers;

// This interface defines the contract for a field mapper that provides
// methods for detecting account details and column mappings from an Excel worksheet.
public interface IFieldMapper<TDocument>
{

    // Detects column mappings based on the header row and synonyms.
    public Task<TransColumnMap> DetectColumns(TDocument document, int headerRow);

    // Detects account details from the given Excel worksheet.
    public Task<Account> DetectAccountDetails(TDocument document);
}