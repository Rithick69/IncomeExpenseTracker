using System.Security;

public interface IPasswordHasher
{
    (string Hash, string Salt) HashPassword(SecureString password);
    bool VerifyPassword(SecureString password, string hash, string salt);
}