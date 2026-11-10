using Avalonia.Controls;

namespace IncomeExpenditureTracker.UI.Reports.Views
{
    /// <summary>
    /// Code-behind for SpendTrackerView.
    /// Child tab displaying monthly income/expense trends and category-wise expense breakdowns.
    /// Charts are rendered using LiveCharts2 (requires package installation).
    /// </summary>
    public partial class SpendTrackerView : UserControl
    {
        public SpendTrackerView()
        {
            InitializeComponent();
        }
    }
}
