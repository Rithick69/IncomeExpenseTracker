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
    /// Child ViewModel for the Simulators tab within PlanningHub.
    /// Displays saved simulation profiles (XIRR, Fixed Income, FIRE) as a dashboard.
    /// Allows creating new simulations and navigating to detailed simulation views.
    /// Registered as Transient to ensure clean state on each navigation.
    /// </summary>
    public partial class SimulatorsViewModel : ViewModelBase
    {
        private readonly IPlanningService _planningService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Collection of saved simulator profiles displayed as cards in the dashboard.
        /// Each profile represents a different financial scenario (XIRR, Fixed Income, FIRE, etc.)
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<SimulatorProfileCardViewModel> _profiles = new();

        /// <summary>
        /// Controls visibility of the right slide-in panel for creating a new simulation.
        /// Panel has Z-Index 105 to overlay the main content.
        /// </summary>
        [ObservableProperty]
        private bool _isNewSimulationPanelVisible = false;

        /// <summary>
        /// The currently active simulation view (null when on dashboard, populated when viewing a specific simulation).
        /// Used to navigate between dashboard and detail views.
        /// </summary>
        [ObservableProperty]
        private ActiveSimulationViewModel? _activeSimulation;

        /// <summary>
        /// Controls whether to show the dashboard (true) or the active simulation detail view (false).
        /// </summary>
        [ObservableProperty]
        private bool _isDashboardVisible = true;

        // New Simulation Panel Input Fields

        /// <summary>
        /// Selected account/tag from the AutoCompleteBox for the new simulation.
        /// In production, this will be bound to an Account or Tag entity.
        /// </summary>
        [ObservableProperty]
        private string _selectedAccountTag = string.Empty;

        /// <summary>
        /// Custom interest rate parameter for the new simulation (e.g., 8.5 for 8.5%).
        /// </summary>
        [ObservableProperty]
        private decimal _interestRate;

        /// <summary>
        /// Custom target corpus parameter for the new simulation.
        /// </summary>
        [ObservableProperty]
        private decimal _targetCorpus;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public SimulatorsViewModel(
            IApplicationBroker broker,
            IPlanningService planningService,
            IExportService exportService) : base(broker)
        {
            _planningService = planningService ?? throw new ArgumentNullException(nameof(planningService));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));

            // Load mocked simulator profiles on initialization
            _ = LoadProfilesAsync();
        }

        // ============================================================================
        // DATA LOADING (Mocked for now)
        // ============================================================================

        /// <summary>
        /// Fetches saved simulator profiles from IPlanningService and populates the Profiles collection.
        /// In production, this will query the database through IDatabaseService.
        /// Currently uses mocked data for UI development.
        /// </summary>
        private async Task LoadProfilesAsync()
        {
            // TODO: Replace with actual service call when IPlanningService is implemented
            // var profiles = await _planningService.GetSavedSimulatorProfilesAsync();

            // For now, use mocked data to demonstrate the UI layout
            Profiles.Clear();

            // Mocked Profile 1: XIRR Calculation
            Profiles.Add(new SimulatorProfileCardViewModel
            {
                ProfileId = 1,
                ProfileName = "SIP XIRR - Tech Stocks",
                SimulationType = "XIRR",
                Description = "Monthly ₹10,000 SIP with 12.5% annual returns",
                LastModified = DateTime.Now.AddDays(-5)
            });

            // Mocked Profile 2: Fixed Income Projection
            Profiles.Add(new SimulatorProfileCardViewModel
            {
                ProfileId = 2,
                ProfileName = "Fixed Deposit - Emergency Fund",
                SimulationType = "Fixed Income",
                Description = "₹5,00,000 principal at 7.5% p.a. for 5 years",
                LastModified = DateTime.Now.AddDays(-12)
            });

            // Mocked Profile 3: FIRE Calculation
            Profiles.Add(new SimulatorProfileCardViewModel
            {
                ProfileId = 3,
                ProfileName = "FIRE Goal - Age 45",
                SimulationType = "FIRE",
                Description = "₹2 Cr target corpus with ₹50,000 monthly expenses",
                LastModified = DateTime.Now.AddDays(-20)
            });

            // Mocked Profile 4: Loan Amortization
            Profiles.Add(new SimulatorProfileCardViewModel
            {
                ProfileId = 4,
                ProfileName = "Home Loan EMI Schedule",
                SimulationType = "Loan Amortization",
                Description = "₹50L loan at 8.5% for 20 years",
                LastModified = DateTime.Now.AddDays(-30)
            });

            await Task.CompletedTask;
        }

        // ============================================================================
        // RELAY COMMANDS
        // ============================================================================

        /// <summary>
        /// Toggles the visibility of the "New Simulation" slide-in panel.
        /// The panel has Z-Index 105 to overlay the main dashboard content.
        /// </summary>
        [RelayCommand]
        private void ToggleNewSimulationPanel()
        {
            IsNewSimulationPanelVisible = !IsNewSimulationPanelVisible;
        }

        /// <summary>
        /// Opens a saved simulator profile in detail view.
        /// Hides the dashboard and displays the ActiveSimulationView with the selected profile's data.
        /// </summary>
        [RelayCommand]
        private void OpenProfile(SimulatorProfileCardViewModel profile)
        {
            if (profile == null) return;

            // Create a new ActiveSimulationViewModel with the selected profile's data
            // In production, this will load data from IPlanningService based on profileId
            ActiveSimulation = new ActiveSimulationViewModel(
                Broker,
                _planningService,
                _exportService,
                profile.ProfileId,
                profile.ProfileName);

            // Hide dashboard and show detail view
            IsDashboardVisible = false;
        }

        /// <summary>
        /// Returns from the active simulation detail view back to the dashboard.
        /// Disposes the ActiveSimulationViewModel to free resources.
        /// </summary>
        [RelayCommand]
        private void BackToDashboard()
        {
            // Dispose the active simulation ViewModel to prevent memory leaks
            ActiveSimulation?.Dispose();
            ActiveSimulation = null;

            // Show dashboard
            IsDashboardVisible = true;
        }

        /// <summary>
        /// Creates a new simulation profile with user-provided parameters.
        /// In production, this will persist to database via IPlanningService.
        /// Currently mocked for UI development.
        /// </summary>
        [RelayCommand]
        private async Task CreateSimulationAsync()
        {
            // TODO: Validate input fields before proceeding

            // TODO: Call IPlanningService to persist the new simulation profile
            // var newProfileId = await _planningService.CreateSimulatorProfileAsync(
            //     selectedAccountTag, interestRate, targetCorpus);

            // Close the panel
            IsNewSimulationPanelVisible = false;

            // Refresh the profiles list
            await LoadProfilesAsync();

            // TODO: Show success toast notification via IApplicationBroker
            // Broker.Send(new ToastNotificationMessage("Simulation created successfully!", ToastSeverity.Success));
        }

        /// <summary>
        /// Refreshes the simulator profiles list by re-querying IPlanningService.
        /// Useful when profiles are updated externally or after creating a new simulation.
        /// </summary>
        [RelayCommand]
        private async Task RefreshProfilesAsync()
        {
            await LoadProfilesAsync();
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            // Clean up the active simulation ViewModel if present
            ActiveSimulation?.Dispose();

            // Unregister broker subscriptions to prevent memory leaks
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }

    /// <summary>
    /// ViewModel representing a single simulator profile card in the dashboard.
    /// Contains all properties needed for card display and navigation to detail view.
    /// </summary>
    public partial class SimulatorProfileCardViewModel : ObservableObject
    {
        [ObservableProperty]
        private long _profileId;

        [ObservableProperty]
        private string _profileName = string.Empty;

        [ObservableProperty]
        private string _simulationType = string.Empty;

        [ObservableProperty]
        private string _description = string.Empty;

        [ObservableProperty]
        private DateTime _lastModified;

        /// <summary>
        /// Formatted display string for last modified date (e.g., "Modified 5 days ago").
        /// </summary>
        public string LastModifiedDisplay => $"Modified {FormatRelativeTime(LastModified)}";

        private static string FormatRelativeTime(DateTime dateTime)
        {
            var timeSpan = DateTime.Now - dateTime;

            if (timeSpan.TotalDays >= 30)
                return $"{(int)(timeSpan.TotalDays / 30)} month(s) ago";
            if (timeSpan.TotalDays >= 1)
                return $"{(int)timeSpan.TotalDays} day(s) ago";
            if (timeSpan.TotalHours >= 1)
                return $"{(int)timeSpan.TotalHours} hour(s) ago";

            return "Just now";
        }
    }
}
