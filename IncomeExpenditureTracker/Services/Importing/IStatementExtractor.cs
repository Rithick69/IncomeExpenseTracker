using IncomeExpenditureTracker.Models;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Importing;

public interface IStatementExtractor<in TDocument>
{
    public Task<StatementPreview> Analyze(TDocument document, string sourceFileName);
}