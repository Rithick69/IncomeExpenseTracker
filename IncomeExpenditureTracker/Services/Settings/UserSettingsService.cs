using System.Threading.Tasks;
using Dapper;
using System;
using System.Data;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using System.Collections;
using System.Linq;

namespace IncomeExpenditureTracker.Services.Settings
{


    public class UserSettingsService : IUserSettingsService
    {
        private readonly IDatabaseService _databaseService;
        private readonly ILogger<UserSettingsService> _logger;
        private readonly IApplicationBroker _broker;

        // Cache for individual settings (Stampede-protected)
        private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _settingsCache = new(StringComparer.OrdinalIgnoreCase);

        // Cache for the full settings list
        private readonly ConcurrentDictionary<string, Lazy<Task<List<UserSetting>>>> _allSettingsCache = new(StringComparer.OrdinalIgnoreCase);

        public UserSettingsService(IDatabaseService databaseService, ILogger<UserSettingsService> logger, IApplicationBroker broker)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));

            _broker.Register<ProfileSwappedMessage>(this, (message) => InvalidateCache());
        }

        public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await _databaseService.ExecuteWithRetryAsync(async (connection, cancelToken) =>
                {
                    var sql = @"
                    INSERT INTO UserSettings (SettingKey, SettingValue)
                    VALUES (@Key, @Value)
                    ON CONFLICT(SettingKey) DO UPDATE
                    SET SettingValue = excluded.SettingValue,
                        UpdatedAt = datetime('now');";

                    var cmd = new CommandDefinition(
                        sql,
                        new { Key = key, Value = value },
                        cancellationToken: cancelToken
                    );

                    await connection.ExecuteAsync(cmd);
                }, ct);

                // Invalidate caches to ensure subsequent reads fetch the fresh data
                _settingsCache.TryRemove(key, out _);
                _allSettingsCache.Clear();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update setting {Key}.", key);
                throw;
            }
        }

        public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Cache Stampede Protection: GetOrAdd ensures only ONE thread executes the DB query
                var cachedLazy = _settingsCache.GetOrAdd(key, _ => new Lazy<Task<string?>>(async () =>
                {
                    try
                    {
                        return await _databaseService.ExecuteWithRetryAsync(async (connection, cancelToken) =>
                        {
                            var sql = "SELECT SettingValue FROM UserSettings WHERE SettingKey = @Key";
                            var cmd = new CommandDefinition(sql, new { Key = key }, cancellationToken: cancelToken);
                            return await connection.QuerySingleOrDefaultAsync<string?>(cmd);
                        }, ct);
                    }
                    catch
                    {
                        // Fault Eviction: Remove the broken task from cache if the DB fails
                        _settingsCache.TryRemove(key, out var _);
                        throw;
                    }

                }, LazyThreadSafetyMode.ExecutionAndPublication));

                // Await the task. All concurrent requests for this key await the exact same task.
                return await cachedLazy.Value;
            }
            catch (OperationCanceledException)
            {
                throw; // Bubble up silently
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch setting {Key}.", key);
                throw;
            }
        }

        public async Task<List<UserSetting>> GetAllSettingsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            const string cacheKey = "ALL_SETTINGS";
            try
            {
                var cachedLazy = _allSettingsCache.GetOrAdd(cacheKey, _ => new Lazy<Task<List<UserSetting>>>(async () =>
                {
                    try
                    {
                        return await _databaseService.ExecuteWithRetryAsync(async (connection, cancelToken) =>
                        {
                            var sql = "SELECT SettingKey, SettingValue FROM UserSettings";
                            var cmd = new CommandDefinition(sql, cancellationToken: cancelToken);
                            var result = await connection.QueryAsync<UserSetting>(cmd);
                            return result.ToList();
                        }, ct);
                    }
                    catch
                    {
                        _allSettingsCache.TryRemove(cacheKey, out var _);
                        throw;
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication));

                return await cachedLazy.Value;
            }
            catch (OperationCanceledException)
            {
                throw; // Bubble up silently
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch all settings.");
                throw;
            }
        }

        private void InvalidateCache()
        {
            _settingsCache.Clear();
            _allSettingsCache.Clear();
            _logger.LogInformation("User settings cache invalidated due to data mutation or profile swap.");
        }

        public void Dispose()
        {
            _broker.UnregisterAll(this);
            GC.SuppressFinalize(this);
        }
    }
}