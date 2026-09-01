using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;


// Import ViewModels and Views
using IncomeExpenditureTracker.UI.Shell;
using IncomeExpenditureTracker.UI.Shared;

// Import the dependency injection setup
using IncomeExpenditureTracker.DependencyInjection;

// Import the database services we created
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker;

public partial class App : Application
{

    // This method loads Avalonia XAML resources when the application starts
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }


    // This method runs when the Avalonia framework has finished initializing
    public override void OnFrameworkInitializationCompleted()
    {
        try
        {
            // ---------------------------------------------------------
            // STEP 1: Configure Dependency Injection
            // ---------------------------------------------------------

            var services = new ServiceCollection();

            ServiceRegistration.Register(services);

            var localServiceProvider = services.BuildServiceProvider();

            // =========================================================================
            // STEP 2: INITIALIZE THE PROFILE REGISTRY (SYSTEM.DB)
            // Resolve the service and block synchronously just for startup creation
            // =========================================================================
            var registry = localServiceProvider.GetRequiredService<IProfileRegistry>();
            registry.InitializeRegistryAsync().GetAwaiter().GetResult();

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
                    DataContext = localServiceProvider.GetRequiredService<MainWindowViewModel>(),
                };
            }

            // Call the base method to complete framework startup
            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception ex)
        {
            // CRITICAL: If the DB fails to start, the app cannot function.
            // You must log this to Serilog (your prong 1 telemetry)
            // and ideally display a "Fatal Startup Error" window.
            Console.WriteLine($"FATAL STARTUP ERROR: {ex.Message}");
        }
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