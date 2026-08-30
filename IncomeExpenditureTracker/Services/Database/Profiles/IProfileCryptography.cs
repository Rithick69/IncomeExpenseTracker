using System.Security;

namespace IncomeExpenditureTracker.Services.Database
{
    /// <summary>
    /// Abstracts the generation of encrypted connection strings to ensure
    /// UI ViewModels and the DatabaseService remain ignorant of the underlying cryptography.
    /// </summary>
    public interface IProfileCryptography
    {
        /// <summary>
        /// Safely unwraps a SecureString in unmanaged memory to build the connection string,
        /// ensuring the raw password does not linger in the managed Garbage Collector heap.
        /// </summary>
        string BuildEncryptedConnectionString(string databasePath, SecureString securePassword);
    }
}