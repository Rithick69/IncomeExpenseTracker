using System;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Tagging;
using IncomeExpenditureTracker.Services.TransactionExtractor;

namespace IncomeExpenditureTracker.Services.Importing;


public class ExcelStatementImport : IStatementImport<IXLWorksheet>
{
    private readonly IEntityService _entityService;
    private readonly IAccountService _accountService;
    private readonly IHeaderDetector<IXLWorksheet> _headerDetector;
    private readonly ITransactionExtractor<IXLWorksheet> _transactionExtractor;
    private readonly DescriptionParser _descriptionParser;
    private readonly TagEngine _tagEngine;
    private readonly IImportBatchService _batchService;
    private readonly ITransactionService _transactionService;

    private const int BatchSize = 250;

    public ExcelStatementImport(
        IEntityService entityService,
        IAccountService accountService,
        IHeaderDetector<IXLWorksheet> headerDetector,
        ITransactionExtractor<IXLWorksheet> transactionExtractor,
        DescriptionParser descriptionParser,
        TagEngine tagEngine,
        IImportBatchService batchService,
        ITransactionService transactionService)
    {
        _entityService = entityService;
        _accountService = accountService;
        _headerDetector = headerDetector;
        _transactionExtractor = transactionExtractor;
        _descriptionParser = descriptionParser;
        _tagEngine = tagEngine;
        _batchService = batchService;
        _transactionService = transactionService;
    }

    public async Task ImportConfirmedStatementAsync(IXLWorksheet worksheet, StatementPreview previewMap)
    {
        // 1. Ensure the account and entity exist in the database, creating them if necessary

        var entityId = await _entityService.GetOrCreateEntity(previewMap.AccountInfo.EntityName);

        var accountId = await _accountService.GetOrCreateAccount(new Account
        {
            AccountNumber = previewMap.AccountInfo.AccountNumber,
            CardNumber = previewMap.AccountInfo.CardNumber,
            EntityId = entityId,
            EntityName = previewMap.AccountInfo.EntityName,
            AccountType = previewMap.AccountInfo.AccountType,
            Currency = previewMap.AccountInfo.Currency,
            CreatedDate = DateTime.UtcNow
        });

        // 2. Extract all transactions from the statement using the detected header and column mappings

        var transactions = _transactionExtractor.ExtractTransactions(
            worksheet,
            previewMap.HeaderRow,
            previewMap,
            accountId
        );

        // 3. Parse descriptions and apply tags to transactions before inserting into the database

        var tokenRows = new List<List<string>>(transactions.Count);

        foreach (var txn in transactions)
        {
            var tokens = _descriptionParser.ExtractTokens(txn.Description);
            tokenRows.Add(tokens);
        }

        _tagEngine.ProcessTransactions(transactions, tokenRows);

        // 4. Insert transactions in batches to optimize performance and avoid large transactions

        var batchId = await _batchService.CreateBatch(
            Path.GetFileName(previewMap.FilePath),
            previewMap.AccountInfo.EntityName
        );

        // 5. Assign the ImportBatchId and generate a hash for each transaction before insertion
        // This allows us to identify duplicates and group transactions by import batch for easier management

        foreach (var txn in transactions)
        {
            txn.ImportBatchId = batchId;
            txn.TransactionHash = GenerateHash(txn);
        }

        // 6. Insert transactions in batches

        for (int i = 0; i < transactions.Count; i += BatchSize)
        {
            var batch = transactions.Skip(i).Take(BatchSize).ToList();

            await _transactionService.InsertTransactions(batch);
        }
    }
    private string GenerateHash(Transaction txn)
    {
        var raw = $"{txn.Date:yyyy-MM-dd}|{txn.Description}|{txn.Debit}|{txn.Credit}|{txn.AccountId}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));

        return Convert.ToHexString(bytes);
    }
}