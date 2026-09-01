using Microsoft.Extensions.DependencyInjection;
using ClosedXML.Excel;
using System;
using System.IO;
using Serilog;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using IncomeExpenditureTracker.Services.PreviewInsights;
using IncomeExpenditureTracker.Services.StatementManagement;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.Services.Tagging;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shell;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.UI.Gatekeeper;
using IncomeExpenditureTracker.UI.ImportHub;
using IncomeExpenditureTracker.UI.Ledger;
using IncomeExpenditureTracker.UI.MasterData;
using Microsoft.Extensions.Configuration;
namespace IncomeExpenditureTracker.DependencyInjection;

/// <summary>
/// Serilog is a structured logging library.
/// Traditional loggers just write plain text strings to a file (e.g., "Error processing file at 8:00 PM").
/// Serilog writes data as key-value pairs (like JSON).
/// This means if you ever use a log viewer, you can filter logs exactly by BatchId or Severity rather than just reading endless text walls.
/// It is fast, memory-safe, and the absolute standard in modern .NET development.
/// </summary>

public static class ServiceRegistration
{
    public static void Register(IServiceCollection services)
    {
        // 1. Build the Configuration (Equivalent to dotenv.config())
        // This allows you to use appsettings.json or environment variables if needed
        var configuration = new ConfigurationBuilder()
            // .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true) // Uncomment if using appsettings
            .Build();

        // 2. Register it as a Singleton so ProfileRegistry can resolve it
        services.AddSingleton<IConfiguration>(configuration);

        // =========================================================
        // 2. LOGGING CONFIGURATION (The Record Keeper)
        // =========================================================

        // 1. Get the cross-platform application data folder
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Console.WriteLine($"LocalApplicationData: {appDataPath}");

        string appDirectory = Path.Combine(appDataPath, "IncomeExpenditureTracker");
        string logDirectory = Path.Combine(appDirectory, "Logs");

        Console.WriteLine($"Log directory: {logDirectory}");

        // 2. Ensure the directory exists (Directory.CreateDirectory does nothing if it already exists)
        Directory.CreateDirectory(logDirectory);

        string logFilePath = Path.Combine(logDirectory, "tracker-.txt");

        Console.WriteLine($"Log file path: {logFilePath}");


        // Build the Serilog configuration
        var serilogLogger = new LoggerConfiguration()
            .MinimumLevel.Debug() // As per architecture: sets minimum severity to Debug
            .WriteTo.Console()    // As per architecture: routes to Console
            .WriteTo.Debug()      // As per architecture: routes to VS Debug
                                  // The new Rolling File Sink for production tracking:
            .WriteTo.File(
                path: logFilePath, // Serilog will auto-append the date (e.g., tracker-20260816.txt)
                rollingInterval: RollingInterval.Day, // Creates a new file every night at midnight
                fileSizeLimitBytes: 10 * 1024 * 1024, // 10 MB strict limit per file to prevent disk bloat
                rollOnFileSizeLimit: true, // If it hits 10MB in one day, it creates a new file (e.g., tracker-20260816_001.txt)
                retainedFileCountLimit: 7) // Auto-deletes files older than 7 days (Zero manual cleanup required)
            .CreateLogger();

        services.AddLogging(loggingBuilder =>
        {
            // As per architecture: Clears default noisy Microsoft providers
            loggingBuilder.ClearProviders();

            // Plugs our Serilog configuration into the Microsoft.Extensions.Logging pipeline.
            // 'dispose: true' ensures the file handles are gracefully released when the app shuts down.
            loggingBuilder.AddSerilog(serilogLogger, dispose: true);
        });

        // =========================================================
        // UI MESSAGING & MVVM (Phase 5)
        // =========================================================

        // The Broker is a Singleton: We only ever want ONE postman delivering mail for the entire app.
        services.AddSingleton<IApplicationBroker, ToolkitMessengerAdapter>();

        // ViewModels are usually Transient: If the user opens a window, we get a fresh Waiter.
        // If they close it, it gets destroyed cleanly without holding onto old data.
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<StatementEditViewModel>();
        services.AddTransient<MasterDataViewModel>();
        services.AddTransient<TransactionReviewViewModel>();
        services.AddTransient<LoginViewModel>();
        services.AddTransient<RegisterViewModel>();

        // ---------------------------------------------------------
        // Profiles
        // ---------------------------------------------------------

        services.AddTransient<IPasswordHasher, PasswordHasher>();
        services.AddTransient<IProfileCryptography, ProfileCryptography>();
        services.AddTransient<IProfileLoginService, ProfileLoginService>();
        services.AddTransient<IProfileRegistry, ProfileRegistry>();

        // ---------------------------------------------------------
        // Database
        // ---------------------------------------------------------

        services.AddSingleton<IDatabaseService, DatabaseService>(); // Singleton because it manages the database connection and should be shared across the application.

        services.AddTransient<IDatabaseInitializer, DatabaseInitializer>();

        // ---------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------

        services.AddTransient<IFieldMapper<IXLWorksheet>, FieldMapper>();
        services.AddTransient<IHeaderDetector<IXLWorksheet>, HeaderDetector>();
        services.AddTransient<ITransactionExtractor<IXLWorksheet>, ExcelTransactionExtractor>();
        services.AddTransient<IConfidenceService, ConfidenceService>();
        services.AddTransient<IStrictAccountParser, StrictAccountParser>();

        // ---------------------------------------------------------
        // Entities
        // ---------------------------------------------------------
        services.AddSingleton<ICategoryService, CategoryService>();
        services.AddSingleton<ISubCategoryService, SubCategoryService>();
        services.AddSingleton<IEntityService, EntityService>();
        services.AddSingleton<IAccountService, AccountService>();
        services.AddSingleton<IImportBatchService, ImportBatchService>();
        services.AddSingleton<ITransactionService, TransactionService>();
        services.AddSingleton<ISynonymService, SynonymService>(); // Singleton because it maintains a cache of synonyms, readonly service, non blocking, thread-safe.

        // ---------------------------------------------------------
        // Statement Generic Processing Layer
        // ---------------------------------------------------------
        // Registering the generic interface mapped to your Excel engines
        services.AddSingleton<IStatementLoader, StatementLoader>();
        services.AddTransient<IStatementExtractor<IXLWorksheet>, ExcelStatementExtractor>();
        services.AddTransient<IStatementImport<IXLWorksheet>, ExcelStatementImport>();
        services.AddTransient<IStatementEditSession, StatementEditSession>();

        // =========================================================================
        // FACTORIES (Bridges between Singletons and Transients)
        // =========================================================================
        // Tells the DI container: "Whenever a constructor asks for Func<IStatementEditSession>,
        // give it a function that resolves a fresh IStatementEditSession."
        services.AddSingleton<Func<IStatementEditSession>>(provider =>
            () => provider.GetRequiredService<IStatementEditSession>());

        services.AddSingleton<Func<IStatementExtractor<IXLWorksheet>>>(provider =>
            () => provider.GetRequiredService<IStatementExtractor<IXLWorksheet>>());

        services.AddSingleton<Func<IStatementImport<IXLWorksheet>>>(provider =>
            () => provider.GetRequiredService<IStatementImport<IXLWorksheet>>());

        // ---------------------------------------------------------
        // Lifecycle / Session Orchestrators
        // ---------------------------------------------------------
        // =========================================================================
        // ORCHESTRATORS (Application-wide UI Facades)
        // =========================================================================
        services.AddSingleton<IMasterDataOrchestrator, MasterDataOrchestrator>();
        services.AddSingleton<ITransactionReviewOrchestrator, TransactionReviewOrchestrator>();
        services.AddSingleton<StatementManager>();

        // ---------------------------------------------------------
        // Tagging
        // ---------------------------------------------------------
        services.AddSingleton<ITagService, TagService>();
        services.AddTransient<ITagEngine, TagEngine>();
        services.AddTransient<IDescriptionParser, DescriptionParser>();
    }
}