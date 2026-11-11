using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Analytics;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

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
        // LIVECHARTS2 SERIES COLLECTIONS
        // ============================================================================

        /// <summary>
        /// Series collection for the monthly income/expense bar chart
        /// </summary>
        public ISeries[] MonthlyTrendSeries { get; set; } = Array.Empty<ISeries>();

        /// <summary>
        /// Series collection for the category-wise expense pie chart
        /// </summary>
        public ISeries[] CategoryBreakdownSeries { get; set; } = Array.Empty<ISeries>();

        /// <summary>
        /// X-Axis configuration for the monthly trend chart
        /// </summary>
        public Axis[] XAxes { get; set; } = new Axis[]
        {
            new Axis
            {
                Labels = new string[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }
            }
        };

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
        /// WHY: LiveCharts2 requires ISeries[] collections for rendering.
        /// </summary>
        private void LoadMockData()
        {
            // Mock Summary Card Values
            TotalIncome = 450000m;
            TotalExpense = 320000m;
            NetCashflow = TotalIncome - TotalExpense; // 130000

            // Mock Monthly Income/Expense Trends (Last 12 Months)
            var incomeValues = new double[] { 35000, 38000, 40000, 42000, 37000, 39000, 41000, 38000, 36000, 40000, 42000, 44000 };
            var expenseValues = new double[] { 25000, 27000, 30000, 28000, 26000, 29000, 31000, 27000, 25000, 28000, 30000, 32000 };

            // Create LiveCharts2 Column Series for Monthly Trend
            MonthlyTrendSeries = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Name = "Income",
                    Values = incomeValues,
                    Fill = new SolidColorPaint(SKColors.Green),
                    Stroke = null,
                    DataLabelsPaint = new SolidColorPaint(SKColors.Black),
                    DataLabelsSize = 12,
                    DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top
                },
                new ColumnSeries<double>
                {
                    Name = "Expense",
                    Values = expenseValues,
                    Fill = new SolidColorPaint(SKColors.Red),
                    Stroke = null,
                    DataLabelsPaint = new SolidColorPaint(SKColors.Black),
                    DataLabelsSize = 12,
                    DataLabelsPosition = LiveChartsCore.Measure.DataLabelsPosition.Top
                }
            };

            // Mock Category-wise Expense Breakdown for Pie Chart
            CategoryBreakdownSeries = new ISeries[]
            {
                new PieSeries<double> { Name = "Housing", Values = new double[] { 80000 } },
                new PieSeries<double> { Name = "Food & Dining", Values = new double[] { 60000 } },
                new PieSeries<double> { Name = "Transportation", Values = new double[] { 45000 } },
                new PieSeries<double> { Name = "Utilities", Values = new double[] { 35000 } },
                new PieSeries<double> { Name = "Entertainment", Values = new double[] { 40000 } },
                new PieSeries<double> { Name = "Healthcare", Values = new double[] { 30000 } },
                new PieSeries<double> { Name = "Miscellaneous", Values = new double[] { 30000 } }
            };
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
            // Recreate MonthlyTrendSeries with fresh data
            // Recreate CategoryBreakdownSeries with fresh data
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

}
