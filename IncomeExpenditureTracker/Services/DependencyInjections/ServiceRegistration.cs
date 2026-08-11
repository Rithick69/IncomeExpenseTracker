using Microsoft.Extensions.DependencyInjection;
using ClosedXML.Excel;
using System;
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
namespace IncomeExpenditureTracker.DependencyInjection;

public static class ServiceRegistration
{
    public static void Register(IServiceCollection services)
    {
        // 1. REGISTER LOGGING
        services.AddLogging(builder =>
        {
            builder.ClearProviders(); // Clears default noisy providers

            // Outputs logs to the Visual Studio / Rider "Debug Output" window
            builder.AddDebug();

            // Outputs logs to the terminal / console window
            builder.AddConsole();

            // Set the minimum log level (Information is great for general dev, Debug for deep troubleshooting)
            builder.SetMinimumLevel(LogLevel.Debug);
        });

        // ---------------------------------------------------------
        // Database
        // ---------------------------------------------------------

        services.AddSingleton<IDatabaseService, DatabaseService>(); // Singleton because it manages the database connection and should be shared across the application.

        services.AddTransient<DatabaseInitializer>();

        // ---------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------

        services.AddSingleton<ISynonymService, SynonymService>(); // Singleton because it maintains a cache of synonyms, readonly service, non blocking, thread-safe.
        services.AddTransient<IFieldMapper<IXLWorksheet>, FieldMapper>();
        services.AddTransient<IHeaderDetector<IXLWorksheet>, HeaderDetector>();
        services.AddTransient<ITransactionExtractor<IXLWorksheet>, ExcelTransactionExtractor>();
        services.AddTransient<ConfidenceService>();
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
        services.AddTransient<TagEngine>();
        services.AddTransient<DescriptionParser>();
    }
}