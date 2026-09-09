using System.Threading.Tasks;
using System.Data;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Settings
{
    public interface IUserSettingsService
    {
        Task SetSettingAsync(string key, string value);
        Task<string?> GetSettingAsync(string key);
        Task<List<UserSetting>> GetAllSettingsAsync();
    }
}