using System.Collections.Generic;
using System.Threading.Tasks;
using System.Data;
using IncomeExpenditureTracker.Models;
namespace IncomeExpenditureTracker.Services.Entities;

// Interface for managing subcategories in the system.
// Provides methods to get or create subcategories, retrieve all subcategories, update a subcategory, and delete a subcategory.
// This service abstracts the data access layer for subcategories, allowing for easier testing and separation of concerns.

public interface ISubCategoryService
{
    Task<int> GetOrCreateSubCategory(string name, int? categoryId, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task<List<SubCategory>> GetAllSubCategories(IDbConnection? conn = null, IDbTransaction? tx = null);
    Task<List<SubCategory>> GetSubCategoriesByCategoryId(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task UpdateSubCategory(SubCategory subCategory, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteSubCategory(int subCategoryId, IDbConnection? conn = null, IDbTransaction? tx = null);
    Task DeleteByCategoryId(int categoryId, IDbConnection? conn = null, IDbTransaction? tx = null);
}