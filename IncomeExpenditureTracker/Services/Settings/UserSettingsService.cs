using System.Threading.Tasks;
using Dapper;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Settings
{


    public class UserSettingsService : IUserSettingsService
    {
        private readonly IDatabaseService _databaseService;

        public UserSettingsService(IDatabaseService databaseService)
        {
            _databaseService = databaseService;
        }

        public async Task SetSettingAsync(string key, string value)
        {
            // We use the retry wrapper to ensure this doesn't fail if a background
            // process is currently reading the database.
            await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var sql = @"
                    INSERT INTO UserSettings (SettingKey, SettingValue)
                    VALUES (@Key, @Value)
                    ON CONFLICT(SettingKey) DO UPDATE
                    SET SettingValue = excluded.SettingValue,
                        UpdatedAt = datetime('now');";

                await connection.ExecuteAsync(sql, new { Key = key, Value = value });
            });
        }

        public async Task<string?> GetSettingAsync(string key)
        {
            return await _databaseService.ExecuteWithRetryAsync(async connection =>
            {
                var sql = "SELECT SettingValue FROM UserSettings WHERE SettingKey = @Key";
                return await connection.QuerySingleOrDefaultAsync<string>(sql, new { Key = key });
            });
        }
    }
}