using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Services.Export;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Planning;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Planning.ViewModels
{
    /// <summary>
    /// Detail ViewModel for viewing and editing a specific financial simulation.
    /// Displays amortization/compounding schedules in a DataGrid with tabular numeric formatting.
    /// Supports parameter editing and Excel export via IExportService.
    /// Registered as Transient and instantiated on-demand when a profile is opened.
    /// </summary>
    public partial class ActiveSimulationViewModel : ViewModelBase
    {
        private readonly IPlanningService _planningService;
        private readonly IExportService _exportService;
        private readonly long _profileId;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Name of the simulation profile being viewed (e.g., "SIP XIRR - Tech Stocks").
        /// </summary>
        [ObservableProperty]
        private string _profileName = string.Empty;

        /// <summary>
        /// Collection of schedule rows for the amortization/compounding table.
        /// Each row represents a month/period with Principal, Interest, and Balance.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<ScheduleRowViewModel> _scheduleRows = new();

        /// <summary>
        /// Controls visibility of the parameter edit panel.
        /// </summary>
        [ObservableProperty]
        private bool _isEditParametersPanelVisible = false;

        // Editable Parameters (for the edit panel)

        /// <summary>
        /// Interest rate parameter (e.g., 8.5 for 8.5% p.a.).
        /// </summary>
        [ObservableProperty]
        private decimal _interestRate = 8.5m;

        /// <summary>
        /// Target corpus or principal amount parameter.
        /// </summary>
        [ObservableProperty]
        private decimal _targetCorpus = 500000m;

        /// <summary>
        /// Monthly contribution or EMI amount parameter.
        /// </summary>
        [ObservableProperty]
        private decimal _monthlyAmount = 10000m;

        /// <summary>
        /// Tenure in months parameter.
        /// </summary>
        [ObservableProperty]
        private int _tenureMonths = 60;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public ActiveSimulationViewModel(
            IApplicationBroker broker,
            IPlanningService planningService,
            IExportService exportService,
            long profileId,
            string profileName) : base(broker)
        {
            _planningService = planningService ?? throw new ArgumentNullException(nameof(planningService));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));
            _profileId = profileId;
            _profileName = profileName;

            // Load simulation data on initialization
            _ = LoadSimulationDataAsync();
        }

        // ============================================================================
        // DATA LOADING (Mocked for now)
        // ============================================================================

        /// <summary>
        /// Fetches simulation schedule data from IPlanningService and populates the ScheduleRows collection.
        /// In production, this will query the database through IDatabaseService or compute on-the-fly.
        /// Currently uses mocked amortization/compounding data for UI development.
        /// WHY: Demonstrates DataGrid with tabular numeric formatting (FontFeatures="tnum").
        /// </summary>
        private async Task LoadSimulationDataAsync()
        {
            // TODO: Replace with actual service call when IPlanningService is implemented
            // var schedule = await _planningService.GetSimulationScheduleAsync(_profileId);

            // For now, generate mocked amortization schedule data
            // Simulates a loan/investment scenario with monthly compounding
            ScheduleRows.Clear();

            decimal principal = 500000m; // Starting principal
            decimal monthlyRate = InterestRate / 100m / 12m; // Monthly interest rate
            decimal balance = principal;

            // Generate 60 months of schedule data
            for (int month = 1; month <= 60; month++)
            {
                // Calculate interest for this month
                decimal interest = balance * monthlyRate;

                // Calculate principal payment (simplified EMI logic)
                decimal principalPayment = MonthlyAmount - interest;

                // Update balance
                balance -= principalPayment;

                // Ensure balance doesn't go negative (simplified)
                if (balance < 0) balance = 0;

                ScheduleRows.Add(new ScheduleRowViewModel
                {
                    Month = month,
                    Principal = principalPayment,
                    Interest = interest,
                    Balance = balance
                });
            }

            await Task.CompletedTask;
        }

        // ============================================================================
        // RELAY COMMANDS
        // ============================================================================

        /// <summary>
        /// Toggles the visibility of the parameter edit panel.
        /// Allows user to modify simulation parameters and regenerate the schedule.
        /// </summary>
        [RelayCommand]
        private void ToggleEditParametersPanel()
        {
            IsEditParametersPanelVisible = !IsEditParametersPanelVisible;
        }

        /// <summary>
        /// Saves the edited parameters and regenerates the simulation schedule.
        /// In production, this will persist changes via IPlanningService.
        /// WHY: Allows real-time "what-if" analysis by tweaking parameters.
        /// </summary>
        [RelayCommand]
        private async Task SaveParametersAsync()
        {
            // TODO: Validate input fields before proceeding

            // TODO: Persist parameter changes via IPlanningService
            // await _planningService.UpdateSimulationParametersAsync(_profileId, InterestRate, TargetCorpus, MonthlyAmount, TenureMonths);

            // Regenerate the schedule with new parameters
            await LoadSimulationDataAsync();

            // Close the edit panel
            IsEditParametersPanelVisible = false;

            // TODO: Show success toast notification via IApplicationBroker
            // Broker.Send(new ToastNotificationMessage("Parameters updated successfully!", ToastSeverity.Success));
        }

        /// <summary>
        /// Exports the current simulation schedule to Excel via IExportService.
        /// WHY: Allows users to perform external analysis or share reports.
        /// </summary>
        [RelayCommand]
        private async Task ExportToExcelAsync()
        {
            // TODO: Call IExportService to generate Excel file with the schedule
            // await _exportService.ExportSimulationScheduleAsync(_profileId, ProfileName, ScheduleRows);

            // TODO: Show file save dialog to let user pick output path

            // TODO: Show success toast notification via IApplicationBroker
            // Broker.Send(new ToastNotificationMessage("Schedule exported to Excel!", ToastSeverity.Success));

            await Task.CompletedTask;
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            // Unregister broker subscriptions to prevent memory leaks
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }

    /// <summary>
    /// ViewModel representing a single row in the amortization/compounding schedule DataGrid.
    /// Contains Month, Principal, Interest, and Balance columns with tabular numeric formatting.
    /// WHY: ObservableObject enables property change notifications for dynamic grid updates.
    /// </summary>
    public partial class ScheduleRowViewModel : ObservableObject
    {
        [ObservableProperty]
        private int _month;

        [ObservableProperty]
        private decimal _principal;

        [ObservableProperty]
        private decimal _interest;

        [ObservableProperty]
        private decimal _balance;

        /// <summary>
        /// Formatted principal amount display (e.g., "₹8,234.56").
        /// WHY: DataGrid cells will use this for consistent currency formatting.
        /// </summary>
        public string PrincipalDisplay => $"₹{Principal:N2}";

        /// <summary>
        /// Formatted interest amount display (e.g., "₹1,765.44").
        /// </summary>
        public string InterestDisplay => $"₹{Interest:N2}";

        /// <summary>
        /// Formatted balance display (e.g., "₹4,91,765.44").
        /// </summary>
        public string BalanceDisplay => $"₹{Balance:N2}";
    }
}
