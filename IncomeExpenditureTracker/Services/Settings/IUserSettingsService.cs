using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Settings
{
    public interface IUserSettingsService
    {
        Task SetSettingAsync(string key, string value);
        Task<string?> GetSettingAsync(string key);
    }
}