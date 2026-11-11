using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Dashboard;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;

namespace IncomeExpenditureTracker.UI.Dashboard.ViewModels
{
    /// <summary>
    /// Lightweight landing page ViewModel serving as the application's high-speed entry point.
    /// Displays KPI spark-cards, system alerts, visual pulse charts, and quick navigation.
    /// Registered as Transient to ensure fresh state on each dashboard navigation.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        private readonly IDashboardService _dashboardService;

        // ============================================================================
        // ZONE 1: WELCOME & DYNAMIC TRIAGE CENTER
        // ============================================================================

        /// <summary>
        /// User's profile display name for personalized greeting.
        /// In production, this would be fetched from IProfileRegistry.
        /// </summary>
        [ObservableProperty]
        private string _profileName = "John Doe";

        /// <summary>
        /// Collection of system diagnostic alerts (e.g., unmapped transactions, data faults).
        /// Dynamically populated based on backend analysis.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<AlertBannerDTO> _systemAlerts;

        // ============================================================================
        // ZONE 2: KPI SPARK-CARDS
        // ============================================================================

        /// <summary>
        /// Month-to-Date total cash inflow (credits).
        /// </summary>
        [ObservableProperty]
        private decimal _mtdInflow;

        /// <summary>
        /// Month-to-Date total cash outflow (debits).
        /// </summary>
        [ObservableProperty]
        private decimal _mtdOutflow;

        /// <summary>
        /// Net Cash Flow (Inflow - Outflow) for the current month.
        /// </summary>
        [ObservableProperty]
        private decimal _mtdNetCashFlow;

        /// <summary>
        /// Current Net Worth (Total Assets - Total Liabilities).
        /// </summary>
        [ObservableProperty]
        private decimal _currentNetWorth;

        /// <summary>
        /// Active financial goal name (e.g., "Emergency Fund", "Vacation Savings").
        /// </summary>
        [ObservableProperty]
        private string _activeGoalName = string.Empty;

        /// <summary>
        /// Active goal target amount.
        /// </summary>
        [ObservableProperty]
        private decimal _activeGoalTarget;

        /// <summary>
        /// Active goal current saved amount.
        /// </summary>
        [ObservableProperty]
        private decimal _activeGoalCurrent;

        /// <summary>
        /// Active goal completion percentage (0-100).
        /// </summary>
        [ObservableProperty]
        private double _activeGoalPercentage;

        // ============================================================================
        // ZONE 3: VISUAL PULSE (30-Day Expense Sparkline)
        // ============================================================================

        /// <summary>
        /// LiveCharts2 series for the rolling 30-day expense sparkline.
        /// Uses a simple line chart to visualize daily spending trends.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<ISeries> _expenseSparklineSeries;

        // ============================================================================
        // ZONE 4: ACTIVITY & NAVIGATION
        // ============================================================================

