// Dapper allows us to execute SQL easily
using Dapper;
using System;

namespace IncomeExpenditureTracker.Services.Database;

// This class is responsible for creating the database tables
// when the application starts for the first time.
public class DatabaseInitializer
{
    private readonly IDatabaseService _database;

    // The IDatabaseService provides the SQLite connection
    public DatabaseInitializer(IDatabaseService database)
    {
        _database = database;
    }

    // This method runs during application startup
    // and ensures all required tables exist.
    public void Initialize()
    {
        try
        {
            using var connection = _database.GetConnection();

            connection.Open();

            var sql = @"

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
            Name TEXT NOT NULL,
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
            EnitityId INTEGER,
            EntityName TEXT,
            AccountType TEXT,
            Currency TEXT,
            CreatedDate DATETIME,
            CreditLimit TEXT,
            FOREIGN KEY(EnitityId) REFERENCES Entities(Id)
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
            Account TEXT,
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

        ------------------------------------------------------------
        -- IMPORT BATCHES
        ------------------------------------------------------------
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
        -- SYNONYMS
        ------------------------------------------------------------
        -- Used for automatic field detection when importing Excel.
        -- Synonyms allow matching different bank column names.
        ------------------------------------------------------------

        CREATE TABLE IF NOT EXISTS Synonyms (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FieldType TEXT NOT NULL,
            Synonym TEXT NOT NULL,
            Priority INTEGER DEFAULT 10
        );

        ";

            // Execute the SQL statements above
            connection.Execute(sql);

            // Add default synonyms during initialization.
            connection.Execute(@"
                INSERT OR IGNORE INTO Synonyms (FieldType, Synonym, Priority) VALUES
                ('DATE','DATE',50),
                ('DATE','TXN DATE',80),
                ('DATE','TRANSACTION DATE',100),
                ('DATE','VALUE DATE',20),

                ('DESCRIPTION','DESCRIPTION',100),
                ('DESCRIPTION','NARRATION',90),
                ('DESCRIPTION','REMARKS',80),
                ('DESCRIPTION','DETAILS',70),

                ('DEBIT','DEBIT',100),
                ('DEBIT','WITHDRAWAL',90),
                ('DEBIT','DR',70),

                ('CREDIT','CREDIT',100),
                ('CREDIT','DEPOSIT',90),
                ('CREDIT','CR',70)
            ");
        }
        catch (Exception ex)
        {
            // ------------------------------------------------------------
            // CRITICAL DATABASE FAILURE
            // ------------------------------------------------------------
            // If schema initialization fails, the application
            // cannot operate safely.
            // We log the error and rethrow so startup fails clearly.
            // ------------------------------------------------------------
            Console.WriteLine($"[DatabaseInitializer] Database initialization failed: {ex.Message}");
            throw;
        }
    }
}