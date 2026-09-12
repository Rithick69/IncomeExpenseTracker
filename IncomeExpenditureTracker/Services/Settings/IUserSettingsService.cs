using System.Threading.Tasks;
using System.Data;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;
using System.Threading;
namespace IncomeExpenditureTracker.Services.Settings
{
    public interface IUserSettingsService
    {
        Task SetSettingAsync(string key, string value, CancellationToken ct = default);
        Task<string?> GetSettingAsync(string key, CancellationToken ct = default);
        Task<List<UserSetting>> GetAllSettingsAsync(CancellationToken ct = default);
    }
}