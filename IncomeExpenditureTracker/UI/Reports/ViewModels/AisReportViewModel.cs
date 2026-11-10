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
    /// Child ViewModel for the AIS (Annual Information Statement) report tab.
    /// Displays a high-density DataGrid grouped by Payee Name with Total Inflow and Outflow.
    /// Designed for Indian tax reporting requirements (Section 285BA of Income Tax Act).
    /// Populates with MOCK DATA for immediate UI rendering (no database queries).
    /// </summary>
    public partial class AisReportViewModel : ViewModelBase
    {
        private readonly IAnalyticsService _analyticsService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Selected financial year for the AIS report
        /// </summary>
        [ObservableProperty]
        private string _selectedFinancialYear = "FY 2024-25";

        /// <summary>
        /// Total inflow for the selected financial year
        /// </summary>
        [ObservableProperty]
        private decimal _totalInflow;

        /// <summary>
        /// Total outflow for the selected financial year
        /// </summary>
        [ObservableProperty]
        private decimal _totalOutflow;

        /// <summary>
        /// Net flow (Inflow - Outflow)
        /// </summary>
        [ObservableProperty]
        private decimal _netFlow;

        // ============================================================================
        // DATAGRID MOCK DATA COLLECTIONS
        // ============================================================================

        /// <summary>
        /// Mock payee-wise summary data for the high-density DataGrid
        /// </summary>
        public ObservableCollection<AisPayeeSummaryDto> PayeeSummaries { get; } = new();

        /// <summary>
        /// Available financial years for the dropdown selector
        /// </summary>
        public ObservableCollection<string> AvailableFinancialYears { get; } = new();

        // ============================================================================
        // CONSTRUCTOR & MOCK DATA INITIALIZATION
        // ============================================================================

        public AisReportViewModel(
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
        /// This demonstrates the expected data shape for the AIS tax report DataGrid.
        /// </summary>
        private void LoadMockData()
        {
            // Mock Available Financial Years for Dropdown
            // Indian FY: April 1st to March 31st
            AvailableFinancialYears.Add("FY 2024-25");
            AvailableFinancialYears.Add("FY 2023-24");
            AvailableFinancialYears.Add("FY 2022-23");
            AvailableFinancialYears.Add("FY 2021-22");

            // Mock Summary Values
            TotalInflow = 1850000m;
            TotalOutflow = 1320000m;
            NetFlow = TotalInflow - TotalOutflow; // 530,000

            // Mock Payee-wise Summary for DataGrid (Grouped by Payee Name)
            // This data is crucial for AIS tax reporting (Section 285BA)
            var mockPayeeSummaries = new[]
            {
                new AisPayeeSummaryDto("ABC Corporation Pvt Ltd", "Salary", 1500000m, 0m),
                new AisPayeeSummaryDto("XYZ Consulting Services", "Consulting Income", 250000m, 0m),
                new AisPayeeSummaryDto("Freelance Client - Tech Co", "Project Income", 100000m, 0m),
                new AisPayeeSummaryDto("HDFC Bank", "Interest Income", 15000m, 45000m),
                new AisPayeeSummaryDto("ICICI Credit Card", "Credit Card", 0m, 185000m),
                new AisPayeeSummaryDto("Axis Bank", "Banking Fees", 0m, 12000m),
                new AisPayeeSummaryDto("Amazon India", "E-commerce", 0m, 95000m),
                new AisPayeeSummaryDto("Flipkart", "E-commerce", 0m, 68000m),
                new AisPayeeSummaryDto("Swiggy", "Food Delivery", 0m, 42000m),
                new AisPayeeSummaryDto("Uber India", "Transportation", 0m, 38000m),
                new AisPayeeSummaryDto("Reliance Jio", "Telecom", 0m, 18000m),
                new AisPayeeSummaryDto("Maharashtra Electricity Board", "Utilities", 0m, 24000m),
                new AisPayeeSummaryDto("Apollo Hospitals", "Healthcare", 0m, 55000m),
                new AisPayeeSummaryDto("BookMyShow", "Entertainment", 0m, 15000m),
                new AisPayeeSummaryDto("BigBasket", "Groceries", 0m, 72000m),
                new AisPayeeSummaryDto("Various Retail Merchants", "Miscellaneous", 0m, 151000m)
            };

            foreach (var summary in mockPayeeSummaries)
            {
                PayeeSummaries.Add(summary);
            }
        }

        // ============================================================================
        // PUBLIC METHODS (Called by Parent ViewModel or User Actions)
        // ============================================================================

        /// <summary>
        /// Updates the selected financial year and reloads the report data.
        /// In production, this will trigger a fresh database query via IAnalyticsService.
        /// </summary>
        public void UpdateFinancialYear(string financialYear)
        {
            SelectedFinancialYear = financialYear;

            // TODO: In production, query IAnalyticsService with the new financial year
            // For now, the mock data remains static
            // Example:
            // var aisSummary = await _analyticsService.GetAisReportAsync(financialYear);
            // PayeeSummaries.Clear();
            // foreach (var summary in aisSummary.PayeeSummaries)
            // {
            //     PayeeSummaries.Add(summary);
            // }
            // TotalInflow = aisSummary.TotalInflow;
            // TotalOutflow = aisSummary.TotalOutflow;
            // NetFlow = aisSummary.NetFlow;
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
    /// Represents a payee-wise summary record for the AIS report DataGrid.
    /// Includes total inflow and outflow amounts for tax reporting purposes.
    /// </summary>
    public record AisPayeeSummaryDto(string PayeeName, string Category, decimal TotalInflow, decimal TotalOutflow)
    {
        /// <summary>
        /// Net amount for this payee (Inflow - Outflow)
        /// </summary>
        public decimal NetAmount => TotalInflow - TotalOutflow;
    }
}
