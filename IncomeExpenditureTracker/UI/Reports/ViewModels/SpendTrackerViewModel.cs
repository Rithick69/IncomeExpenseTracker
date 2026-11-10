using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Analytics;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Reports.ViewModels
{
    /// <summary>
    /// Child ViewModel for the SpendTracker report tab.
    /// Displays monthly income vs. expense trends and category-wise expense breakdowns.
    /// Populates with MOCK DATA for immediate UI rendering (no database queries).
    /// </summary>
    public partial class SpendTrackerViewModel : ViewModelBase
    {
        private readonly IAnalyticsService _analyticsService;

        // ============================================================================
        // OBSERVABLE PROPERTIES (Summary Cards)
        // ============================================================================

        /// <summary>
        /// Total income for the selected period
        /// </summary>
        [ObservableProperty]
        private decimal _totalIncome;

        /// <summary>
        /// Total expenses for the selected period
        /// </summary>
        [ObservableProperty]
        private decimal _totalExpense;

        /// <summary>
        /// Net cashflow (Income - Expense)
        /// </summary>
        [ObservableProperty]
        private decimal _netCashflow;

        // ============================================================================
        // LIVECHARTS2 MOCK DATA COLLECTIONS
        // ============================================================================
        // NOTE: These collections will hold mock data to render charts immediately.
        // In production, these will be populated via IAnalyticsService queries.
        // LiveCharts2 requires: Install-Package LiveChartsCore.SkiaSharpView.Avalonia

        /// <summary>
        /// Mock monthly income data points for the bar chart
        /// </summary>
        public ObservableCollection<MonthlyDataPoint> MonthlyIncomeData { get; } = new();

        /// <summary>
        /// Mock monthly expense data points for the bar chart
        /// </summary>
        public ObservableCollection<MonthlyDataPoint> MonthlyExpenseData { get; } = new();

        /// <summary>
        /// Mock category-wise expense data for the doughnut chart
        /// </summary>
        public ObservableCollection<CategoryDataPoint> CategoryExpenseData { get; } = new();

        // ============================================================================
        // CONSTRUCTOR & MOCK DATA INITIALIZATION
        // ============================================================================

        public SpendTrackerViewModel(
            IApplicationBroker broker,
            IAnalyticsService analyticsService) : base(broker)
        {
            _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));

            // Populate MOCK DATA immediately for UI rendering
            LoadMockData();
        }

        /// <summary>
        /// Populates mock data for immediate UI rendering without database queries.
        /// This method demonstrates the expected data shape for LiveCharts2 integration.
        /// </summary>
        private void LoadMockData()
        {
            // Mock Summary Card Values
            TotalIncome = 450000m;
            TotalExpense = 320000m;
            NetCashflow = TotalIncome - TotalExpense; // 130000

            // Mock Monthly Income/Expense Trends (Last 12 Months)
            var mockMonthlyData = new[]
            {
                new { Month = "Jan", Income = 35000m, Expense = 25000m },
                new { Month = "Feb", Income = 38000m, Expense = 27000m },
                new { Month = "Mar", Income = 40000m, Expense = 30000m },
                new { Month = "Apr", Income = 42000m, Expense = 28000m },
                new { Month = "May", Income = 37000m, Expense = 26000m },
                new { Month = "Jun", Income = 39000m, Expense = 29000m },
                new { Month = "Jul", Income = 41000m, Expense = 31000m },
                new { Month = "Aug", Income = 38000m, Expense = 27000m },
                new { Month = "Sep", Income = 36000m, Expense = 25000m },
                new { Month = "Oct", Income = 40000m, Expense = 28000m },
                new { Month = "Nov", Income = 42000m, Expense = 30000m },
                new { Month = "Dec", Income = 44000m, Expense = 32000m }
            };

            foreach (var dataPoint in mockMonthlyData)
            {
                MonthlyIncomeData.Add(new MonthlyDataPoint(dataPoint.Month, (double)dataPoint.Income));
                MonthlyExpenseData.Add(new MonthlyDataPoint(dataPoint.Month, (double)dataPoint.Expense));
            }

            // Mock Category-wise Expense Breakdown for Doughnut Chart
            var mockCategoryData = new[]
            {
                new { Category = "Housing", Amount = 80000m },
                new { Category = "Food & Dining", Amount = 60000m },
                new { Category = "Transportation", Amount = 45000m },
                new { Category = "Utilities", Amount = 35000m },
                new { Category = "Entertainment", Amount = 40000m },
                new { Category = "Healthcare", Amount = 30000m },
                new { Category = "Miscellaneous", Amount = 30000m }
            };

            foreach (var dataPoint in mockCategoryData)
            {
                CategoryExpenseData.Add(new CategoryDataPoint(dataPoint.Category, (double)dataPoint.Amount));
            }
        }

        // ============================================================================
        // PUBLIC METHODS (Called by Parent ViewModel)
        // ============================================================================

        /// <summary>
        /// Updates the date range and reloads the report data.
        /// In production, this will trigger a fresh database query via IAnalyticsService.
        /// </summary>
        public void UpdateDateRange(DateTime startDate, DateTime endDate)
        {
            // TODO: In production, query IAnalyticsService with the new date range
            // For now, the mock data remains static
            // Example:
            // var summaries = await _analyticsService.GetMonthlySpendSummaryAsync(startDate, endDate);
            // MonthlyIncomeData.Clear();
            // MonthlyExpenseData.Clear();
            // foreach (var summary in summaries)
            // {
            //     MonthlyIncomeData.Add(new MonthlyDataPoint(summary.MonthLabel, (double)summary.Income));
            //     MonthlyExpenseData.Add(new MonthlyDataPoint(summary.MonthLabel, (double)summary.Expense));
            // }
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }

    // ============================================================================
    // DATA TRANSFER OBJECTS FOR LIVECHARTS2
    // ============================================================================

    /// <summary>
    /// Represents a single data point for monthly income/expense charts.
    /// </summary>
    public record MonthlyDataPoint(string Month, double Value);

    /// <summary>
    /// Represents a single data point for category-wise expense breakdown.
    /// </summary>
    public record CategoryDataPoint(string Category, double Value);
}
