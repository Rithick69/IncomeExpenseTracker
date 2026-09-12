using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data;
using System;
using IncomeExpenditureTracker.Models;
using System.Threading;
namespace IncomeExpenditureTracker.Services.Entities;

// Interface for managing categories in the system.
// Provides methods to get or create categories, retrieve all categories, update a category, and delete a category.
// This service abstracts the data access layer for categories, allowing for easier testing and separation of concerns.

public interface ICategoryService
{
    Task<int> GetOrCreateCategory(string name, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
    Task<List<Category>> GetAllCategories(CancellationToken ct = default);
    Task UpdateCategory(Category category, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
    Task DeleteCategory(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null, CancellationToken ct = default);
}