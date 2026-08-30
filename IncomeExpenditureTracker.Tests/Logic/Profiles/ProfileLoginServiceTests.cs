using System;
using System.Security;
using System.Threading.Tasks;
using Xunit;
using Moq;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.Tests.Logic.Profiles
{
    public class ProfileLoginServiceTests
    {
        private readonly Mock<IProfileRegistry> _registryMock;
        private readonly Mock<IPasswordHasher> _hasherMock;
        private readonly Mock<IProfileCryptography> _cryptoMock;
        private readonly Mock<IDatabaseService> _dbServiceMock;
        private readonly Mock<IDatabaseInitializer> _dbInitMock;
        private readonly Mock<IApplicationBroker> _brokerMock;
        private readonly Mock<ILogger<ProfileLoginService>> _loggerMock;

        private readonly ProfileLoginService _loginService;

        public ProfileLoginServiceTests()
        {
            _registryMock = new Mock<IProfileRegistry>();
            _hasherMock = new Mock<IPasswordHasher>();
            _cryptoMock = new Mock<IProfileCryptography>();
            _dbServiceMock = new Mock<IDatabaseService>();
            _dbInitMock = new Mock<IDatabaseInitializer>();
            _loggerMock = new Mock<ILogger<ProfileLoginService>>();

            _brokerMock = new Mock<IApplicationBroker>();

            _loginService = new ProfileLoginService(
                _registryMock.Object, _hasherMock.Object, _cryptoMock.Object,
                _dbServiceMock.Object, _dbInitMock.Object, _loggerMock.Object, _brokerMock.Object);
        }

        private SecureString CreateSecureString()
        {
            var ss = new SecureString();
            ss.AppendChar('x');
            ss.MakeReadOnly();
            return ss;
        }

        [Fact]
        public async Task AuthenticateAndLoad_ValidCredentials_SwapsDbAndInitializesSchema()
        {
            // Arrange
            var profileId = "123";
            using var password = CreateSecureString();
            var profile = new ProfileDto
            {

                ProfileId = profileId,
                ProfileName = "Charlie",
                DatabaseFilePath = "path",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now

            };

            _registryMock.Setup(r => r.GetProfileByIdAsync(profileId)).ReturnsAsync(profile);
            _hasherMock.Setup(h => h.VerifyPassword(password, "hash", "salt")).Returns(true);
            _cryptoMock.Setup(c => c.BuildEncryptedConnectionString("path", password)).Returns("EncryptedString");

            // Act
            var result = await _loginService.AuthenticateAndLoadProfileAsync(profileId, password);

            // Assert
            Assert.True(result);

            // Proves the Airlock was triggered with the correct cryptographic string
            _dbServiceMock.Verify(d => d.SetConnectionStringAsync("EncryptedString"), Times.Once);

            // Proves the Database Schema Initializer was delayed until AFTER the swap
            _dbInitMock.Verify(i => i.InitializeAsync(), Times.Once);
        }

        [Fact]
        public async Task AuthenticateAndLoad_InvalidPassword_ShortCircuitsBeforeDatabaseSwap()
        {
            // Arrange
            var profileId = "123";
            using var badPassword = CreateSecureString();
            var profile = new ProfileDto
            {

                ProfileId = profileId,
                ProfileName = "Charlie",
                DatabaseFilePath = "path",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now

            };

            _registryMock.Setup(r => r.GetProfileByIdAsync(profileId)).ReturnsAsync(profile);

            // SIMULATE FAILED LOGIN
            _hasherMock.Setup(h => h.VerifyPassword(badPassword, "hash", "salt")).Returns(false);

            // Act
            var result = await _loginService.AuthenticateAndLoadProfileAsync(profileId, badPassword);

            // Assert - The Short-Circuit Defense
            Assert.False(result);

            // Mathematically proves that a bad password never attempts to decrypt or swap the DB
            _cryptoMock.Verify(c => c.BuildEncryptedConnectionString(It.IsAny<string>(), It.IsAny<SecureString>()), Times.Never);
            _dbServiceMock.Verify(d => d.SetConnectionStringAsync(It.IsAny<string>()), Times.Never);
            _dbInitMock.Verify(i => i.InitializeAsync(), Times.Never);
        }

        [Fact]
        public async Task LogoutAsync_SecuresApplication_ByEmptyingConnectionString_AndFlushingCaches()
        {
            // Act
            await _loginService.LogoutAsync();

            // Assert: Proves the Airlock was triggered with an empty string, cutting off DB access
            _dbServiceMock.Verify(d => d.SetConnectionStringAsync(string.Empty), Times.Once);

            // Assert: Proves the Data Bleed defense was triggered to wipe Singleton memory
            _brokerMock.Verify(b => b.Send(It.IsAny<ProfileSwappedMessage>()), Times.Once);
        }

        [Fact]
        public async Task Authenticate_WhenFailedLoginOccurs_DoesNotFlushExistingCaches()
        {
            // Arrange (Simulate Alice is currently logged in, but Bob types the wrong password)
            var profileId = "bob123";
            using var badPassword = CreateSecureString();
            var profile = new ProfileDto
            {

                ProfileId = profileId,
                ProfileName = "Bob",
                DatabaseFilePath = "path",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now

            };

            _registryMock.Setup(r => r.GetProfileByIdAsync(profileId)).ReturnsAsync(profile);
            _hasherMock.Setup(h => h.VerifyPassword(badPassword, "hash", "salt")).Returns(false);

            // Act
            var result = await _loginService.AuthenticateAndLoadProfileAsync(profileId, badPassword);

            // Assert
            Assert.False(result);

            // Proves we didn't accidentally destroy Alice's active session just because Bob failed to log in
            _dbServiceMock.Verify(d => d.SetConnectionStringAsync(It.IsAny<string>()), Times.Never);
            _brokerMock.Verify(b => b.Send(It.IsAny<ProfileSwappedMessage>()), Times.Never);
        }

        [Fact]
        public async Task Authenticate_WhenSchemaInitializationFails_ExecutesEmergencyRollback()
        {
            // Arrange (Simulate a corrupted database file crashing the DatabaseInitializer)
            var profileId = "123";
            using var password = CreateSecureString();
            var profile = new ProfileDto
            {

                ProfileId = profileId,
                ProfileName = "Charlie",
                DatabaseFilePath = "path",
                PasswordHash = "hash",
                PasswordSalt = "salt",
                CreatedDate = DateTime.Now

            };

            _registryMock.Setup(r => r.GetProfileByIdAsync(profileId)).ReturnsAsync(profile);
            _hasherMock.Setup(h => h.VerifyPassword(password, "hash", "salt")).Returns(true);
            _cryptoMock.Setup(c => c.BuildEncryptedConnectionString("path", password)).Returns("EncryptedString");

            // The Initializer crashes midway through execution
            _dbInitMock.Setup(i => i.InitializeAsync()).ThrowsAsync(new InvalidOperationException("Corrupt DB"));

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _loginService.AuthenticateAndLoadProfileAsync(profileId, password));

            // Proves the emergency rollback (LogoutAsync) was successfully executed to prevent a half-loaded state
            _dbServiceMock.Verify(d => d.SetConnectionStringAsync(string.Empty), Times.Once);
            _brokerMock.Verify(b => b.Send(It.IsAny<ProfileSwappedMessage>()), Times.AtLeastOnce);
        }

    }
}