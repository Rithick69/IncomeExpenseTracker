using Microsoft.Extensions.DependencyInjection;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Importing;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using IncomeExpenditureTracker.Services.PreviewInsights;
using IncomeExpenditureTracker.Services.StatementManagement;
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

        // ---------------------------------------------------------
        // Entities
        // ---------------------------------------------------------
        services.AddTransient<IEntityService, EntityService>();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IImportBatchService, ImportBatchService>();
        services.AddTransient<ITransactionService, TransactionService>();

        // ---------------------------------------------------------
        // Statement Generic Processing Layer
        // ---------------------------------------------------------
        // Registering the generic interface mapped to your Excel engines
        services.AddTransient<IStatementExtractor<IXLWorksheet>, ExcelStatementExtractor>();
        services.AddTransient<IStatementImport<IXLWorksheet>, ExcelStatementImport>();

        // ---------------------------------------------------------
        // Lifecycle / Session Orchestrators
        // ---------------------------------------------------------
        services.AddSingleton<IStatementLoader, StatementLoader>();
        services.AddSingleton<StatementManager>();

        // ---------------------------------------------------------
        // Tagging
        // ---------------------------------------------------------
        services.AddSingleton<ITagService, TagService>();
        services.AddTransient<TagEngine>();
        services.AddTransient<DescriptionParser>();

        // Un-comment or add this once we write the edit session logic:
        // services.AddTransient<StatementEditSessionService>();
    }
}