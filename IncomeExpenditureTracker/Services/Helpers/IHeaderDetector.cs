using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Helpers;

// This interface defines the contract for a header detector that provides
public interface IHeaderDetector<TDocument>
{
    // Detects the header row in the given Excel worksheet.
    public Task<int> DetectHeaderRow(TDocument document);
}