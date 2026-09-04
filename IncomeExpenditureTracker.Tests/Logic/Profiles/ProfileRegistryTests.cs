using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Xunit;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using System.Collections.Generic;

namespace IncomeExpenditureTracker.Tests.Integration.Profiles
{
    public class ProfileRegistryTests : IAsyncLifetime
    {
        private readonly ProfileRegistry _registry;
        private readonly SqliteConnection _keepAliveConnection; // Keeps the in-memory DB alive during the test

        public ProfileRegistryTests()
        {
            // Use an isolated in-memory SQLite database for testing the registry
            var connectionString = "Data Source=InMemorySystemDb;Mode=Memory;Cache=Shared";
            _keepAliveConnection = new SqliteConnection(connectionString);
            _keepAliveConnection.Open();

            // Simulate the configuration override
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "ConnectionStrings:SystemConnection", connectionString } })
                .Build();

            _registry = new ProfileRegistry(config);
        }

        public async Task InitializeAsync()
        {
            await _registry.InitializeRegistryAsync();
        }

        public Task DisposeAsync()
        {
            _keepAliveConnection.Close();
            _keepAliveConnection.Dispose();
            return Task.CompletedTask;
        }

        [Fact]
        public async Task RegisterAndRetrieveProfile_Succeeds_AndMaintainsDataIntegrity()
        {
            // Arrange
            var newProfile = new ProfileDto
            {
                ProfileId = Guid.NewGuid().ToString(),
                ProfileName = "Alice",
                Nickname = "Ally",
                DatabaseFilePath = "C:\\Db\\Alice.db",
                PasswordHash = "FakeHash",
                PasswordSalt = "FakeSalt",
                CreatedDate = DateTime.Now
            };

            // Act
            await _registry.RegisterProfileAsync(newProfile);
            var fetchedProfile = await _registry.GetProfileByNameAsync(newProfile.ProfileName);

            // Assert
            Assert.NotNull(fetchedProfile);
            Assert.Equal("Alice", fetchedProfile.ProfileName);
            Assert.Equal("C:\\Db\\Alice.db", fetchedProfile.DatabaseFilePath);
        }

        [Fact]
        public async Task RegisterProfile_DuplicateProfileName_ThrowsSqliteException()
        {
            // Arrange - The registry schema strictly enforces UNIQUE on ProfileName
            var profile1 = new ProfileDto
            {
                ProfileId = Guid.NewGuid().ToString(),
                ProfileName = "Bob",
                Nickname = "Robert",
                DatabaseFilePath = "path1",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now
            };


            var profile2 = new ProfileDto
            {
                ProfileId = Guid.NewGuid().ToString(),
                ProfileName = "Bob",
                Nickname = "Robert",
                DatabaseFilePath = "path2",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now
            };

            await _registry.RegisterProfileAsync(profile1);

            // Act & Assert - Proves that SQL constraints block duplicate users at the engine level
            var ex = await Assert.ThrowsAsync<SqliteException>(() => _registry.RegisterProfileAsync(profile2));
            Assert.Contains("UNIQUE constraint failed", ex.Message);
        }
    }
}