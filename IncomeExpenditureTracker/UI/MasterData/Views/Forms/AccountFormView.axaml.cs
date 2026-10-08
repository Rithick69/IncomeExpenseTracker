using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class AccountFormView : UserControl
    {
        public AccountFormView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (DataContext is AccountFormViewModel viewModel && viewModel.InitializeCommand.CanExecute(null))
            {
                viewModel.InitializeCommand.Execute(null);
            }
        }

        // Instantly opens the institution dropdown list when the user clicks the field
        private void OnDropDownFocus(object? sender, GotFocusEventArgs e)
        {
            if (sender is AutoCompleteBox autoCompleteBox)
            {
                autoCompleteBox.IsDropDownOpen = true;
            }
        }
    }
}