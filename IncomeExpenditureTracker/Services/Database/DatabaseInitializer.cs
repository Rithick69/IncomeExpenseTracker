using Dapper;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;

namespace IncomeExpenditureTracker.Services.Database;

/// <summary>
/// Responsible for establishing the baseline SQLite database schema,
/// relational constraints, and initial domain seeding upon application startup.
/// </summary>
public class DatabaseInitializer
{
    private readonly IDatabaseService _database;
    private readonly ISynonymService _synonymService;
    private readonly ILogger<DatabaseInitializer> _logger;

    // Injected ILogger for structured observability alongside database and synonym services
    public DatabaseInitializer(
        IDatabaseService database,
        ISynonymService synonymService,
        ILogger<DatabaseInitializer> logger)
    {
        _database = database;
        _synonymService = synonymService;
        _logger = logger;
    }

    /// <summary>
    /// Executes schema DDL and baseline seeding within the resilient retry wrapper.
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _logger.LogInformation("Starting database initialization and schema validation...");

            // -------------------------------------------------------------------------
            // ARCHITECTURAL GUARDRAIL: RESILIENT EXECUTION
            // -------------------------------------------------------------------------
            // We route all DDL (CREATE TABLE / CREATE INDEX) through ExecuteWithRetryAsync.
            // As noted in the architecture, this wrapper automatically fires:
            // "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;" upon opening [source: 2],
            // guaranteeing WAL concurrency mode and relational constraints are active [source: 2].
            // -------------------------------------------------------------------------
            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var schemaDdl = @"
                ------------------------------------------------------------
                -- CATEGORIES
                ------------------------------------------------------------
                -- Top level financial grouping.
                -- Examples:
                -- Income
                -- Expense
                -- Investment
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Categories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE
                );

                ------------------------------------------------------------
                -- SUBCATEGORIES
                ------------------------------------------------------------
                -- A category can have multiple subcategories.
                --
                -- Example:
                -- Investment
                -- → Equity
                -- → Mutual Fund
                -- → Insurance
                -- Expense
                -- → Food
                -- → Travel
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS SubCategories (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    CategoryId INTEGER,
                    FOREIGN KEY(CategoryId) REFERENCES Categories(Id)
                );

                ------------------------------------------------------------
                -- TAGS
                ------------------------------------------------------------
                -- A tag represents a specific entity or label.
                --
                -- Example:
                -- Swiggy
                -- Zerodha
                -- Groww
                -- Salary
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    SubCategoryId INTEGER,
                    FOREIGN KEY(SubCategoryId) REFERENCES SubCategories(Id)
                );

                ------------------------------------------------------------
                -- TAG RULES
                ------------------------------------------------------------
                -- Rules used by the tagging engine.
                --
                -- Each rule maps a keyword to a tag.
                --
                -- Example:
                -- Keyword: ZERODHA
                -- Tag: Zerodha
                --
                -- Priority allows more specific rules to override generic ones.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS TagRules (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Keyword TEXT NOT NULL,
                    TagId INTEGER,
                    Priority INTEGER DEFAULT 10,
                    FOREIGN KEY(TagId) REFERENCES Tags(Id)
                );

                -- SQLite B-tree indexes to speed up self-learning queries and joins

                CREATE INDEX IF NOT EXISTS idx_tagrules_keyword ON TagRules(Keyword);
                CREATE INDEX IF NOT EXISTS idx_tagrules_tagid ON TagRules(TagId);

                INSERT OR IGNORE INTO Tags (Id, Name, SubCategoryId) VALUES (1, 'Misc', NULL);

                ------------------------------------------------------------
                -- Unified View for Tag, SubCategory, Category
                ------------------------------------------------------------

                CREATE VIEW IF NOT EXISTS vw_TagTaxonomy AS
                SELECT
                    t.Id AS TagId,
                    t.Name AS TagName,
                    s.Name AS SubcategoryName,
                    c.Name AS CategoryName
                FROM Tags t
                JOIN Subcategories s ON t.SubcategoryId = s.Id
                JOIN Categories c ON s.CategoryId = c.Id;

                ------------------------------------------------------------
                -- ENTITIES
                ------------------------------------------------------------
                -- Stores entity metadata.
                -- Allows grouping accounts by entity for dashboard analytics.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Entities (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL UNIQUE,
                    Country TEXT,
                    CreatedDate TEXT
                );

                ------------------------------------------------------------
                -- ACCOUNTS
                ------------------------------------------------------------
                -- Stores information about bank accounts or credit cards.
                -- Used for dashboard grouping and analytics.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Accounts (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    AccountNumber TEXT UNIQUE,
                    CardNumber TEXT UNIQUE,
                    EntityId INTEGER,
                    EntityName TEXT,
                    AccountType TEXT,
                    Currency TEXT,
                    CreatedDate DATETIME,
                    CreditLimit TEXT,
                    FOREIGN KEY(EntityId) REFERENCES Entities(Id)
                );

                CREATE INDEX IF NOT EXISTS idx_accounts_entityid ON Accounts(EntityId);

                ------------------------------------------------------------
                -- TRANSACTIONS
                ------------------------------------------------------------
                -- This table stores all imported bank and credit card
                -- transactions.
                --
                -- Important fields:
                --
                -- Entity → extracted counterparty (for readability)
                -- Credit → money received
                -- Debit → money spent
                --
                -- TagId links the transaction to its classification.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Transactions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Date TEXT NOT NULL,
                    AccountId INTEGER,
                    Description TEXT,
                    Entity TEXT,
                    Credit REAL,
                    Debit REAL,
                    TransactionType TEXT,
                    ImportBatchId INTEGER,
                    TagId INTEGER,
                    TransactionHash TEXT,
                    CreatedDate TEXT,
                    FOREIGN KEY(TagId) REFERENCES Tags(Id),
                    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
                );

                CREATE INDEX IF NOT EXISTS idx_transactions_accountid ON Transactions(AccountId);
                CREATE INDEX IF NOT EXISTS idx_transactions_entity ON Transactions(Entity);

                ------------------------------------------------------------
                -- IMPORT BATCHES
                ------------------------------------------------------------
                -- Tracks each imported statement file.
                -- Allows grouping transactions by import.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS ImportBatches (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FileName TEXT,
                    Source TEXT,
                    ImportDate TEXT,
                    AccountId INTEGER,
                    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
                );

                ------------------------------------------------------------
                -- SYNONYMS (HYBRID DOMAIN ISOLATION SCHEMA)
                ------------------------------------------------------------
                -- Used for automatic field detection when importing Excel.
                -- Synonyms allow matching different bank column names.
                -- Enforces compound uniqueness across FieldType, Synonym, and Category
                -- to allow identical header strings to exist independently across domains.
                ------------------------------------------------------------

                CREATE TABLE IF NOT EXISTS Synonyms (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FieldType TEXT NOT NULL,
                    Synonym TEXT NOT NULL,
                    Priority INTEGER DEFAULT 10,
                    Category TEXT NOT NULL,
                    UNIQUE(FieldType, Synonym, Category)
                );";

                // Execute schema DDL asynchronously
                await connection.ExecuteAsync(schemaDdl);
            });

            // -------------------------------------------------------------------------
            // DOMAIN ISOLATION PRE-SEEDING
            // -------------------------------------------------------------------------
            // Seed the default domain enum field types for both TRANSACTION and METADATA.
            // SynonymService handles its own retry-protected writes internally.
            // -------------------------------------------------------------------------
            var fieldTypeGroups = new[]
            {
                (Category: "TRANSACTION", Fields: Enum.GetNames(typeof(TransactionColumnField)).Select(f => f.ToUpperInvariant())),
                (Category: "METADATA", Fields: Enum.GetNames(typeof(MetadataField)).Select(f => f.ToUpperInvariant()))
            };

            foreach (var group in fieldTypeGroups)
            {
                await _synonymService.SeedDefaultFieldTypesAsync(group.Fields, group.Category);
            }

            // -------------------------------------------------------------------------
            // BASELINE SYNONYM SEEDING
            // -------------------------------------------------------------------------
            // Wrap initial default synonym seeding in the retry wrapper as well.
            // Using INSERT OR IGNORE respects the compound unique constraint without failing.
            // -------------------------------------------------------------------------
            await _database.ExecuteWithRetryAsync(async (connection) =>
            {
                var seedSql = @"
                    INSERT OR IGNORE INTO Synonyms (FieldType, Synonym, Priority, Category) VALUES
                    ('DATE','DATE',50, 'TRANSACTION'),
                    ('DATE','TXN DATE',80, 'TRANSACTION'),
                    ('DATE','TRANSACTION DATE',100, 'TRANSACTION'),
                    ('DATE','VALUE DATE',20, 'TRANSACTION'),

                    ('DESCRIPTION','DESCRIPTION',100, 'TRANSACTION'),
                    ('DESCRIPTION','NARRATION',90, 'TRANSACTION'),
                    ('DESCRIPTION','REMARKS',80, 'TRANSACTION'),
                    ('DESCRIPTION','DETAILS',70, 'TRANSACTION'),

                    ('DEBIT','DEBIT',100, 'TRANSACTION'),
                    ('DEBIT','WITHDRAWAL',90, 'TRANSACTION'),
                    ('DEBIT','DR',70, 'TRANSACTION'),

                    ('CREDIT','CREDIT',100, 'TRANSACTION'),
                    ('CREDIT','DEPOSIT',90, 'TRANSACTION'),
                    ('CREDIT','CR',70, 'TRANSACTION');";

                await connection.ExecuteAsync(seedSql);
            });

            _logger.LogInformation("Database schema initialization and baseline seeding completed successfully.");
        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // CRITICAL DATABASE FAILURE
            // ------------------------------------------------------------
            // If schema initialization fails, the application cannot operate safely.
            // Log as critical and rethrow to abort startup cleanly.
            // ------------------------------------------------------------
            _logger.LogCritical(ex, "Critical database failure during schema initialization. The application cannot start safely.");
            throw;
        }
    }
}