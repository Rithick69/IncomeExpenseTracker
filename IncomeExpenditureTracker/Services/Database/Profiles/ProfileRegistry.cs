using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Database
{

    public class ProfileRegistry : IProfileRegistry
    {
        private readonly string _systemDbConnectionString;

        // 1. Add IConfiguration to the constructor
        public ProfileRegistry(IConfiguration configuration)
        {
            // 2. Check if a test configuration explicitly overrides the DB path
            var configPath = configuration.GetConnectionString("SystemConnection");
            if (!string.IsNullOrEmpty(configPath))
            {
                _systemDbConnectionString = configPath;
                return; // Exit early if the test config is found
            }
            // Route system.db to the exact same isolated LocalApplicationData folder
            var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appDataFolder, "IncomeExpenditureTracker");
            Directory.CreateDirectory(appFolder);

            var systemDbPath = Path.Combine(appFolder, "system.db");
            _systemDbConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = systemDbPath
            }.ToString();
        }

        public async Task InitializeRegistryAsync()
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            await connection.OpenAsync();

            // Create the Profiles directory table if it doesn't exist
            var sql = @"
                CREATE TABLE IF NOT EXISTS Profiles (
                    ProfileId TEXT PRIMARY KEY,
                    ProfileName TEXT UNIQUE NOT NULL,
                    DatabaseFilePath TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    PasswordSalt TEXT NOT NULL,
                    CreatedDate  DATETIME DEFAULT (datetime('now')),
                    FailedAttemptCount INTEGER NOT NULL DEFAULT 0,
                    LockoutEndUtc TEXT NULL
                );";
            await connection.ExecuteAsync(sql);
        }

        public async Task<IEnumerable<ProfileDto>> GetAllProfilesAsync()
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            return await connection.QueryAsync<ProfileDto>("SELECT * FROM Profiles ORDER BY ProfileName");
        }

        public async Task RegisterProfileAsync(ProfileDto profile)
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            var sql = @"
                INSERT INTO Profiles (ProfileId, ProfileName, DatabaseFilePath, PasswordHash, PasswordSalt)
                VALUES (@ProfileId, @ProfileName, @DatabaseFilePath, @PasswordHash, @PasswordSalt)";
            await connection.ExecuteAsync(sql, profile);
        }

        public async Task DeleteProfileAsync(string profileId)
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            await connection.ExecuteAsync("DELETE FROM Profiles WHERE ProfileId = @ProfileId", new { ProfileId = profileId });
        }

        public async Task<ProfileDto?> GetProfileByIdAsync(string profileId)
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            return await connection.QuerySingleOrDefaultAsync<ProfileDto>(
                "SELECT * FROM Profiles WHERE ProfileId = @ProfileId",
                new { ProfileId = profileId });
        }

        public async Task UpdateLockoutStateAsync(string profileId, int failedAttemptCount, DateTime? lockoutEndUtc)
        {
            using var connection = new SqliteConnection(_systemDbConnectionString);
            var sql = @"UPDATE Profiles
                SET FailedAttemptCount = @FailedAttemptCount,
                    LockoutEndUtc = @LockoutEndUtc
                WHERE ProfileId = @ProfileId";

            await connection.ExecuteAsync(sql, new
            {
                ProfileId = profileId,
                FailedAttemptCount = failedAttemptCount,
                LockoutEndUtc = lockoutEndUtc
            });
        }
    }
}