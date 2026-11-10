using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Export;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Planning;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Planning.ViewModels
{
    /// <summary>
    /// Parent SPA router for all financial planning and forecasting features.
    /// Manages tab navigation between Goal Tracker and Financial Simulators.
    /// Registered as Transient to ensure clean state on each navigation.
    /// </summary>
    public partial class PlanningHubViewModel : ViewModelBase
    {
        private readonly IPlanningService _planningService;
        private readonly IExportService _exportService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Currently selected tab index (0 = Goal Tracker, 1 = Financial Simulator, etc.)
        /// </summary>
        [ObservableProperty]
        private int _selectedTabIndex = 0;

        /// <summary>
        /// Child ViewModel for the Goal Tracker tab
        /// </summary>
        [ObservableProperty]
        private GoalTrackerViewModel? _goalTrackerViewModel;

        /// <summary>
        /// Child ViewModel for the Simulators tab
        /// </summary>
        [ObservableProperty]
        private SimulatorsViewModel? _simulatorsViewModel;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public PlanningHubViewModel(
            IApplicationBroker broker,
            IPlanningService planningService,
            IExportService exportService) : base(broker)
        {
            _planningService = planningService ?? throw new ArgumentNullException(nameof(planningService));
            _exportService = exportService ?? throw new ArgumentNullException(nameof(exportService));

            // Instantiate child ViewModels with the same service dependencies
            // NOTE: In production, these would be injected via DI container as Transient instances
            GoalTrackerViewModel = new GoalTrackerViewModel(broker, planningService);
            SimulatorsViewModel = new SimulatorsViewModel(broker, planningService, exportService);
        }

        // ============================================================================
        // LIFECYCLE & DISPOSAL
        // ============================================================================

        public override void Dispose()
        {
            // Clean up child ViewModels to prevent memory leaks
            GoalTrackerViewModel?.Dispose();
            SimulatorsViewModel?.Dispose();

            // Unregister broker subscriptions to prevent memory leaks
            Broker.UnregisterAll(this);
            base.Dispose();
        }
    }
}
