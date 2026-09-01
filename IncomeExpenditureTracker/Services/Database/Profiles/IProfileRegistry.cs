using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Database
{
    public interface IProfileRegistry
    {
        Task InitializeRegistryAsync();
        Task<IEnumerable<ProfileDto>> GetAllProfilesAsync();
        Task RegisterProfileAsync(ProfileDto profile);
        Task DeleteProfileAsync(string profileId);
        Task<ProfileDto?> GetProfileByNameAsync(string profileName);
        Task UpdateLockoutStateAsync(string profileId, int failedAttemptCount, DateTime? lockoutEndUtc);
    }
}