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
        // LIVECHARTS2 SERIES COLLECTIONS
        // ============================================================================

        /// <summary>
        /// Series collection for the net worth stacked area chart
        /// </summary>
        public ISeries[] NetWorthSeries { get; set; } = Array.Empty<ISeries>();

        /// <summary>
        /// X-Axis configuration for the net worth chart
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
            var assetsValues = new double[] { 950000, 970000, 990000, 1010000, 1030000, 1060000, 1090000, 1120000, 1150000, 1180000, 1210000, 1250000 };
            var liabilitiesValues = new double[] { 220000, 215000, 210000, 205000, 200000, 195000, 190000, 188000, 186000, 184000, 182000, 180000 };
            var netWorthValues = new double[] { 730000, 755000, 780000, 805000, 830000, 865000, 900000, 932000, 964000, 996000, 1028000, 1070000 };

            // Create LiveCharts2 Stacked Area Series for Net Worth Visualization
            NetWorthSeries = new ISeries[]
            {
                new StackedAreaSeries<double>
                {
                    Name = "Assets",
                    Values = assetsValues,
                    Fill = new SolidColorPaint(SKColors.Green.WithAlpha(100)),
                    Stroke = new SolidColorPaint(SKColors.Green) { StrokeThickness = 2 },
                    GeometrySize = 0
                },
                new StackedAreaSeries<double>
                {
                    Name = "Liabilities",
                    Values = liabilitiesValues,
                    Fill = new SolidColorPaint(SKColors.Red.WithAlpha(100)),
                    Stroke = new SolidColorPaint(SKColors.Red) { StrokeThickness = 2 },
                    GeometrySize = 0
                },
                new LineSeries<double>
                {
                    Name = "Net Worth",
                    Values = netWorthValues,
                    Fill = null,
                    Stroke = new SolidColorPaint(SKColors.Blue) { StrokeThickness = 3 },
                    GeometryStroke = new SolidColorPaint(SKColors.Blue) { StrokeThickness = 3 },
                    GeometryFill = new SolidColorPaint(SKColors.White),
                    GeometrySize = 6
                }
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
