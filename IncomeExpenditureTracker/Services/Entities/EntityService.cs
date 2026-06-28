using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using IncomeExpenditureTracker.Models;

using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Entities;

// ------------------------------------------------------------
// ENTITY SERVICE
// ------------------------------------------------------------
// Handles CRUD operations for Entities.
//
// Entities represent financial institutions such as:
// • Banks
// • Credit card providers
// • Wallet services
//-------------------------------------------------------------
public class EntityService : IEntityService
{
    private readonly IDatabaseService _database;

    public EntityService(IDatabaseService database)
    {
        _database = database;
    }

    // ------------------------------------------------------------
    // FIND OR CREATE ENTITY
    // ------------------------------------------------------------
    public async Task<int> GetOrCreateEntity(string name)
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // Check if entity already exists using Async Dapper
                var existingId = await connection.QueryFirstOrDefaultAsync<int?>(
                    "SELECT Id FROM Entities WHERE Name = @Name",
                    new { Name = name });

                if (existingId.HasValue)
                    return existingId.Value;

                // Insert new entity and return Id
                var sql = @"
                INSERT INTO Entities (Name, Country, CreatedDate)
                VALUES (@Name, @Country, @CreatedDate);

                SELECT last_insert_rowid();";

                var id = await connection.ExecuteScalarAsync<long>(sql, new
                {
                    Name = name,
                    Country = "",
                    CreatedDate = DateTime.UtcNow
                });

                return (int)id;
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EntityService] Failed to create/find entity: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // GET ALL ENTITIES
    // ------------------------------------------------------------
    public async Task<List<Entity>> GetAllEntities()
    {
        try
        {
            return await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var entities = await connection.QueryAsync<Entity>(
                    "SELECT Id, Name, Country, CreatedDate FROM Entities ORDER BY Name ASC");

                return entities.ToList();
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EntityService] Failed to fetch entities: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // UPDATE ENTITY
    // ------------------------------------------------------------
    public async Task UpdateEntity(Entity entity)
    {
        try
        {
            var updates = new List<string>();

            if (!string.IsNullOrWhiteSpace(entity.Name))
                updates.Add("Name = @Name");

            if (!string.IsNullOrWhiteSpace(entity.Country))
                updates.Add("Country = @Country");

            if (updates.Count == 0)
                return;

            var sql = $@"
                UPDATE Entities
                SET {string.Join(", ", updates)}
                WHERE Id = @Id
            ";

            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                await connection.ExecuteAsync(sql, entity);
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EntityService] Failed to update entity: {ex.Message}");
            throw;
        }
    }

    // ------------------------------------------------------------
    // DELETE ENTITY
    // ------------------------------------------------------------
    public async Task DeleteEntity(int entityId)
    {
        try
        {
            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                // Check if entity is used by accounts
                var usageCount = await connection.ExecuteScalarAsync<int>(
                    @"SELECT COUNT(*)
                      FROM Accounts
                      WHERE EntityId = @EntityId",
                    new { EntityId = entityId });

                if (usageCount > 0)
                    throw new Exception("Cannot delete entity because accounts reference it.");

                await connection.ExecuteAsync(
                    @"DELETE FROM Entities WHERE Id = @EntityId",
                    new { EntityId = entityId });
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EntityService] Failed to delete entity: {ex.Message}");
            throw;
        }
    }
}