using Dapper;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Entities;

namespace IncomeExpenditureTracker.Services.Database;

/// <summary>
/// Responsible for establishing the baseline SQLite database schema,
/// relational constraints, and initial domain seeding upon application startup.
/// </summary>
public class DatabaseInitializer : IDatabaseInitializer
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
            // "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;" upon opening,
            // guaranteeing WAL concurrency mode and relational constraints are active.
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
                    Name TEXT UNIQUE,
                    CreatedDate DATETIME DEFAULT (datetime('now'))
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
                    Name TEXT,
                    CategoryId INTEGER,
                    CreatedDate DATETIME DEFAULT (datetime('now')),
                    FOREIGN KEY(CategoryId) REFERENCES Categories(Id)
                    UNIQUE(Name, CategoryId)
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
                    CreatedDate DATETIME DEFAULT (datetime('now')),
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
                    CreatedDate DATETIME DEFAULT (datetime('now')),
                    FOREIGN KEY(TagId) REFERENCES Tags(Id)
                );

                -- SQLite B-tree indexes to speed up self-learning queries and joins

                CREATE INDEX IF NOT EXISTS idx_tagrules_keyword ON TagRules(Keyword);
                CREATE INDEX IF NOT EXISTS idx_tagrules_tagid ON TagRules(TagId);

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
                    CreatedDate DATETIME DEFAULT (datetime('now'))
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
                    CreatedDate DATETIME DEFAULT (datetime('now')),
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
                -- Source → extracted counterparty (for readability)
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
                    Source TEXT,
                    Credit REAL,
                    Debit REAL,
                    TransactionType TEXT,
                    ImportBatchId INTEGER,
                    TagId INTEGER,
                    TransactionHash TEXT,
                    CreatedDate DATETIME DEFAULT (datetime('now')),
                    NeedsReview BOOLEAN,
                    RawAmountText TEXT,
                    ParseErrorMessage TEXT,
                    FOREIGN KEY(TagId) REFERENCES Tags(Id),
                    FOREIGN KEY(AccountId) REFERENCES Accounts(Id)
                );

                CREATE INDEX IF NOT EXISTS idx_transactions_accountid ON Transactions(AccountId);
                CREATE INDEX IF NOT EXISTS idx_transactions_source ON Transactions(Source);

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
                    ImportDate DATETIME DEFAULT (datetime('now')),
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
                    CreatedDate DATETIME DEFAULT (datetime('now')),
                    CONSTRAINT unique_synonym UNIQUE(FieldType, Synonym, Category)
                );";

                // Execute schema DDL asynchronously
                await connection.ExecuteAsync(schemaDdl);
            });

            await _database.ExecuteWithRetryAsync(async (c) =>
            {
                const string seedSql = @"
                    INSERT OR IGNORE INTO Tags (Id, Name, SubCategoryId)
                        VALUES (999, @MiscTag, NULL);
                ";

                await c.ExecuteAsync(seedSql, new
                {
                    MiscTag = SystemConstants.MiscTag
                });
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
                        -- Transaction header seeds
                        ('DATE','DATE',50, 'TRANSACTION'),
                        ('DATE','TXN DATE',80, 'TRANSACTION'),
                        ('DATE','TRANSACTION DATE',100, 'TRANSACTION'),
                        ('DATE','VALUE DATE',20, 'TRANSACTION'),

                        ('DESCRIPTION','DESCRIPTION',100, 'TRANSACTION'),
                        ('DESCRIPTION','NARRATION',90, 'TRANSACTION'),
                        ('DESCRIPTION','REMARKS',80, 'TRANSACTION'),
                        ('DESCRIPTION','DETAILS',70, 'TRANSACTION'),
                        ('DESCRIPTION', 'Memo', 80, 'TRANSACTION'),

                        ('DEBIT','DEBIT',100, 'TRANSACTION'),
                        ('DEBIT','WITHDRAWAL',90, 'TRANSACTION'),
                        ('DEBIT','DR',70, 'TRANSACTION'),

                        ('CREDIT','CREDIT',100, 'TRANSACTION'),
                        ('CREDIT','DEPOSIT',90, 'TRANSACTION'),
                        ('CREDIT','CR',70, 'TRANSACTION'),

                        ('AMOUNT', 'Amount', 100, 'TRANSACTION'),
                        ('AMOUNT', 'Amount ($)', 80, 'TRANSACTION'),
                        ('AMOUNT', 'Balance', 100, 'TRANSACTION'),

                        -- Metadata / Account Details seeds
                        ('ACCOUNT_NUMBER', 'Account Number', 100, 'METADATA'),
                        ('ACCOUNT_NUMBER', 'Account No', 90, 'METADATA'),
                        ('ACCOUNT_NUMBER', 'Acct No', 80, 'METADATA'),
                        ('ACCOUNT_NUMBER', 'A/C No', 70, 'METADATA'),

                        ('CARD_NUMBER', 'Card Number', 100, 'METADATA'),
                        ('CARD_NUMBER', 'Card No', 90, 'METADATA'),
                        ('CARD_NUMBER', 'Credit Card Number', 80, 'METADATA'),
                        ('CARD_NUMBER', 'Card Ending In', 70, 'METADATA'),

                        ('ACCOUNT_TYPE', 'Account Type', 100, 'METADATA'),
                        ('ACCOUNT_TYPE', 'Type of Account', 90, 'METADATA'),
                        ('ACCOUNT_TYPE', 'Product Type', 80, 'METADATA'),

                        ('CURRENCY', 'Currency', 100, 'METADATA'),
                        ('CURRENCY', 'Curr', 90, 'METADATA'),
                        ('CURRENCY', 'Statement Currency', 80, 'METADATA'),

                        ('CREDIT_LIMIT', 'Credit Limit', 100, 'METADATA'),
                        ('CREDIT_LIMIT', 'Total Credit Limit', 90, 'METADATA'),
                        ('CREDIT_LIMIT', 'Assigned Limit', 80, 'METADATA'),

                        ('ENTITY_NAME', 'Account Holder', 100, 'METADATA'),
                        ('ENTITY_NAME', 'Customer Name', 90, 'METADATA'),
                        ('ENTITY_NAME', 'Entity Name', 80, 'METADATA'),
                        ('ENTITY_NAME', 'Name', 70, 'METADATA');";

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