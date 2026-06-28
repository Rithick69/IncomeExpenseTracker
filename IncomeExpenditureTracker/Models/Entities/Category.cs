namespace IncomeExpenditureTracker.Models;

// ------------------------------------------------------------
// CATEGORY MODEL
// ------------------------------------------------------------
// Top-level grouping of financial transactions.
//
// Example categories:
// - Income
// - Expense
// - Investment
//
// Categories are broad classifications used mainly
// for reporting and dashboard summaries.
// ------------------------------------------------------------
public class Category
{
    // Primary key
    public int Id { get; set; }

    // Name of the category
    // Example: Income, Expense, Investment
    public string Name { get; set; } = "";
}