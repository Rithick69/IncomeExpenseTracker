using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Analytics
{
    /// <summary>
    /// Provides analytical data aggregation for historical reports.
    /// Routes all database queries through IDatabaseService.
    /// </summary>
    public interface IAnalyticsService
    {
        /// <summary>
        /// Fetches aggregated income and expense totals grouped by month within the specified date range.
        /// </summary>
        /// <param name="startDate">Start of the reporting period</param>
        /// <param name="endDate">End of the reporting period</param>
        /// <returns>List of monthly aggregated income/expense data</returns>
        Task<IReadOnlyList<MonthlySpendSummary>> GetMonthlySpendSummaryAsync(DateTime startDate, DateTime endDate);

        /// <summary>
        /// Fetches expense totals grouped by macro category for the specified date range.
        /// </summary>
        /// <param name="startDate">Start of the reporting period</param>
        /// <param name="endDate">End of the reporting period</param>
        /// <returns>List of category-wise expense breakdowns</returns>
        Task<IReadOnlyList<CategorySpendSummary>> GetCategorySpendSummaryAsync(DateTime startDate, DateTime endDate);

        /// <summary>
        /// Calculates the total income for the specified date range.
        /// </summary>
        Task<decimal> GetTotalIncomeAsync(DateTime startDate, DateTime endDate);

        /// <summary>
        /// Calculates the total expenses for the specified date range.
        /// </summary>
        Task<decimal> GetTotalExpenseAsync(DateTime startDate, DateTime endDate);
    }

    /// <summary>
    /// Data transfer object representing monthly income and expense aggregation.
    /// </summary>
    public record MonthlySpendSummary(string MonthLabel, decimal Income, decimal Expense);

    /// <summary>
    /// Data transfer object representing category-wise expense breakdown.
    /// </summary>
    public record CategorySpendSummary(string CategoryName, decimal Amount);
}
