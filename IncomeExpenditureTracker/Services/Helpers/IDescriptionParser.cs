using System.Collections.Generic;

namespace IncomeExpenditureTracker.Services.Helpers
{

    /// <summary>
    /// Interface for the DescriptionParser service, which extracts tokens from transaction descriptions.
    /// </summary>
    public interface IDescriptionParser
    {
        List<string> ExtractTokens(string description);
    }
}