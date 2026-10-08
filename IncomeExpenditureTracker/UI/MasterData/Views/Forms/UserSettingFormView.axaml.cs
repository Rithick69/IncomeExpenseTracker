using Avalonia.Controls;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class UserSettingFormView : UserControl
    {
        public UserSettingFormView()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            if (DataContext is UserSettingFormViewModel viewModel && viewModel.InitializeCommand.CanExecute(null))
            {
                viewModel.InitializeCommand.Execute(null);
            }
        }
    }
}