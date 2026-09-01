using System.Security;
using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Database
{

    public interface IProfileLoginService
    {
        /// <summary>
        /// Authenticates the user. If successful, it securely swaps the database connection
        /// and initializes the schema, returning true. Returns false on invalid credentials.
        /// </summary>
        Task<bool> AuthenticateAndLoadProfileAsync(string profileId, SecureString password);

        /// <summary>
        /// Safely terminates the current session by resetting the database connection,
        /// releasing all OS file locks, and broadcasting a cache teardown event.
        /// </summary>
        Task LogoutAsync();
    }
}