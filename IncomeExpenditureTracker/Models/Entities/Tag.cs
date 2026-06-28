namespace IncomeExpenditureTracker.Models;

// ------------------------------------------------------------
// TAG MODEL
// ------------------------------------------------------------
// Tags represent specific entities or labels assigned
// to transactions.
//
// Tags belong to a SubCategory.
//
// Example:
//
// SubCategory: Mutual Fund
// Tags:
// - Groww
// - Zerodha
//
// SubCategory: Food
// Tags:
// - Swiggy
// - Zomato
//
// TagId is what gets assigned to transactions.
// ------------------------------------------------------------
public class Tag
{
    // Primary key
    public int Id { get; set; }

    // Name of the tag
    // Example: Groww, Swiggy, Zerodha
    public string Name { get; set; } = "";

    // Foreign key referencing SubCategory
    public int SubCategoryId { get; set; }
}