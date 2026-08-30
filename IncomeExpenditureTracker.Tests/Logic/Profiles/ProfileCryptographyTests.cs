using System;
using System.Security;
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
        public void BuildEncryptedConnectionString_WithValidInputs_ReturnsFormattedSqlCipherString()
        {
            // Arrange
            var dbPath = "C:\\FakeData\\ProfileA.db";
            var rawPassword = "SuperSecretPassword123!";

            // Constructing a SecureString exactly as the UI will when a user types
            using var securePassword = new SecureString();
            foreach (char c in rawPassword)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            // Act
            var result = _cryptography.BuildEncryptedConnectionString(dbPath, securePassword);

            // Assert
            // We check that the builder successfully integrated the path and password.
            // Note: SqliteConnectionStringBuilder automatically formats keys (e.g., "Data Source", "Password")
            Assert.Contains(dbPath, result);
            Assert.Contains(rawPassword, result);
            Assert.Contains("Mode=ReadWriteCreate", result);
            Assert.Contains("Cache=Shared", result);
        }

        [Fact]
        public void BuildEncryptedConnectionString_EmptyDatabasePath_ThrowsArgumentException()
        {
            // Arrange
            using var securePassword = new SecureString();
            securePassword.AppendChar('A');

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _cryptography.BuildEncryptedConnectionString("", securePassword));

            Assert.Contains("databasePath", exception.ParamName);
        }

        [Fact]
        public void BuildEncryptedConnectionString_EmptyPassword_ThrowsArgumentException()
        {
            // Arrange
            var dbPath = "C:\\FakeData\\ProfileA.db";
            using var emptySecurePassword = new SecureString(); // 0 length

            // Act & Assert
            var exception = Assert.Throws<ArgumentException>(() =>
                _cryptography.BuildEncryptedConnectionString(dbPath, emptySecurePassword));

            Assert.Contains("securePassword", exception.ParamName);
        }
    }
}