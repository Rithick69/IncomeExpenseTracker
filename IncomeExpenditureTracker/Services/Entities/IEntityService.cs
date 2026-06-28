using System.Collections.Generic;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Entities;

// Interface for managing entities (e.g., banks, financial institutions) in the system.
// Provides methods to get or create entities, retrieve all entities, update an entity, and delete an entity.
// This service abstracts the data access layer for entities, allowing for easier testing and separation of concerns.

public interface IEntityService
{
    public Task<int> GetOrCreateEntity(string name);
    public Task<List<Entity>> GetAllEntities();
    public Task UpdateEntity(Entity entity);
    public Task DeleteEntity(int entityId);
}