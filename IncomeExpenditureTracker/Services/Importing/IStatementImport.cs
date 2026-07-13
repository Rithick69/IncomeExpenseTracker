using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Importing;

public interface IStatementImport<in TDocument>
{
    Task ImportConfirmedStatementAsync(TDocument document, StatementPreview approvedPreview);
}