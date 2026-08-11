using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Entities;

// Interface for managing categories in the system.
// Provides methods to get or create categories, retrieve all categories, update a category, and delete a category.
// This service abstracts the data access layer for categories, allowing for easier testing and separation of concerns.

public interface ICategoryService
{
    Task<int> GetOrCreateCategory(string name, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task<List<Category>> GetAllCategories(IDbConnection? conn = null, IDbTransaction? tx = null);
    Task UpdateCategory(Category category, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteCategory(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null);
}