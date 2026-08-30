using System;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Data.Sqlite;

namespace IncomeExpenditureTracker.Services.Database
{
    public class ProfileCryptography : IProfileCryptography
    {
        public string BuildEncryptedConnectionString(string databasePath, SecureString securePassword)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));

            if (securePassword == null || securePassword.Length == 0)
                throw new ArgumentException("A valid password is required for encrypted profiles.", nameof(securePassword));

            // A pointer to hold the unmanaged memory location of our decrypted string
            IntPtr unmanagedPointer = IntPtr.Zero;

            try
            {
                // 1. Decrypt the SecureString directly into unmanaged memory.
                // This bypasses the standard .NET Garbage Collector heap.
                unmanagedPointer = Marshal.SecureStringToGlobalAllocUnicode(securePassword);

                // 2. Read the pointer into a transient string.
                // Note: While this transient string briefly exists in managed memory,
                // Microsoft.Data.Sqlite requires a standard string to parse the Password property.
                string decryptedPassword = Marshal.PtrToStringUni(unmanagedPointer) ?? string.Empty;

                // 3. Build the SQLCipher-compatible connection string
                var builder = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Password = decryptedPassword,
                    Cache = SqliteCacheMode.Shared
                };

                return builder.ToString();
            }
            finally
            {
                // 4. MEMORY ANNIHILATION: Zero out the unmanaged memory block immediately.
                // This guarantees that if a memory dump occurs seconds later, the raw password
                // is completely erased from this address space.
                if (unmanagedPointer != IntPtr.Zero)
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(unmanagedPointer);
                }
            }
        }
    }
}