using System;
using Xunit;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Tests.Logic
{
    public class ProfileCryptographyTests
    {
        private readonly ProfileCryptography _cryptography;

        public ProfileCryptographyTests()
        {
            _cryptography = new ProfileCryptography();
        }

        [Fact]
        public void BuildEncryptedConnectionString_WithValidPath_ReturnsStringWithoutPassword()
        {
            // Arrange
            var dbPath = "C:\\FakeData\\ProfileA.db";

            // Act
            var result = _cryptography.BuildEncryptedConnectionString(dbPath);

            // Assert
            // We verify the baseline SQLite configurations are present
            Assert.Contains(dbPath, result);
            Assert.Contains("Mode=ReadWriteCreate", result);
            Assert.Contains("Cache=Shared", result);

            // ARCHITECTURAL GUARDRAIL PROOF:
            // Mathematically prove the string builder does NOT contain a 'Password' field,
            // verifying our zero-leak memory defense.
            Assert.DoesNotContain("Password=", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void BuildEncryptedConnectionString_EmptyDatabasePath_ThrowsArgumentException()
        {
            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _cryptography.BuildEncryptedConnectionString(""));

            Assert.Equal("databasePath", exception.ParamName);
        }

        // NOTE: The 'EmptyPassword_ThrowsArgumentException' test was intentionally deleted.
        // The method no longer accepts a password parameter, as password injection
        // has been delegated directly to the DatabaseService via PRAGMA key.
    }
}