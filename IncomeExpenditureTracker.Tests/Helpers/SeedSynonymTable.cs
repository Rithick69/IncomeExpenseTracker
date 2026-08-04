
using Dapper;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Tests.Helpers;

public static class SeedSynonymTable
{
    public static async Task SetupInMemorySynonymsTable(this DatabaseService databaseService)
    {
        // 1. Create just the Synonyms table required for these logic tests
        var createTableSql = @"
        CREATE TABLE IF NOT EXISTS Synonyms (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            FieldType TEXT NOT NULL,
            Synonym TEXT NOT NULL,
            Priority INTEGER DEFAULT 10,
            Category TEXT NOT NULL,
            UNIQUE(FieldType, Synonym, Category)
        );";

        // 2. Seed minimal test data required by HeaderDetector (Category = 'TRANSACTION')
        // and FieldMapper (Categories = 'DATE', 'DESCRIPTION', 'AMOUNT', 'ACCOUNT')
        var seedDataSql = @"
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

        await databaseService.ExecuteWithRetryAsync(async (connection) =>
        {
            await connection.ExecuteAsync(createTableSql);
            await connection.ExecuteAsync(seedDataSql);
        });
    }
}