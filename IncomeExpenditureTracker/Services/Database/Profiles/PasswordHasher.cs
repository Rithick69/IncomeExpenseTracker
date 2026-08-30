using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;

namespace IncomeExpenditureTracker.Services.Database
{

    public class PasswordHasher : IPasswordHasher
    {
        // 600,000 iterations slows down brute-force attacks significantly
        private const int Iterations = 600000;
        private const int HashSize = 32; // 256 bits
        private const int SaltSize = 16; // 128 bits

        public (string Hash, string Salt) HashPassword(SecureString password)
        {
            byte[] saltBytes = new byte[SaltSize];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes); // Generate a cryptographically secure random salt
            }

            var hashBytes = DeriveHash(password, saltBytes);

            return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
        }

        public bool VerifyPassword(SecureString password, string hash, string salt)
        {
            byte[] saltBytes = Convert.FromBase64String(salt);
            byte[] expectedHashBytes = Convert.FromBase64String(hash);
            byte[] actualHashBytes = DeriveHash(password, saltBytes);

            // Cryptographic constant-time comparison prevents timing attacks
            return CryptographicOperations.FixedTimeEquals(expectedHashBytes, actualHashBytes);
        }

        private byte[] DeriveHash(SecureString password, byte[] salt)
        {
            IntPtr unmanagedPtr = IntPtr.Zero;
            try
            {
                // Decrypt SecureString to a transient string in memory
                unmanagedPtr = Marshal.SecureStringToGlobalAllocUnicode(password);
                string transientPassword = Marshal.PtrToStringUni(unmanagedPtr) ?? string.Empty;

                // Execute PBKDF2 hashing
                using var pbkdf2 = new Rfc2898DeriveBytes(transientPassword, salt, Iterations, HashAlgorithmName.SHA256);
                return pbkdf2.GetBytes(HashSize);
            }
            finally
            {
                // Annihilate the unmanaged memory pointer
                if (unmanagedPtr != IntPtr.Zero)
                    Marshal.ZeroFreeGlobalAllocUnicode(unmanagedPtr);
            }
        }
    }
}