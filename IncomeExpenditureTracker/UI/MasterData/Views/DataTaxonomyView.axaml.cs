using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class DataTaxonomyView : UserControl
    {
        public DataTaxonomyView()
        {
            InitializeComponent();

            // 1. Subscribe to the event that fires when the UI is fully rendered on screen
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // 2. Grab the ViewModel and explicitly tell it to fetch the data
            if (DataContext is DataTaxonomyViewModel vm)
            {
                // This executes the [RelayCommand] InitializeAsync
                vm.InitializeCommand.Execute(null);
            }
        }
    }
}