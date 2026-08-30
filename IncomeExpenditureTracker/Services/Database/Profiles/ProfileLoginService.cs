using System;
using System.Security;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.Services.Database
{

    public class ProfileLoginService : IProfileLoginService
    {
        private readonly IProfileRegistry _registry;
        private readonly IPasswordHasher _hasher;
        private readonly IProfileCryptography _cryptography;
        private readonly IDatabaseService _databaseService;
        private readonly IDatabaseInitializer _dbInitializer;
        private readonly ILogger<ProfileLoginService> _logger;

        private readonly IApplicationBroker _broker;

        public ProfileLoginService(
            IProfileRegistry registry,
            IPasswordHasher hasher,
            IProfileCryptography cryptography,
            IDatabaseService databaseService,
            IDatabaseInitializer dbInitializer,
            ILogger<ProfileLoginService> logger,
            IApplicationBroker broker)
        {
            _registry = registry;
            _hasher = hasher;
            _cryptography = cryptography;
            _databaseService = databaseService;
            _dbInitializer = dbInitializer; // Injected to delay execution until post-login
            _logger = logger;
            _broker = broker;
        }

        public async Task<bool> AuthenticateAndLoadProfileAsync(string profileId, SecureString password)
        {
            if (string.IsNullOrWhiteSpace(profileId) || password == null || password.Length == 0)
                return false;

            // 1. Fetch the profile metadata from the unencrypted system.db directory
            var profile = await _registry.GetProfileByIdAsync(profileId);
            if (profile == null)
            {
                _logger.LogWarning("Login failed: Profile ID {ProfileId} not found.", profileId);
                return false;
            }

            // 2. Cryptographic Verification: Hash the provided SecureString and compare it to the stored Hash/Salt
            bool isAuthorized = _hasher.VerifyPassword(password, profile.PasswordHash, profile.PasswordSalt);

            if (!isAuthorized)
            {
                _logger.LogWarning("Login failed: Invalid password for profile {ProfileName}.", profile.ProfileName);
                return false;
            }

            try
            {
                // 3. Build the SQLCipher connection string. The cryptography service handles
                // unwrapping the SecureString in unmanaged memory and zeroing it out immediately.
                var connectionString = _cryptography.BuildEncryptedConnectionString(profile.DatabaseFilePath, password);

                // 4. Trigger the Airlock: Swap the connection string and annihilate old file locks.
                await _databaseService.SetConnectionStringAsync(connectionString);

                // 5. Broadcast Cache Annihilation: If Profile A was logged in, this forces
                // TagService and CategoryService to wipe Profile A's data from RAM immediately.
                _broker.Send(new ProfileSwappedMessage());

                // 6. Delayed Initialization: Now that the encrypted file is unlocked,
                // execute the PRAGMAs and schema creations.
                await _dbInitializer.InitializeAsync();

                _logger.LogInformation("Profile {ProfileName} successfully authenticated and loaded into memory.", profile.ProfileName);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A critical failure occurred while loading the encrypted database for {ProfileName}.", profile.ProfileName);

                // EMERGENCY ROLLBACK: If schema initialization fails (e.g., corrupt DB file),
                // force a hard logout to prevent the application from being stuck in a broken state.
                await LogoutAsync();
                throw;
            }
        }

        public async Task LogoutAsync()
        {
            _logger.LogInformation("Initiating profile logout sequence...");

            // 1. Trigger the Airlock: Point the database string to empty and clear SQLite pools
            // This guarantees the OS releases the physical .db file lock.
            await _databaseService.SetConnectionStringAsync(string.Empty);

            // 2. Broadcast Cache Annihilation: Ensure no sensitive data lingers in Singleton memory.
            _broker.Send(new ProfileSwappedMessage());

            _logger.LogInformation("Logout complete. File locks released and caches annihilated.");
        }
    }
}