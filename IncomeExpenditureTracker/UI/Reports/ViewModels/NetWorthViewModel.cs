using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Analytics;
using IncomeExpenditureTracker.Services.Export;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Reports.ViewModels
{
    /// <summary>
    /// Child ViewModel for the Net Worth report tab.
    /// Displays a stacked area chart showing Assets (positive) vs Liabilities (negative) over time.
    /// Populates with MOCK DATA for immediate UI rendering (no database queries).
    /// </summary>
    public partial class NetWorthViewModel : ViewModelBase
    {
        private readonly IAnalyticsService _analyticsService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Current total net worth (Assets - Liabilities)
        /// </summary>
        [ObservableProperty]
        private decimal _currentNetWorth;

        /// <summary>
        /// Total assets across all accounts
        /// </summary>
        [ObservableProperty]
        private decimal _totalAssets;

        /// <summary>
        /// Total liabilities (credit card balances, loans, etc.)
        /// </summary>
        [ObservableProperty]
        private decimal _totalLiabilities;

        /// <summary>
        /// Percentage change in net worth compared to last period
        /// </summary>
        [ObservableProperty]
        private decimal _netWorthChange;

        // ============================================================================
        // LIVECHARTS2 MOCK DATA COLLECTIONS
        // ============================================================================

        /// <summary>
        /// Mock monthly asset values for the stacked area chart (positive values)
        /// </summary>
        public ObservableCollection<MonthlyDataPoint> AssetsData { get; } = new();

        /// <summary>
        /// Mock monthly liability values for the stacked area chart (negative values)
        /// </summary>
        public ObservableCollection<MonthlyDataPoint> LiabilitiesData { get; } = new();

        /// <summary>
        /// Mock net worth trend line (Assets - Liabilities)
        /// </summary>
        public ObservableCollection<MonthlyDataPoint> NetWorthData { get; } = new();

        // ============================================================================
        // CONSTRUCTOR & MOCK DATA INITIALIZATION
        // ============================================================================

        public NetWorthViewModel(
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
        /// This demonstrates the expected data shape for LiveCharts2 stacked area chart integration.
        /// </summary>
        private void LoadMockData()
        {
            // Mock Summary Values
            TotalAssets = 1250000m;
            TotalLiabilities = 180000m;
            CurrentNetWorth = TotalAssets - TotalLiabilities; // 1,070,000
            NetWorthChange = 12.5m; // +12.5% growth

            // Mock Monthly Assets/Liabilities Trend for Stacked Area Chart (Last 12 Months)
            var mockData = new[]
            {
                new { Month = "Jan", Assets = 950000m, Liabilities = 220000m },
                new { Month = "Feb", Assets = 970000m, Liabilities = 215000m },
                new { Month = "Mar", Assets = 990000m, Liabilities = 210000m },
                new { Month = "Apr", Assets = 1010000m, Liabilities = 205000m },
                new { Month = "May", Assets = 1030000m, Liabilities = 200000m },
                new { Month = "Jun", Assets = 1060000m, Liabilities = 195000m },
                new { Month = "Jul", Assets = 1090000m, Liabilities = 190000m },
                new { Month = "Aug", Assets = 1120000m, Liabilities = 188000m },
                new { Month = "Sep", Assets = 1150000m, Liabilities = 186000m },
                new { Month = "Oct", Assets = 1180000m, Liabilities = 184000m },
                new { Month = "Nov", Assets = 1210000m, Liabilities = 182000m },
                new { Month = "Dec", Assets = 1250000m, Liabilities = 180000m }
            };

            foreach (var dataPoint in mockData)
            {
                AssetsData.Add(new MonthlyDataPoint(dataPoint.Month, (double)dataPoint.Assets));
                // Liabilities stored as negative values for stacked area chart
                LiabilitiesData.Add(new MonthlyDataPoint(dataPoint.Month, (double)-dataPoint.Liabilities));
                // Net Worth = Assets - Liabilities
                NetWorthData.Add(new MonthlyDataPoint(dataPoint.Month, (double)(dataPoint.Assets - dataPoint.Liabilities)));
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
            // var netWorthData = await _analyticsService.GetNetWorthTrendAsync(startDate, endDate);
            // AssetsData.Clear();
            // LiabilitiesData.Clear();
            // NetWorthData.Clear();
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
}
