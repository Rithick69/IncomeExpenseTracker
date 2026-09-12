using Avalonia.Controls;

namespace IncomeExpenditureTracker.UI.Shell
{
    /// <summary>
    /// Code-behind for the DashboardView.
    /// Strictly adheres to MVVM principles: contains no business logic or direct data manipulation.
    /// All state and UI logic is driven by DashboardViewModel.cs.
    /// </summary>
    public partial class DashboardView : UserControl
    {
        public DashboardView()
        {
            InitializeComponent();
        }
    }
}