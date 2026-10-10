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
            // Note: Adjust the payload of NavigationMessage based on how
            // your specific enum or string routing dictionary is set up.
            _broker.Send(new NavigationMessage("DataTaxonomy"));
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