        /// <summary>
        /// Recent transactions (maximum 5) for the activity feed.
        /// Displayed in a read-only DataGrid.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<RecentTransactionDTO> _recentTransactions;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public DashboardViewModel(
            IApplicationBroker broker,
            IDashboardService dashboardService) : base(broker)
        {
            _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));

            // Initialize collections
            _systemAlerts = new ObservableCollection<AlertBannerDTO>();
            _recentTransactions = new ObservableCollection<RecentTransactionDTO>();
            _expenseSparklineSeries = new ObservableCollection<ISeries>();

            // MOCK DATA: Populate immediately for UI rendering without backend dependencies
            LoadMockData();
        }

        /// <summary>
        /// Populates all Dashboard properties with mock data for scaffolding purposes.
        /// This ensures the UI renders immediately without requiring backend integration.
        /// WHY: Allows UI/UX testing and layout validation while backend services are being built.
        /// </summary>
        private void LoadMockData()
        {
            // ZONE 1: Mock System Alerts
            // WHY: Demonstrates conditional banner styling (Red for Critical, Yellow for Warning)
            SystemAlerts.Add(new AlertBannerDTO
            {
                Severity = AlertSeverity.Critical,
                Title = "Core Data Fault",
                Message = "23 transactions have missing payee assignments. Review recommended.",
                ActionLocaleKey = "Locale_Dashboard_Button_ReviewTransactions",
                NavigationDestination = "TransactionReview"
            });

            SystemAlerts.Add(new AlertBannerDTO
            {
                Severity = AlertSeverity.Warning,
                Title = "Taxonomy Incomplete",
                Message = "5 unmapped categories detected in recent imports.",
                ActionLocaleKey = "Locale_Dashboard_Button_ManageData",
                NavigationDestination = "DataTaxonomy"
            });

            SystemAlerts.Add(new AlertBannerDTO
            {
                Severity = AlertSeverity.Info,
                Title = "System Healthy",
                Message = "All transactions are properly tagged and reconciled.",
                ActionLocaleKey = null,
                NavigationDestination = null
            });

            // ZONE 2: Mock KPI Spark-Cards
            // WHY: Demonstrates data binding for numeric displays with currency formatting
            MtdInflow = 125430.50m;
            MtdOutflow = 87250.75m;
            MtdNetCashFlow = MtdInflow - MtdOutflow; // Auto-calculated

            CurrentNetWorth = 1523400.00m;

            // Mock Active Goal
            ActiveGoalName = "Emergency Fund";
            ActiveGoalTarget = 500000.00m;
            ActiveGoalCurrent = 327500.00m;
            ActiveGoalPercentage = (double)(ActiveGoalCurrent / ActiveGoalTarget * 100);

            // ZONE 3: Mock 30-Day Expense Sparkline
            // WHY: Demonstrates LiveCharts2 integration with a rolling time-series dataset
            var dailyExpenses = new ObservableCollection<double>();
            var today = DateTime.Today;

            // Generate 30 days of mock daily expense data with realistic variance
            var random = new Random(42); // Seeded for consistent mock data
            for (int i = 29; i >= 0; i--)
            {
                // Simulate daily expenses between ₹1,500 and ₹4,500
                var baseAmount = 2500.0;
                var variance = random.NextDouble() * 2000.0 - 1000.0;
                dailyExpenses.Add(baseAmount + variance);
            }

            // WHY: LineSeries provides a clean sparkline visualization without axes clutter
            ExpenseSparklineSeries.Add(new LineSeries<double>
            {
                Values = dailyExpenses,
                Fill = null, // No fill, just the line
                GeometrySize = 0, // No data point markers for cleaner sparkline
                LineSmoothness = 0.5 // Slight curve smoothing for visual polish
            });

            // ZONE 4: Mock Recent Transactions
            // WHY: Demonstrates DataGrid binding with diverse transaction types
            RecentTransactions.Add(new RecentTransactionDTO
            {
                TransactionId = 1,
                TransactionDate = DateTime.Today.AddDays(-1),
                Description = "SWIGGY BANGALORE",
                PayeeName = "Swiggy",
                TagName = "Food & Dining",
                DebitAmount = 385.50m,
                CreditAmount = null
            });

            RecentTransactions.Add(new RecentTransactionDTO
            {
                TransactionId = 2,
                TransactionDate = DateTime.Today.AddDays(-2),
                Description = "SALARY CREDIT",
                PayeeName = "Acme Corp",
                TagName = "Salary",
                DebitAmount = null,
                CreditAmount = 95000.00m
            });

            RecentTransactions.Add(new RecentTransactionDTO
            {
                TransactionId = 3,
                TransactionDate = DateTime.Today.AddDays(-3),
                Description = "AMAZON PAY",
                PayeeName = "Amazon",
                TagName = "Shopping",
                DebitAmount = 2450.00m,
                CreditAmount = null
            });

            RecentTransactions.Add(new RecentTransactionDTO
            {
                TransactionId = 4,
                TransactionDate = DateTime.Today.AddDays(-4),
                Description = "ELECTRICITY BILL",
                PayeeName = "BESCOM",
                TagName = "Utilities",
                DebitAmount = 1340.00m,
                CreditAmount = null
            });

            RecentTransactions.Add(new RecentTransactionDTO
            {
                TransactionId = 5,
                TransactionDate = DateTime.Today.AddDays(-5),
                Description = "ATM WITHDRAWAL",
                PayeeName = "HDFC Bank",
                TagName = "Cash",
                DebitAmount = 5000.00m,
                CreditAmount = null
            });
        }

        // ============================================================================
        // RELAY COMMANDS (NAVIGATION & ACTIONS)
        // ============================================================================

        /// <summary>
        /// Navigates to the Import Hub for statement ingestion.
        /// WHY: Quick-access CTA button for primary user workflow.
        /// </summary>
        [RelayCommand]
        private Task NavigateToImportAsync()
        {
            // WHY: Use the broker-based navigation pattern to maintain SPA routing consistency
            Broker.Send(new NavigationMessage("ImportHub"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Navigates to the AIS Tax Summary report.
        /// </summary>
        [RelayCommand]
        private Task NavigateToAisSummaryAsync()
        {
            Broker.Send(new NavigationMessage("Reports"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Navigates to the Planning & Simulators hub.
        /// </summary>
        [RelayCommand]
        private Task NavigateToSimulatorsAsync()
        {
            Broker.Send(new NavigationMessage("Planning"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Navigates to the Data & Taxonomy management screen.
        /// </summary>
        [RelayCommand]
        private Task NavigateToTaxonomyAsync()
        {
            Broker.Send(new NavigationMessage("DataTaxonomy"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Navigates to the full Transaction Ledger view.
        /// WHY: Allows users to drill down from the recent activity preview.
        /// </summary>
        [RelayCommand]
        private Task NavigateToLedgerAsync()
        {
            Broker.Send(new NavigationMessage("Ledger"));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Handles alert banner CTA button clicks.
        /// Routes navigation based on the alert's NavigationDestination property.
        /// </summary>
        [RelayCommand]
        private Task HandleAlertActionAsync(AlertBannerDTO alert)
        {
            if (!string.IsNullOrEmpty(alert.NavigationDestination))
            {
                Broker.Send(new NavigationMessage(alert.NavigationDestination));
            }
            return Task.CompletedTask;
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            // WHY: Unregister all broker subscriptions to prevent memory leaks
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }
}
