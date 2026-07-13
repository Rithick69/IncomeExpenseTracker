using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Entities;

// Interface for managing entities (e.g., banks, financial institutions) in the system.
// Provides methods to get or create entities, retrieve all entities, update an entity, and delete an entity.
// This service abstracts the data access layer for entities, allowing for easier testing and separation of concerns.

public interface IEntityService
{
    Task<int> GetOrCreateEntity(string name, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task<List<Entity>> GetAllEntities(IDbConnection? conn = null, IDbTransaction? tx = null);
    Task UpdateEntity(Entity entity, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteEntity(int entityId, IDbConnection? conn = null, IDbTransaction? tx = null);
}