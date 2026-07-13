using System;
using System.IO;
using System.Linq;
using System.Data;
using System.Text;
using ClosedXML.Excel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Tagging;
using IncomeExpenditureTracker.Services.TransactionExtractor;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Services.Importing;


public class ExcelStatementImport : IStatementImport<IXLWorksheet>
{
    private readonly IDatabaseService _database;
    private readonly IEntityService _entityService;
    private readonly IAccountService _accountService;
    private readonly ITransactionExtractor<IXLWorksheet> _transactionExtractor;
    private readonly DescriptionParser _descriptionParser;
    private readonly TagEngine _tagEngine;
    private readonly IImportBatchService _batchService;
    private readonly ITransactionService _transactionService;
    private readonly ILogger<ExcelStatementImport> _logger;

    private const int BatchSize = 250;

    public ExcelStatementImport(
        IDatabaseService database,
        IEntityService entityService,
        IAccountService accountService,
        ITransactionExtractor<IXLWorksheet> transactionExtractor,
        DescriptionParser descriptionParser,
        TagEngine tagEngine,
        IImportBatchService batchService,
        ITransactionService transactionService,
        ILogger<ExcelStatementImport> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _entityService = entityService ?? throw new ArgumentNullException(nameof(entityService));
        _accountService = accountService ?? throw new ArgumentNullException(nameof(accountService));
        _transactionExtractor = transactionExtractor ?? throw new ArgumentNullException(nameof(transactionExtractor));
        _descriptionParser = descriptionParser ?? throw new ArgumentNullException(nameof(descriptionParser));
        _tagEngine = tagEngine ?? throw new ArgumentNullException(nameof(tagEngine));
        _batchService = batchService ?? throw new ArgumentNullException(nameof(batchService));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ImportConfirmedStatementAsync(IXLWorksheet worksheet, StatementPreview previewMap)
    {
        if (worksheet == null) throw new ArgumentNullException(nameof(worksheet));
        if (previewMap == null) throw new ArgumentNullException(nameof(previewMap));

        _logger.LogInformation("Starting confirmed statement import for file '{FileName}'...", previewMap.FileName);
        // -------------------------------------------------------------------------
        // 1. UPFRONT DICTIONARY RESOLUTION (O(1) Execution - Zero lookups in loops)
        // -------------------------------------------------------------------------
        // We resolve all metadata and column indices upfront to avoid repeated dictionary lookups during the transaction extraction loop.
        var fields = previewMap.Fields;

        try
        {
            // Resolve Metadata (with safe fallbacks for relaxed validation)
            string entityName = GetMetaValue(fields, "Meta:ENTITY_NAME", "Unknown Entity");
            string accountNumber = GetMetaValue(fields, "Meta:ACCOUNT_NUMBER", "Unknown Account");
            string cardNumber = GetMetaValue(fields, "Meta:CARD_NUMBER", string.Empty);
            string accountType = GetMetaValue(fields, "Meta:ACCOUNT_TYPE", "Checking");
            string currency = GetMetaValue(fields, "Meta:CURRENCY", "INR");

            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx) =>
            {

                // Ensure the account and entity exist in the database, creating them if necessary

                // -------------------------------------------------------------------------
                // 2. DATABASE METADATA PERSISTENCE
                // -------------------------------------------------------------------------
                var entityId = await _entityService.GetOrCreateEntity(entityName, conn, tx);

                var accountId = await _accountService.GetOrCreateAccount(new Account
                {
                    AccountNumber = accountNumber,
                    CardNumber = cardNumber,
                    EntityId = entityId,
                    EntityName = entityName,
                    AccountType = accountType,
                    Currency = currency,
                    CreatedDate = DateTime.UtcNow
                }, conn, tx);

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

                if (transactions.Count == 0)
                {
                    _logger.LogWarning("No valid transactions extracted from worksheet. Aborting import transaction.");
                    return;
                }

                // -------------------------------------------------------------------------
                // 4. IN-MEMORY TOKENIZATION & TAGGING
                // -------------------------------------------------------------------------

                var tokenRows = new List<List<string>>(transactions.Count);

                foreach (var txn in transactions)
                {
                    var tokens = _descriptionParser.ExtractTokens(txn.Description);
                    tokenRows.Add(tokens);
                }

                await _tagEngine.ProcessTransactions(transactions, tokenRows);

                // -------------------------------------------------------------------------
                // 5. BATCH CREATION & HASHING
                // -------------------------------------------------------------------------
                // Extract filename safely without file I/O locks
                string fileName = !string.IsNullOrWhiteSpace(previewMap.FileName)
                    ? previewMap.FileName
                    : $"Statement_{DateTime.UtcNow:yyyyMMdd}";

                var batchId = await _batchService.CreateBatch(fileName, entityName, accountId, conn, tx);

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

                    await _transactionService.InsertTransactionsAsync(batch, conn, tx);
                }
                _logger.LogInformation("Successfully imported {Count} transactions under Batch ID {BatchId} for account ID {AccountId}.", transactions.Count, batchId, accountId);

            });

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error occurred while importing statement file '{FileName}'. Aborting workflow.", previewMap?.FileName);
            throw;
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