using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Helpers;

public interface IStrictAccountParser
{
    /// <summary>
    /// Evaluates any raw transaction string from any source and returns a structurally validated decimal result.
    /// </summary>
    AccountParseResult Parse(string? rawText);
}