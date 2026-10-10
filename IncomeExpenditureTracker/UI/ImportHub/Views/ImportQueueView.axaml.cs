using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace IncomeExpenditureTracker.UI.ImportHub;

public partial class ImportQueueView : UserControl
{
    public ImportQueueView()
    {
        InitializeComponent();

        // Wire up events after controls are initialized
        this.Loaded += OnViewLoaded;
    }

    /// <summary>
    /// Wire up event handlers after the view is fully loaded.
    /// This ensures named controls from XAML are available.
    /// </summary>
    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        // Find named controls from XAML
        var browseButton = this.FindControl<Button>("BrowseFilesButton");
        var dropZone = this.FindControl<Border>("DropZoneBorder");

        if (browseButton != null)
        {
            browseButton.Click += OnBrowseFilesClicked;
        }

        if (dropZone != null)
        {
            dropZone.AddHandler(DragDrop.DropEvent, OnDrop);
            dropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        }
    }

    /// <summary>
    /// Handles the Browse Files button click.
    /// Opens the OS file picker using Avalonia v11 StorageProvider API.
    /// </summary>
    private async void OnBrowseFilesClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ImportQueueViewModel viewModel)
            return;

        // Get the TopLevel (Window) to access the StorageProvider
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;

        // Determine file type filter based on selected radio button
        var fileTypeFilter = new FilePickerFileType(viewModel.IsExcelSelected ? "Excel Files" : "CSV Files")
        {
            Patterns = viewModel.IsExcelSelected
                ? new[] { "*.xlsx" }
                : new[] { "*.csv" }
        };

        // Configure file picker options
        var options = new FilePickerOpenOptions
        {
            Title = "Select Bank Statement Files",
            AllowMultiple = true,
            FileTypeFilter = new[] { fileTypeFilter }
        };

        // Open file picker dialog
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);

        // Extract file paths from storage files
        if (files != null && files.Count > 0)
        {
            var filePaths = files.Select(f => f.Path.LocalPath).ToList();

            // Pass file paths to the ViewModel's command
            await viewModel.HandleDroppedFilesCommand.ExecuteAsync(filePaths);
        }
    }

    /// <summary>
    /// Handles drag-over event to provide visual feedback.
    /// </summary>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Only accept files
        if (e.DataTransfer.TryGetFiles() != null)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    /// <summary>
    /// Handles drop event when files are dropped onto the DropZone.
    /// Extracts file paths and passes them to the ViewModel.
    /// </summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ImportQueueViewModel viewModel)
            return;

        // Extract files from drag data
        var storageItems = e.DataTransfer.TryGetFiles();
        if (storageItems == null)
            return;

        // Convert storage items to file paths
        var filePaths = new List<string>();
        foreach (var item in storageItems)
        {
            // Only add actual files (not directories)
            if (item is IStorageFile file)
            {
                filePaths.Add(file.Path.LocalPath);
            }
        }

        // Pass file paths to the ViewModel's command
        if (filePaths.Count > 0)
        {
            await viewModel.HandleDroppedFilesCommand.ExecuteAsync(filePaths);
        }
    }

    /// <summary>
    /// Handles sheet selection button click.
    /// Extracts file ID from Button.Tag (bound via XAML) and sheet name from Button.Content.
    /// No visual tree traversal needed - data binding provides all required information.
    /// </summary>
    private void OnSheetButtonClick(object? sender, RoutedEventArgs e)
    {
        // Extract data safely and directly from the Button properties (no visual tree traversal)
        if (sender is Button button &&
            button.Tag is Guid fileId &&
            button.Content is string sheetName &&
            DataContext is ImportQueueViewModel viewModel)
        {
            // Create tuple parameter and execute command
            viewModel.SelectSheetCommand.Execute((fileId, sheetName));
        }
    }
}
