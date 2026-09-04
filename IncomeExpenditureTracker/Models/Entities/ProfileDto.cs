using System;

namespace IncomeExpenditureTracker.Models
{
    public record ProfileDto
    {
        public string ProfileId { get; init; } = string.Empty;
        public string ProfileName { get; init; } = string.Empty;
        public string Nickname { get; init; } = string.Empty;
        public string DatabaseFilePath { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string PasswordSalt { get; init; } = string.Empty;

        // Captures the extra column Dapper found without breaking the mapping
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        // Brute Force Defense
        public int FailedAttemptCount { get; init; } = 0;
        public DateTime? LockoutEndUtc { get; init; }

        public string MasterKeyHash { get; init; } = string.Empty;
        public string MasterKeySalt { get; init; } = string.Empty;

        // Parameterless constructor strictly required by Dapper for safe deserialization
        public ProfileDto() { }
    }
}