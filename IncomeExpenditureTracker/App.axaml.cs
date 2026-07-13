using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;


// Import ViewModels and Views
using IncomeExpenditureTracker.ViewModels;
using IncomeExpenditureTracker.Views;

// Import the dependency injection setup
using IncomeExpenditureTracker.DependencyInjection;

// Import the database services we created
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker;

public partial class App : Application
{
    /// <summary>
    /// Global service provider accessible throughout the application.
    /// </summary>
    public static IServiceProvider Services { get; private set; } = null!;

    // This method loads Avalonia XAML resources when the application starts
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }


    // This method runs when the Avalonia framework has finished initializing
    public override async void OnFrameworkInitializationCompleted()
    {
        // ---------------------------------------------------------
        // STEP 1: Configure Dependency Injection
        // ---------------------------------------------------------

        var services = new ServiceCollection();

        ServiceRegistration.Register(services);

        Services = services.BuildServiceProvider();

        // ---------------------------------------------------------
        // STEP 2: Initialize Database
        // ---------------------------------------------------------

        var databaseInitializer =
            Services.GetRequiredService<DatabaseInitializer>();

        await databaseInitializer.InitializeAsync();

        // ---------------------------------------------------------
        // STEP 3: Setup the main application window
        // ---------------------------------------------------------

        // Check if we are running as a desktop application
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Disable duplicate validation behavior from Avalonia
            DisableAvaloniaDataAnnotationValidation();

            // Create the main window and assign its ViewModel
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        // Call the base method to complete framework startup
        base.OnFrameworkInitializationCompleted();
    }

    // This helper method removes duplicate validation plugins
    // that can conflict with MVVM validation frameworks
    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Find validation plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // Remove them from Avalonia
        foreach (var plugin in dataValidationPluginsToRemove)
        {
            BindingPlugins.DataValidators.Remove(plugin);
        }
    }
}