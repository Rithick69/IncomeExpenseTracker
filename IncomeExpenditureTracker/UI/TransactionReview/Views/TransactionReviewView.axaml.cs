using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using IncomeExpenditureTracker.UI.TransactionReview;

namespace IncomeExpenditureTracker.UI.TransactionReview;

/// <summary>
/// Code-behind for TransactionReviewView.
/// Handles UI events and delegates business logic to the ViewModel.
/// </summary>
public partial class TransactionReviewView : UserControl
{
    public TransactionReviewView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Handles double-click on a DataGrid row to open the slide-in panel.
    /// Invokes the SelectTransactionCommand on the ViewModel.
    /// </summary>
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Extract the DataGrid from the sender
        if (sender is not DataGrid dataGrid) return;

        // Get the selected item (TransactionRowViewModel)
        var selectedTransaction = dataGrid.SelectedItem as TransactionRowViewModel;

        // Get the ViewModel from the DataContext
        if (DataContext is not TransactionReviewViewModel viewModel) return;

        // Execute the command to open the panel
        if (viewModel.SelectTransactionCommand.CanExecute(selectedTransaction))
        {
            viewModel.SelectTransactionCommand.Execute(selectedTransaction);
        }
    }
}
