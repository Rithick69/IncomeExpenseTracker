using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class TagFormView : UserControl
    {
        public TagFormView()
        {
            InitializeComponent();

            // Hook into the view lifecycle to trigger the ViewModel's initialization
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            // Ensure the ViewModel's InitializeCommand is executed when the view is loaded
            if (DataContext is TagFormViewModel viewModel && viewModel.InitializeCommand.CanExecute(null))
            {
                // Execute the InitializeCommand to set up the ViewModel
                viewModel.InitializeCommand.Execute(null);
            }
        }

        // UX ENHANCEMENT: Instantly opens the dropdown list when the user clicks the field
        private void OnDropDownFocus(object? sender, GotFocusEventArgs e)
        {
            if (sender is AutoCompleteBox autoCompleteBox)
            {
                autoCompleteBox.IsDropDownOpen = true;
            }
        }
    }
}