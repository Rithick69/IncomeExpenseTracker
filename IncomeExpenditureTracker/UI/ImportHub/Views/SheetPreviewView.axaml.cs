using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace IncomeExpenditureTracker.UI.ImportHub
{
    public partial class SheetPreviewView : UserControl
    {
        public SheetPreviewView()
        {
            InitializeComponent();

            // Suppress mouse wheel scrolling on all ComboBoxes to prevent accidental value changes
            // this.AddHandler(PointerWheelChangedEvent, (sender, e) =>
            // {
            //     if (e.Source is Avalonia.Controls.ComboBox)
            //     {
            //         e.Handled = true;
            //     }
            // }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            PreventComboBoxWheelSelection(EntityFieldComboBox);
            PreventComboBoxWheelSelection(AccountNumFieldComboBox);
            PreventComboBoxWheelSelection(CardNumFieldComboBox);
            PreventComboBoxWheelSelection(AccountTypeFieldComboBox);
            PreventComboBoxWheelSelection(CurrencyFieldComboBox);

            PreventComboBoxWheelSelection(DateHeaderComboBox);
            PreventComboBoxWheelSelection(DescHeaderComboBox);
            PreventComboBoxWheelSelection(DebitHeaderComboBox);
            PreventComboBoxWheelSelection(CreditHeaderComboBox);
            PreventComboBoxWheelSelection(AmountHeaderComboBox);
            PreventComboBoxWheelSelection(TransactionTypeHeaderComboBox);
        }

        private static void PreventComboBoxWheelSelection(ComboBox comboBox)
        {
            comboBox.AddHandler(
                InputElement.PointerWheelChangedEvent,
                (_, e) => e.Handled = true,
                RoutingStrategies.Tunnel);
        }
    }
}