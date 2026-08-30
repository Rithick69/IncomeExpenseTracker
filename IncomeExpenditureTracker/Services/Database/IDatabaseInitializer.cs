using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Database
{
    public interface IDatabaseInitializer
    {
        Task InitializeAsync();
    }
}