using System;
using System.Security;
using Xunit;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Tests.Logic.Profiles
{
    public class PasswordHasherTests
    {
        private readonly PasswordHasher _hasher;

        public PasswordHasherTests()
        {
            _hasher = new PasswordHasher();
        }

        private SecureString ToSecureString(string plainText)
        {
            var secureString = new SecureString();
            foreach (char c in plainText) secureString.AppendChar(c);
            secureString.MakeReadOnly();
            return secureString;
        }

        [Fact]
        public void HashPassword_SamePasswordHashedTwice_ProducesDifferentSaltsAndHashes()
        {
            // Arrange
            using var password = ToSecureString("MySecurePassword123!");

            // Act - Hashing the exact same password twice
            var (hash1, salt1) = _hasher.HashPassword(password);
            var (hash2, salt2) = _hasher.HashPassword(password);

            // Assert - This proves our random Salt generator works.
            // If these were equal, the system would be vulnerable to Rainbow Table attacks.
            Assert.NotEqual(salt1, salt2);
            Assert.NotEqual(hash1, hash2);
        }

        [Fact]
        public void VerifyPassword_CorrectPasswordAndSalt_ReturnsTrue()
        {
            // Arrange
            using var password = ToSecureString("TestPassword99");
            var (hash, salt) = _hasher.HashPassword(password);

            // Act
            bool isValid = _hasher.VerifyPassword(password, hash, salt);

            // Assert
            Assert.True(isValid, "The hasher failed to verify a correctly matched password and salt.");
        }

        [Fact]
        public void VerifyPassword_IncorrectPassword_ReturnsFalse()
        {
            // Arrange
            using var correctPassword = ToSecureString("CorrectPassword!");
            using var wrongPassword = ToSecureString("WrongPassword!");
            var (hash, salt) = _hasher.HashPassword(correctPassword);

            // Act
            bool isValid = _hasher.VerifyPassword(wrongPassword, hash, salt);

            // Assert - Proves unauthorized access is mathematically rejected
            Assert.False(isValid);
        }
    }
}