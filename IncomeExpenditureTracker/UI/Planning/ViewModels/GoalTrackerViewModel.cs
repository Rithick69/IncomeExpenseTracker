using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Planning;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Planning.ViewModels
{
    /// <summary>
    /// Child ViewModel for the Goal Tracker tab within PlanningHub.
    /// Displays user-defined financial goals as cards with progress tracking.
    /// Registered as Transient to ensure clean state on each navigation.
    /// </summary>
    public partial class GoalTrackerViewModel : ViewModelBase
    {
        private readonly IPlanningService _planningService;

        // ============================================================================
        // OBSERVABLE PROPERTIES
        // ============================================================================

        /// <summary>
        /// Collection of financial goals displayed as cards in a wrap grid.
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<GoalCardViewModel> _goals = new();

        /// <summary>
        /// Controls visibility of the right slide-in panel for adding new goals.
        /// </summary>
        [ObservableProperty]
        private bool _isAddGoalPanelVisible = false;

        // ============================================================================
        // CONSTRUCTOR & INITIALIZATION
        // ============================================================================

        public GoalTrackerViewModel(
            IApplicationBroker broker,
            IPlanningService planningService) : base(broker)
        {
            _planningService = planningService ?? throw new ArgumentNullException(nameof(planningService));

            // Load mocked goals on initialization
            _ = LoadGoalsAsync();
        }

        // ============================================================================
        // DATA LOADING (Mocked for now)
        // ============================================================================

        /// <summary>
        /// Fetches financial goals from IPlanningService and populates the Goals collection.
        /// In production, this will query the database through IDatabaseService.
        /// Currently uses mocked data for UI development.
        /// </summary>
        private async Task LoadGoalsAsync()
        {
            // TODO: Replace with actual service call when IPlanningService is implemented
            // var goals = await _planningService.GetActiveGoalsAsync();

            // For now, use mocked data to demonstrate the UI layout
            Goals.Clear();

            // Mocked Goal 1: Emergency Fund
            Goals.Add(new GoalCardViewModel
            {
                GoalName = "Emergency Fund",
                TargetAmount = 500000m,
                CurrentAmount = 185000m,
                CompletionPercentage = 37.0,
                LinkedTagId = 1,
                LinkedTagName = "Savings"
            });

            // Mocked Goal 2: New Laptop Purchase
            Goals.Add(new GoalCardViewModel
            {
                GoalName = "New Laptop",
                TargetAmount = 120000m,
                CurrentAmount = 72000m,
                CompletionPercentage = 60.0,
                LinkedTagId = 2,
                LinkedTagName = "Tech"
            });

            // Mocked Goal 3: Vacation Fund
            Goals.Add(new GoalCardViewModel
            {
                GoalName = "Vacation to Goa",
                TargetAmount = 80000m,
                CurrentAmount = 15000m,
                CompletionPercentage = 18.75,
                LinkedTagId = 3,
                LinkedTagName = "Travel"
            });

            // Mocked Goal 4: Home Down Payment
            Goals.Add(new GoalCardViewModel
            {
                GoalName = "Home Down Payment",
                TargetAmount = 2000000m,
                CurrentAmount = 450000m,
                CompletionPercentage = 22.5,
                LinkedTagId = 4,
                LinkedTagName = "Housing"
            });

            await Task.CompletedTask;
        }

        // ============================================================================
        // RELAY COMMANDS
        // ============================================================================

        /// <summary>
        /// Toggles the visibility of the "Add New Goal" slide-in panel.
        /// The panel has Z-Index 105 to overlay the main content.
        /// </summary>
        [RelayCommand]
        private void ToggleAddGoalPanel()
        {
            IsAddGoalPanelVisible = !IsAddGoalPanelVisible;
        }

        /// <summary>
        /// Refreshes the goals list by re-querying IPlanningService.
        /// Useful when goals are updated externally or after creating a new goal.
        /// </summary>
        [RelayCommand]
        private async Task RefreshGoalsAsync()
        {
            await LoadGoalsAsync();
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
    /// ViewModel representing a single goal card in the wrap grid.
    /// Contains all properties needed for card display and progress visualization.
    /// </summary>
    public partial class GoalCardViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _goalName = string.Empty;

        [ObservableProperty]
        private decimal _targetAmount;

        [ObservableProperty]
        private decimal _currentAmount;

        [ObservableProperty]
        private double _completionPercentage;

        [ObservableProperty]
        private long? _linkedTagId;

        [ObservableProperty]
        private string? _linkedTagName;

        /// <summary>
        /// Formatted display string for current progress (e.g., "₹1,85,000 / ₹5,00,000").
        /// </summary>
        public string ProgressDisplay => $"₹{CurrentAmount:N0} / ₹{TargetAmount:N0}";
    }
}
