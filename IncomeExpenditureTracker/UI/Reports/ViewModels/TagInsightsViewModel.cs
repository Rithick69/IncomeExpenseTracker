using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Analytics;
using IncomeExpenditureTracker.Services.Export;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace IncomeExpenditureTracker.UI.Reports.ViewModels
{
    /// <summary>
    /// Child ViewModel for the Tag Insights report tab.
    /// Displays spending trends for a selected tag with month-over-month analysis.
    /// Populates with MOCK DATA for immediate UI rendering (no database queries).
    /// </summary>
    public partial class TagInsightsViewModel : ViewModelBase
    {
        private readonly IAnalyticsService _analyticsService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Currently selected tag for analysis
        /// </summary>
        [ObservableProperty]
        private string _selectedTag = "Groceries";

        /// <summary>
        /// Total spending for the selected tag
        /// </summary>
        [ObservableProperty]
        private decimal _totalSpend;

        /// <summary>
        /// Average monthly spending for the selected tag
        /// </summary>
        [ObservableProperty]
        private decimal _averageMonthlySpend;

        /// <summary>
        /// Number of transactions for the selected tag
        /// </summary>
        [ObservableProperty]
        private int _transactionCount;

        // ============================================================================
        // LIVECHARTS2 SERIES & DATAGRID COLLECTIONS
        // ============================================================================

        /// <summary>
        /// Series collection for the monthly spending line chart
        /// </summary>
        public ISeries[] MonthlySpendingSeries { get; set; } = Array.Empty<ISeries>();

        /// <summary>
        /// X-Axis configuration for the monthly spending chart
        /// </summary>
        public Axis[] XAxes { get; set; } = new Axis[]
        {
            new Axis
            {
                Labels = new string[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" }
            }
        };

        /// <summary>
        /// Mock transaction data for the DataGrid
        /// </summary>
        public ObservableCollection<TagTransactionDto> Transactions { get; } = new();

        /// <summary>
        /// Available tags for the AutoCompleteBox selector
        /// </summary>
        public ObservableCollection<string> AvailableTags { get; } = new();

        // ============================================================================
        // CONSTRUCTOR & MOCK DATA INITIALIZATION
        // ============================================================================

        public TagInsightsViewModel(
            IApplicationBroker broker,
            IAnalyticsService analyticsService,
            IExportService exportService) : base(broker)
        {
            _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));

            // Populate MOCK DATA immediately for UI rendering
            LoadMockData();
        }

        /// <summary>
        /// Populates mock data for immediate UI rendering without database queries.
        /// This demonstrates the expected data shape for LiveCharts2 and DataGrid integration.
        /// </summary>
        private void LoadMockData()
        {
            // Mock Available Tags for AutoCompleteBox
            AvailableTags.Add("Groceries");
            AvailableTags.Add("Transportation");
            AvailableTags.Add("Entertainment");
            AvailableTags.Add("Utilities");
            AvailableTags.Add("Healthcare");
            AvailableTags.Add("Dining Out");

            // Mock Summary Values
            TotalSpend = 72000m;
            AverageMonthlySpend = 6000m;
            TransactionCount = 48;

            // Mock Monthly Spending Trend for Line Chart (Last 12 Months)
            var spendingValues = new double[] { 5500, 6200, 6800, 5900, 6100, 6400, 6700, 5800, 5600, 6300, 6500, 7200 };

            // Create LiveCharts2 Line Series for Monthly Spending Trend
            MonthlySpendingSeries = new ISeries[]
            {
                new LineSeries<double>
                {
                    Name = "Groceries Spending",
                    Values = spendingValues,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.Blue) { StrokeThickness = 3 },
                    GeometryStroke = new SolidColorPaint(SKColors.Blue) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(SKColors.White),
                    GeometrySize = 8
                }
            };

            // Mock Transaction Details for DataGrid
            var mockTransactions = new[]
            {
                new TagTransactionDto(new DateTime(2024, 12, 15), "Walmart Supermarket", "Groceries", 3450m),
                new TagTransactionDto(new DateTime(2024, 12, 08), "Target Store", "Groceries", 2890m),
                new TagTransactionDto(new DateTime(2024, 11, 22), "Whole Foods Market", "Groceries", 4120m),
                new TagTransactionDto(new DateTime(2024, 11, 10), "Local Grocery Co-op", "Groceries", 2340m),
                new TagTransactionDto(new DateTime(2024, 10, 28), "Walmart Supermarket", "Groceries", 3670m),
                new TagTransactionDto(new DateTime(2024, 10, 14), "Costco Wholesale", "Groceries", 5890m),
                new TagTransactionDto(new DateTime(2024, 09, 30), "Target Store", "Groceries", 3120m),
                new TagTransactionDto(new DateTime(2024, 09, 16), "Whole Foods Market", "Groceries", 2480m),
                new TagTransactionDto(new DateTime(2024, 08, 25), "Local Grocery Co-op", "Groceries", 2950m),
                new TagTransactionDto(new DateTime(2024, 08, 11), "Walmart Supermarket", "Groceries", 2850m)
            };

            foreach (var transaction in mockTransactions)
            {
                Transactions.Add(transaction);
            }
        }

        // ============================================================================
        // PUBLIC METHODS (Called by Parent ViewModel or User Actions)
        // ============================================================================

        /// <summary>
        /// Updates the selected tag and reloads the report data.
        /// In production, this will trigger a fresh database query via IAnalyticsService.
        /// </summary>
        public void UpdateSelectedTag(string tagName)
        {
            // TODO: In production, query IAnalyticsService with the new tag
            // For now, the mock data remains static
            // Example:
            // var insights = await _analyticsService.GetTagInsightsAsync(tagName, startDate, endDate);
            // MonthlySpendingData.Clear();
            // Transactions.Clear();
            // ... populate with fresh data
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
    // DATA TRANSFER OBJECTS
    // ============================================================================

    /// <summary>
    /// Represents a single transaction record for the Tag Insights DataGrid.
    /// </summary>
    public record TagTransactionDto(DateTime Date, string Merchant, string Tag, decimal Amount);
}
