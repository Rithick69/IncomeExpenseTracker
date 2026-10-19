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

    public async Task ImportConfirmedBatchAsync(IXLWorkbook workbook, PreviewTracker tracker, CancellationToken ct = default)
    {
        if (workbook == null) throw new ArgumentNullException(nameof(workbook));
        if (tracker == null) throw new ArgumentNullException(nameof(tracker));

        var sheetName = tracker.SheetName;
        _logger.LogInformation("Starting confirmed import for sheet '{SheetName}'...", sheetName);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (!workbook.Worksheets.TryGetWorksheet(sheetName, out var worksheet))
            {
                _logger.LogError("Worksheet '{SheetName}' not found in workbook. Aborting import.", sheetName);
                throw new InvalidOperationException($"Worksheet '{sheetName}' does not exist in the uploaded file.");
            }

            // Structure to hold processed data before opening DB transaction
            var payeeMappings = _payeeService.GetMappingsCache();

            // Process each sheet individually in memory

            var previewMap = tracker.FinalPreview;

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
                _logger.LogWarning("No valid transactions extracted from worksheet '{SheetName}'. Aborting import.", sheetName);
                return;
            }

            var tokenRows = new List<List<string>>(transactions.Count);

            foreach (var txn in transactions)
            {
                // LOOP CHECK: Abort the heavy synchronous parsing if cancelled!
                ct.ThrowIfCancellationRequested();

                var tokens = _descriptionParser.ExtractTokens(txn.Description);
                tokenRows.Add(tokens);

                // Sanitize and store in the Source property
                txn.Source = _descriptionParser.SanitizeMerchantString(txn.Description);
            }

            await _tagEngine.ProcessTransactions(transactions, tokenRows, ct);

            // Execute DB operations under a single master transaction token
            await _database.ExecuteInTransactionWithRetryAsync(async (conn, tx, ct) =>
            {

                var entityId = await _entityService.GetOrCreateEntity(entityName, conn, tx, ct);

                var accountId = await _accountService.GetOrCreateAccount(new Account
                {
                    AccountNumber = accountNumber,
                    CardNumber = cardNumber,
                    EntityId = entityId,
                    EntityName = entityName,
                    AccountType = accountType,
                    Currency = currency,
                    CreatedDate = DateTime.UtcNow
                }, conn, tx, ct);

                string fileName = !string.IsNullOrWhiteSpace(previewMap.FileName)
                    ? previewMap.FileName
                    : $"Statement_{DateTime.UtcNow:yyyyMMdd}";

                var batchId = await _batchService.CreateBatch(fileName, entityName, accountId, conn, tx, ct);

                foreach (var txn in transactions)
                {
                    ct.ThrowIfCancellationRequested();
                    txn.ImportBatchId = batchId;
                    txn.TransactionHash = GenerateHash(txn);
                    txn.AccountId = accountId;

                    // Check if it is a Credit
                    bool isCredit = txn.Credit > 0;

                    // Perform Assignment & Store New Mappings (Restricted to Credits)
                    if (isCredit && !string.IsNullOrWhiteSpace(txn.Source))
                    {
                        // 1. STORING: If the extractor already assigned a PayeeId (e.g., from an explicit Excel column)
                        // and it isn't in our cache yet, permanently save the mapping so the engine learns it.
                        if (txn.PayeeId.HasValue && txn.PayeeId.Value > 0)
                        {
                            if (!payeeMappings.ContainsKey(txn.Source))
                            {
                                await _payeeService.AddMappingAsync(txn.Source, txn.PayeeId.Value, ct: ct);
                                payeeMappings[txn.Source] = txn.PayeeId.Value; // Update local cache for subsequent rows
                            }
                        }
                        // 2. ASSIGNMENT: Look up the mapping for the credit transaction
                        else if (payeeMappings.TryGetValue(txn.Source, out int mappedPayeeId))
                        {
                            txn.PayeeId = mappedPayeeId;
                        }
                        // 3. Auto-Create New Payee
                        else
                        {
                            var newPayee = new Payee
                            {
                                Name = txn.Source,
                                IsDefaultIncomeSource = false, // Engine default
                                CreatedDate = DateTime.UtcNow
                            };

                            var createdPayee = await _payeeService.CreateAsync(newPayee, conn, tx, ct);
                            txn.PayeeId = createdPayee.Id;

                            // Save the mapping so the engine remembers it for the next row
                            await _payeeService.AddMappingAsync(txn.Source, createdPayee.Id, conn, tx, ct);
                            payeeMappings[txn.Source] = createdPayee.Id;

                            // Note: We no longer flag MissingPayee because we just created it.
                        }
                    }
                }

                for (int i = 0; i < transactions.Count; i += BatchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    int count = Math.Min(BatchSize, transactions.Count - i);
                    var batch = transactions.GetRange(i, count);
                    await _transactionService.InsertTransactionsAsync(batch, conn, tx, ct);
                }
                _logger.LogInformation("Successfully imported {Count} transactions under Batch ID {BatchId} for account ID {AccountId} (Sheet: {SheetName}).",
                    transactions.Count, batchId, accountId, sheetName);

            }, ct);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Statement import for sheet '{SheetName}' was cancelled.", sheetName);
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