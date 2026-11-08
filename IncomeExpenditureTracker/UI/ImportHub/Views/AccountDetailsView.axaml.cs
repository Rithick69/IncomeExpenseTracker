using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.ImportHub;

public partial class AccountDetailsView : UserControl
{
    public AccountDetailsView()
    {
        InitializeComponent();
        PreventComboBoxWheelSelection(AccountTypeLabelComboBox);
        PreventComboBoxWheelSelection(CurrencyLabelComboBox);
    }

    private static void PreventComboBoxWheelSelection(ComboBox comboBox)
    {
        comboBox.AddHandler(
            InputElement.PointerWheelChangedEvent,
            (_, e) => e.Handled = true,
            RoutingStrategies.Tunnel);
    }
}