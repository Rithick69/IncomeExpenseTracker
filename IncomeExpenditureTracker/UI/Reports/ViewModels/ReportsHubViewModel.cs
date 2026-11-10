using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Services.Analytics;
using IncomeExpenditureTracker.Services.Export;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Reports.ViewModels
{
    /// <summary>
    /// Parent SPA router for all analytical reports.
    /// Manages tab navigation between SpendTracker, CashFlow, Net Worth, and AIS Tax reports.
    /// Registered as Transient to ensure clean state on each navigation.
    /// </summary>
    public partial class ReportsHubViewModel : ViewModelBase
    {
        private readonly IAnalyticsService _analyticsService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Currently selected tab index (0 = SpendTracker, 1 = CashFlow, etc.)
        /// </summary>
        [ObservableProperty]
        private int _selectedTabIndex = 0;

        /// <summary>
        /// Start date for the reporting period (shared across all child tabs)
        /// </summary>
        [ObservableProperty]
        private DateTime _startDate;

        /// <summary>
        /// End date for the reporting period (shared across all child tabs)
        /// </summary>
        [ObservableProperty]
        private DateTime _endDate;

        /// <summary>
        /// Child ViewModel for the SpendTracker tab
        /// </summary>
        [ObservableProperty]
        private SpendTrackerViewModel? _spendTrackerViewModel;

        /// <summary>
        /// Child ViewModel for the Tag Insights tab
        /// </summary>
        [ObservableProperty]
        private TagInsightsViewModel? _tagInsightsViewModel;

        /// <summary>
        /// Child ViewModel for the Net Worth tab
        /// </summary>
        [ObservableProperty]
        private NetWorthViewModel? _netWorthViewModel;

        /// <summary>
        /// Child ViewModel for the AIS Report tab
        /// </summary>
        [ObservableProperty]
        private AisReportViewModel? _aisReportViewModel;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public ReportsHubViewModel(
            IApplicationBroker broker,
            IAnalyticsService analyticsService,
            IExportService exportService) : base(broker)
        {
            _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));

            // Default to current financial year (April 1st to March 31st for Indian users)
            // Or last 12 months for generic usage
            EndDate = DateTime.Today;
            StartDate = EndDate.AddMonths(-12);

            // Instantiate child ViewModels with the same service dependencies
            // NOTE: In production, these would be injected via DI container as Transient instances
            SpendTrackerViewModel = new SpendTrackerViewModel(broker, analyticsService);
            TagInsightsViewModel = new TagInsightsViewModel(broker, analyticsService, exportService);
            NetWorthViewModel = new NetWorthViewModel(broker, analyticsService, exportService);
            AisReportViewModel = new AisReportViewModel(broker, analyticsService, exportService);
        }

        // ============================================================================
        // RELAY COMMANDS
        // ============================================================================

        /// <summary>
        /// Exports the currently active report to Excel.
        /// Routes through IExportService to generate the file using ClosedXML.
        /// </summary>
        [RelayCommand]
        private async Task ExportToExcelAsync()
        {
            // Determine which report is active based on SelectedTabIndex
            var reportType = SelectedTabIndex switch
            {
                0 => "SpendTracker",
                1 => "TagInsights",
                2 => "NetWorth",
                3 => "AISTax",
                _ => "Unknown"
            };

            // TODO: Show file save dialog to let user pick output path
            // For now, use a default path (this will be implemented when IExportService is fully built)
            await _exportService.ExportToExcelAsync(reportType, StartDate, EndDate);

            // TODO: Show success toast notification via IApplicationBroker
            // Broker.Send(new ToastNotificationMessage("Report exported successfully!", ToastSeverity.Success));
        }

        /// <summary>
        /// Refreshes the active child tab with updated date range.
        /// Triggers data reload in the child ViewModel.
        /// </summary>
        [RelayCommand]
        private void RefreshReport()
        {
            // Propagate date range changes to the active child ViewModel
            switch (SelectedTabIndex)
            {
                case 0:
                    SpendTrackerViewModel?.UpdateDateRange(StartDate, EndDate);
                    break;
                case 1:
                    // TagInsightsViewModel doesn't use date range, it filters by tag
                    break;
                case 2:
                    NetWorthViewModel?.UpdateDateRange(StartDate, EndDate);
                    break;
                case 3:
                    // AisReportViewModel uses financial year, not date range
                    break;
            }
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            // Clean up child ViewModels to prevent memory leaks
            SpendTrackerViewModel?.Dispose();
            TagInsightsViewModel?.Dispose();
            NetWorthViewModel?.Dispose();
            AisReportViewModel?.Dispose();

            // Unregister broker subscriptions to prevent memory leaks
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }
}
