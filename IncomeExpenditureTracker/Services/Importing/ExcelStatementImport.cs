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
        // -------------------------------------------------------------------------
        // 1. UPFRONT DICTIONARY RESOLUTION (O(1) Execution - Zero lookups in loops)
        // -------------------------------------------------------------------------
        // We resolve all metadata and column indices upfront to avoid repeated dictionary lookups during the transaction extraction loop.
        var fields = previewMap.Fields;

        // Resolve Metadata (with safe fallbacks for relaxed validation)
        string entityName = GetMetaValue(fields, "Meta:ENTITY_NAME", "Unknown Entity");
        string accountNumber = GetMetaValue(fields, "Meta:ACCOUNT_NUMBER", "Unknown Account");
        string cardNumber = GetMetaValue(fields, "Meta:CARD_NUMBER", string.Empty);
        string accountType = GetMetaValue(fields, "Meta:ACCOUNT_TYPE", "Checking");
        string currency = GetMetaValue(fields, "Meta:CURRENCY", "INR");

        // Ensure the account and entity exist in the database, creating them if necessary

        // -------------------------------------------------------------------------
        // 2. DATABASE METADATA PERSISTENCE
        // -------------------------------------------------------------------------
        var entityId = await _entityService.GetOrCreateEntity(entityName);

        var accountId = await _accountService.GetOrCreateAccount(new Account
        {
            AccountNumber = accountNumber,
            CardNumber = cardNumber,
            EntityId = entityId,
            EntityName = entityName,
            AccountType = accountType,
            Currency = currency,
            CreatedDate = DateTime.UtcNow
        });

        // -------------------------------------------------------------------------
        // 3. PURE INTEGER-INDEXED TRANSACTION EXTRACTION
        // -------------------------------------------------------------------------
        // Passing resolved integer coordinates avoids 2,500+ dictionary string lookups
        var transactions = _transactionExtractor.ExtractTransactions(
            worksheet,
            previewMap.HeaderRow,
            previewMap.Fields,
            accountId
        );

        if (transactions.Count == 0) return;

        // -------------------------------------------------------------------------
        // 4. IN-MEMORY TOKENIZATION & TAGGING
        // -------------------------------------------------------------------------

        var tokenRows = new List<List<string>>(transactions.Count);

        foreach (var txn in transactions)
        {
            var tokens = _descriptionParser.ExtractTokens(txn.Description);
            tokenRows.Add(tokens);
        }

        _tagEngine.ProcessTransactions(transactions, tokenRows);

        // -------------------------------------------------------------------------
        // 5. BATCH CREATION & HASHING
        // -------------------------------------------------------------------------
        // Extract filename safely without file I/O locks
        string fileName = !string.IsNullOrWhiteSpace(previewMap.FileName)
            ? previewMap.FileName
            : $"Statement_{DateTime.UtcNow:yyyyMMdd}";

        var batchId = await _batchService.CreateBatch(fileName, entityName);

        // Assign the ImportBatchId and generate a hash for each transaction before insertion
        // This allows us to identify duplicates and group transactions by import batch for easier management

        foreach (var txn in transactions)
        {
            txn.ImportBatchId = batchId;
            txn.TransactionHash = GenerateHash(txn);
        }

        // -------------------------------------------------------------------------
        // 6. HIGH-PERFORMANCE CHUNKED INSERTION
        // -------------------------------------------------------------------------
        // Using .Chunk() (.NET 6+) or List.GetRange avoids .Skip().Take() GC overhead

        for (int i = 0; i < transactions.Count; i += BatchSize)
        {
            int count = Math.Min(BatchSize, transactions.Count - i);
            var batch = transactions.GetRange(i, count);

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

    // --- Lightweight Helper Method for Upfront Resolution ---

    private static string GetMetaValue(Dictionary<string, DetectedField> fields, string key, string defaultValue)
    {
        return fields.TryGetValue(key, out var field) && !string.IsNullOrWhiteSpace(field.ExtractedValue)
            ? field.ExtractedValue.Trim()
            : defaultValue;
    }
}