namespace IncomeExpenditureTracker.Models;

// ------------------------------------------------------------
// SUBCATEGORY MODEL
// ------------------------------------------------------------
// Subcategories break down Categories into more
// specific financial classifications.
//
// Example:
//
// Category: Investment
// SubCategories:
// - Equity
// - Mutual Fund
// - RD
// - Insurance
//
// Category: Expense
// SubCategories:
// - Food
// - Travel
// - Shopping
// ------------------------------------------------------------
public class SubCategory
{
    // Primary key
    public int Id { get; set; }

    // Name of the subcategory
    public string Name { get; set; } = "";

    // Foreign key linking to Category
    public int CategoryId { get; set; }
}