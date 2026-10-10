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
using System.Threading;

namespace IncomeExpenditureTracker.Services.Importing;

public class ExcelStatementImport : IStatementImport<IXLWorkbook>
{
    private readonly IDatabaseService _database;
    private readonly IEntityService _entityService;
    private readonly IAccountService _accountService;
    private readonly ITransactionExtractor<IXLWorksheet> _transactionExtractor;
    private readonly IDescriptionParser _descriptionParser;
    private readonly ITagEngine _tagEngine;
    private readonly IImportBatchService _batchService;
    private readonly ITransactionService _transactionService;
    private readonly IPayeeService _payeeService;
    private readonly ILogger<ExcelStatementImport> _logger;

    private const int BatchSize = 250;

    public ExcelStatementImport(
        IDatabaseService database,
        IEntityService entityService,
        IAccountService accountService,
        ITransactionExtractor<IXLWorksheet> transactionExtractor,
        IDescriptionParser descriptionParser,
        ITagEngine tagEngine,
        IImportBatchService batchService,
        ITransactionService transactionService,
        IPayeeService payeeService,
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
        _payeeService = payeeService ?? throw new ArgumentNullException(nameof(payeeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ImportConfirmedBatchAsync(IXLWorkbook workbook, IEnumerable<PreviewTracker> trackers, CancellationToken ct = default)
    {
        if (workbook == null) throw new ArgumentNullException(nameof(workbook));
        if (trackers == null) throw new ArgumentNullException(nameof(trackers));

        var trackersList = trackers.ToList();
        if (!trackersList.Any()) return;

        _logger.LogInformation("Starting confirmed batch import for {Count} sheets...", trackersList.Count);

        try
        {
            ct.ThrowIfCancellationRequested();

            // Structure to hold processed data before opening DB transaction
            var processedSheets = new List<ProcessedSheetData>();
            var payeeMappings = _payeeService.GetMappingsCache();

            // Process each sheet individually in memory
            foreach (var tracker in trackersList)
            {
                var previewMap = tracker.FinalPreview;
                var sheetName = tracker.SheetName;

                if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var worksheet))
                {
                    _logger.LogWarning("Worksheet {SheetName} not found in workbook, skipping.", sheetName);
                    continue;
                }

                var fields = previewMap.Fields;

                string entityName = GetMetaValue(fields, "Meta:ENTITY_NAME", "Unknown Entity");
                string accountNumber = GetMetaValue(fields, "Meta:ACCOUNT_NUMBER", "Unknown Account");
                string cardNumber = GetMetaValue(fields, "Meta:CARD_NUMBER", string.Empty);
                string accountType = GetMetaValue(fields, "Meta:ACCOUNT_TYPE", "Checking");
                string currency = GetMetaValue(fields, "Meta:CURRENCY", "INR");

                var transactions = _transactionExtractor.ExtractTransactions(
                    worksheet,
                    previewMap.HeaderRow,
                    previewMap.Fields,
                    ct
                );

                if (transactions.Count == 0)
                {
                    _logger.LogWarning("No valid transactions extracted from worksheet {SheetName}. Skipping.", sheetName);
                    continue;
                }

                var tokenRows = new List<List<string>>(transactions.Count);

                foreach (var txn in transactions)
                {
                    // LOOP CHECK: Abort the heavy synchronous parsing if cancelled!
                    ct.ThrowIfCancellationRequested();

                    var tokens = _descriptionParser.ExtractTokens(txn.Description);
                    tokenRows.Add(tokens);

                    // A. Sanitize and store in the Source property
                    txn.Source = _descriptionParser.SanitizeMerchantString(txn.Description);

                    // B. Check if it is a Credit
                    bool isCredit = txn.Credit > 0;

                    // C.Perform O(1) Exact - Match Lookup
                    if (isCredit)
                    {
                        if (!string.IsNullOrWhiteSpace(txn.Source) && payeeMappings.TryGetValue(txn.Source, out int payeeId))
                        {
                            txn.PayeeId = payeeId;
                        }
                        else
                        {
                            // Flag for review ONLY if it is a credit and missing a mapping
                            txn.ReviewStatus |= ReviewFlags.MissingPayee;
                        }
                    }
                }

                await _tagEngine.ProcessTransactions(transactions, tokenRows, ct);

                processedSheets.Add(new ProcessedSheetData
                {
                    SheetName = sheetName,
                    PreviewMap = previewMap,
                    Transactions = transactions,
                    EntityName = entityName,
                    AccountNumber = accountNumber,
                    CardNumber = cardNumber,
                    AccountType = accountType,
                    Currency = currency
                });
            }

            if (!processedSheets.Any())
            {
                _logger.LogWarning("No sheets contained valid transactions for batch import.");
                return;
            }

            // Execute DB operations under a single master transaction token
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {
                foreach (var sheetData in processedSheets)
                {
                    var entityId = await _entityService.GetOrCreateEntity(sheetData.EntityName, conn, tx, ct);

                    var accountId = await _accountService.GetOrCreateAccount(new Account
                    {
                        AccountNumber = sheetData.AccountNumber,
                        CardNumber = sheetData.CardNumber,
                        EntityId = entityId,
                        EntityName = sheetData.EntityName,
                        AccountType = sheetData.AccountType,
                        Currency = sheetData.Currency,
                        CreatedDate = DateTime.UtcNow
                    }, conn, tx, ct);

                    string fileName = !string.IsNullOrWhiteSpace(sheetData.PreviewMap.FileName)
                        ? sheetData.PreviewMap.FileName
                        : $"Statement_{DateTime.UtcNow:yyyyMMdd}";

                    var batchId = await _batchService.CreateBatch(fileName, sheetData.EntityName, accountId, conn, tx, ct);

                    foreach (var txn in sheetData.Transactions)
                    {
                        ct.ThrowIfCancellationRequested();
                        txn.ImportBatchId = batchId;
                        txn.TransactionHash = GenerateHash(txn);
                        txn.AccountId = accountId;
                    }

                    for (int i = 0; i < sheetData.Transactions.Count; i += BatchSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        int count = Math.Min(BatchSize, sheetData.Transactions.Count - i);
                        var batch = sheetData.Transactions.GetRange(i, count);
                        await _transactionService.InsertTransactionsAsync(batch, conn, tx, ct);
                    }
                    _logger.LogInformation("Successfully imported {Count} transactions under Batch ID {BatchId} for account ID {AccountId} (Sheet: {SheetName}).",
                        sheetData.Transactions.Count, batchId, accountId, sheetData.SheetName);
                }
            }, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Batch statement import was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error occurred while importing batch statement. Aborting workflow.");
            throw new InvalidOperationException("Failed to import the batch statements.", ex);
        }
    }

    private string GenerateHash(Transaction txn)
    {
        var raw = $"{txn.Date:yyyy-MM-dd}|{txn.Description}|{txn.Debit}|{txn.Credit}|{txn.AccountId}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static string GetMetaValue(Dictionary<string, DetectedField> fields, string key, string defaultValue)
    {
        return fields.TryGetValue(key, out var field) && !string.IsNullOrWhiteSpace(field.ExtractedValue)
            ? field.ExtractedValue.Trim()
            : defaultValue;
    }

    private class ProcessedSheetData
    {
        public string SheetName { get; set; } = string.Empty;
        public StatementPreview PreviewMap { get; set; } = null!;
        public List<Transaction> Transactions { get; set; } = new();
        public string EntityName { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string CardNumber { get; set; } = string.Empty;
        public string AccountType { get; set; } = string.Empty;
        public string Currency { get; set; } = string.Empty;
    }
}