using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Messaging;
using System;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.UI.Shell
{
    /// <summary>
    /// Serves as the default landing route post-authentication.
    /// Registered as Transient to guarantee clean state instantiation upon navigation.
    /// Inherits ViewModelBase to enforce IDisposable and secure IApplicationBroker teardown.
    /// </summary>
    public partial class DashboardViewModel : ViewModelBase
    {
        private readonly IApplicationBroker _broker;

        // Constructor injection strictly enforced
        public DashboardViewModel(IApplicationBroker broker) : base(broker)
        {
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));

            // Note: Data-fetching logic (e.g., loading Net Worth summaries)
            // will eventually be triggered here or via an IAsyncInitialization pattern.
        }

        // =========================================================================
        // NAVIGATION COMMANDS
        // =========================================================================

        /// <summary>
        /// Broadcasts a NavigationMessage to the MainWindowViewModel router.
        /// The router will resolve a Transient instance of DataTaxonomyViewModel
        /// and seamlessly swap the ContentControl.
        /// </summary>
        [RelayCommand]
        public void NavigateToDataTaxonomy()
        {
            _broker.Send(new NavigationMessage("DataTaxonomy"));
        }

        /// <summary>
        /// Navigates to the Reports Hub for historical analytics and data visualization.
        /// The router will resolve a Transient instance of ReportsHubViewModel.
        /// </summary>
        [RelayCommand]
        public void NavigateToReportsHub()
        {
            _broker.Send(new NavigationMessage("ReportsHub"));
        }

        /// <summary>
        /// Navigates to the Planning Hub for financial forecasting and goal tracking.
        /// The router will resolve a Transient instance of PlanningHubViewModel.
        /// </summary>
        [RelayCommand]
        public void NavigateToPlanningHub()
        {
            _broker.Send(new NavigationMessage("PlanningHub"));
        }

        public override void Dispose()
        {
            // Guarantees all broker subscriptions are destroyed when navigating away,
            // preventing memory leaks and zombie background events.
            _broker.UnregisterAll(this);
            base.Dispose();
        }
    }
}