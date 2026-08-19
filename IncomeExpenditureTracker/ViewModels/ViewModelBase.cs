using System;
using CommunityToolkit.Mvvm.ComponentModel;
using IncomeExpenditureTracker.Services.Messaging;

/* ============================================================================
 * MVVM ARCHITECTURE QUICK REFERENCE (The Restaurant Analogy)
 * ============================================================================
 *
 * 1. MODEL (The Kitchen / Backend)
 *    - Role: Core business logic, data extraction, and database persistence.
 *    - Examples: StatementManager, SQLite database, FileStagingError.
 *    - Rule: NEVER knows about the UI. Focuses purely on data and processing.
 *
 * 2. VIEWMODEL (The Waiter / The Bridge)
 *    - Role: Translates backend data into UI-friendly observable properties.
 *    - Examples: MainWindowViewModel, ViewModelBase.
 *    - Rule: Subscribes to backend events (via the Broker) and updates its
 *            "Observable" trays. It does NOT know about specific visual
 *            controls (like buttons, grids, or colors).
 *
 * 3. VIEW (The Customer / The Dining Room)
 *    - Role: The actual visual layout seen and interacted with by the user.
 *    - Examples: MainWindow.axaml.
 *    - Rule: Contains zero business logic. It "Binds" to the ViewModel's
 *            properties to automatically update the screen when data changes.
 * ============================================================================
 */

namespace IncomeExpenditureTracker.ViewModels
{
    // 1. ObservableObject wires up the magical data-binding UI notifications
    // 2. IDisposable ensures we clean up our broker subscriptions
    public abstract class ViewModelBase : ObservableObject, IDisposable
    {
        // Protected so child ViewModels can access the postman
        protected readonly IApplicationBroker Broker;
        private bool _isDisposed;

        // Force all ViewModels to ask for the broker via Dependency Injection
        protected ViewModelBase(IApplicationBroker broker)
        {
            Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        }

        /// <summary>
        /// Centralized cleanup routine.
        /// Called automatically by the DI container or parent view when the ViewModel is destroyed.
        /// </summary>
        public virtual void Dispose()
        {
            if (_isDisposed) return;

            // SECURITY: Instantly cut off all mail delivery to this ViewModel to prevent memory leaks
            Broker.UnregisterAll(this);

            _isDisposed = true;
            GC.SuppressFinalize(this);
        }
    }
}