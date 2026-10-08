using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IncomeExpenditureTracker.UI.Shared
{
    /// <summary>
    /// SelectableFilter implements ObservableObject so the UI knows when a checkbox is clicked, and it fires a callback to refresh the grid.
    /// </summary>
    public partial class SelectableFilter : ObservableObject
    {
        public string Name { get; }
        private readonly Action _onSelectionChanged;

        [ObservableProperty]
        private bool _isSelected;

        public SelectableFilter(string name, Action onSelectionChanged)
        {
            Name = name;
            _onSelectionChanged = onSelectionChanged;
        }

        partial void OnIsSelectedChanged(bool value)
        {
            _onSelectionChanged?.Invoke();
        }
    }
}