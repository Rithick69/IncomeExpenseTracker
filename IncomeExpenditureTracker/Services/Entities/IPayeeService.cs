using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Entities
{
    public interface IPayeeService
    {
        Task<IEnumerable<Payee>> GetAllAsync(CancellationToken ct = default);
        Task<Payee> CreateAsync(Payee payee, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
        Task UpdateAsync(Payee payee, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
        Task DeleteAsync(int id, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

        /// <summary>
        /// Upserts a CleanedDescription -> PayeeId mapping and updates the in-memory cache.
        /// </summary>
        Task AddMappingAsync(string cleanedDescription, int payeeId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

        /// <summary>
        /// Executes duplicate consolidation
        /// </summary>
        Task MergeEntitiesAsync(int targetPayeeId, List<int> sourcePayeeIds, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

        /// <summary>
        /// Executes a retroactive sweep of the transactions table to update PayeeId for all transactions matching the given source and payeeId.
        /// </summary>
        Task ExecuteRetroactiveSweepAsync(string source, int payeeId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);

        /// <summary>
        /// Returns the zero-lock flat dictionary
        /// </summary>
        ConcurrentDictionary<string, int> GetMappingsCache();
    }
}