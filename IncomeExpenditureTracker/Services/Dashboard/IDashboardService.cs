using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Dashboard
{
    /// <summary>
    /// Provides high-level aggregated KPIs and diagnostic alerts for the Dashboard landing page.
    /// Routes all database queries through IDatabaseService.
    /// </summary>
    public interface IDashboardService
    {
        /// <summary>
        /// Fetches system-wide diagnostic alerts (e.g., unmapped payees, missing tags, core data faults).
        /// These are ordered by severity (Critical > Warning > Info).
        /// </summary>
        /// <returns>List of alert banners to display in the Dashboard triage zone</returns>
        Task<IReadOnlyList<AlertBannerDTO>> GetSystemAlertsAsync();

        /// <summary>
        /// Calculates Month-to-Date cash flow (total inflow vs outflow).
        /// </summary>
        /// <returns>KPI data for the current month's cash flow</returns>
        Task<CashFlowKpiDTO> GetMonthToDateCashFlowAsync();

        /// <summary>
        /// Calculates current Net Worth (Total Assets - Total Liabilities).
        /// </summary>
        /// <returns>Current net worth figure</returns>
        Task<decimal> GetCurrentNetWorthAsync();

        /// <summary>
        /// Fetches the most recent N transactions for quick visibility.
        /// </summary>
        /// <param name="limit">Maximum number of transactions to return</param>
        /// <returns>List of recent transaction summaries</returns>
        Task<IReadOnlyList<RecentTransactionDTO>> GetRecentTransactionsAsync(int limit = 5);

        /// <summary>
        /// Fetches daily expense totals for the rolling 30-day sparkline chart.
        /// </summary>
        /// <returns>List of daily expense data points</returns>
        Task<IReadOnlyList<DailyExpenseDTO>> GetRolling30DayExpensesAsync();

        /// <summary>
        /// Fetches active financial goal progress for the KPI card.
        /// </summary>
        /// <returns>Active goal summary (null if no goals exist)</returns>
        Task<ActiveGoalDTO?> GetActiveGoalAsync();
    }

    // ============================================================================
    // DATA TRANSFER OBJECTS
    // ============================================================================

    /// <summary>
    /// Represents a system diagnostic alert banner.
    /// </summary>
    public record AlertBannerDTO
    {
        public AlertSeverity Severity { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string? ActionLocaleKey { get; init; } // Optional CTA button text
        public string? NavigationDestination { get; init; } // Optional navigation target
    }

    /// <summary>
    /// Severity levels for alert banners, ordered by urgency.
    /// </summary>
    public enum AlertSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    /// <summary>
    /// Month-to-Date cash flow KPI.
    /// </summary>
    public record CashFlowKpiDTO(decimal TotalInflow, decimal TotalOutflow, decimal NetCashFlow);

    /// <summary>
    /// Recent transaction summary for the activity feed.
    /// </summary>
    public record RecentTransactionDTO
    {
        public long TransactionId { get; init; }
        public DateTime TransactionDate { get; init; }
        public string Description { get; init; } = string.Empty;
        public string? PayeeName { get; init; }
        public string? TagName { get; init; }
        public decimal? DebitAmount { get; init; }
        public decimal? CreditAmount { get; init; }
    }

    /// <summary>
    /// Daily expense data point for the sparkline chart.
    /// </summary>
    public record DailyExpenseDTO(DateTime Date, decimal Amount);

    /// <summary>
    /// Active financial goal progress summary.
    /// </summary>
    public record ActiveGoalDTO
    {
        public string GoalName { get; init; } = string.Empty;
        public decimal TargetAmount { get; init; }
        public decimal CurrentAmount { get; init; }
        public double CompletionPercentage { get; init; }
    }
}
