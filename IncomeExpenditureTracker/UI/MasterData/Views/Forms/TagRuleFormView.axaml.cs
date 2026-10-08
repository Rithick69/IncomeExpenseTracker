using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class TagRuleFormView : UserControl
    {
        public TagRuleFormView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (DataContext is TagRuleFormViewModel viewModel && viewModel.InitializeCommand.CanExecute(null))
            {
                viewModel.InitializeCommand.Execute(null);
            }
        }

        private void OnDropDownFocus(object? sender, GotFocusEventArgs e)
        {
            if (sender is AutoCompleteBox autoCompleteBox)
            {
                autoCompleteBox.IsDropDownOpen = true;
            }
        }
    }
}