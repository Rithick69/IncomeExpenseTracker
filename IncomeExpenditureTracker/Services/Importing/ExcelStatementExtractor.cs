using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Logging;

using IncomeExpenditureTracker.Services.TransactionExtractor;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.PreviewInsights;

namespace IncomeExpenditureTracker.Services.Importing;

// This service analyzes an Excel statement to provide a preview of the detected account information, column mappings, and a sample of extracted transactions.
// It is used to give users feedback before they proceed with importing the full statement.
public class ExcelStatementExtractor : IStatementExtractor<IXLWorksheet>
{
    private readonly IHeaderDetector<IXLWorksheet> _headerDetector;
    private readonly IFieldMapper<IXLWorksheet> _fieldMapper;
    private readonly ITransactionExtractor<IXLWorksheet> _transactionExtractor;
    private readonly IConfidenceService _confidenceService;
    private readonly ILogger<ExcelStatementExtractor> _logger;

    public ExcelStatementExtractor(
        IHeaderDetector<IXLWorksheet> headerDetector,
        IFieldMapper<IXLWorksheet> fieldMapper,
        ITransactionExtractor<IXLWorksheet> transactionExtractor,
        IConfidenceService confidenceService,
        ILogger<ExcelStatementExtractor> logger)
    {
        _headerDetector = headerDetector ?? throw new ArgumentNullException(nameof(headerDetector));
        _fieldMapper = fieldMapper ?? throw new ArgumentNullException(nameof(fieldMapper));
        _transactionExtractor = transactionExtractor ?? throw new ArgumentNullException(nameof(transactionExtractor));
        _confidenceService = confidenceService ?? throw new ArgumentNullException(nameof(confidenceService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // Analyzes the given Excel file and returns a preview of the detected account information, column mappings, and a sample of transactions.
    public async Task<StatementPreview> Analyze(IXLWorksheet worksheet, string fileName, bool forceReload = false)
    {
        try
        {
            if (worksheet == null)
            {
                _logger.LogError("Statement analysis rejected: worksheet was null for file '{FileName}'.", fileName);
                throw new ArgumentNullException(nameof(worksheet));
            }

            _logger.LogInformation(
                "Starting statement analysis for file '{FileName}' (force reload: {ForceReload}).",
                fileName,
                forceReload);

            // 1. Detect account & entity metadata (Returns Dictionary<string, DetectedField>)
            var metadataFields = await _fieldMapper.DetectAccountDetails(worksheet, forceReload);

            // 2. Detect the header row coordinate
            var headerRow = await _headerDetector.DetectHeaderRow(worksheet, forceReload);
            _logger.LogInformation("Detected header row {HeaderRow} for file '{FileName}'.", headerRow, fileName);

            // 3. Detect column coordinates (Returns Dictionary<string, DetectedField>)
            var columnFields = await _fieldMapper.DetectColumns(worksheet, headerRow, forceReload);
            _logger.LogInformation(
                "Detected {ColumnCount} mapped columns for file '{FileName}'.",
                columnFields?.Count ?? 0,
                fileName);

            if (columnFields == null)
            {
                _logger.LogError("Statement analysis rejected: column mapping dictionary was null for file '{FileName}'.", fileName);
                throw new InvalidOperationException($"Column mapping dictionary was null for file '{fileName}'.");
            }

            // 4. Extract a sample of transactions for UI verification (first 20 rows)
            var previewTransactions = _transactionExtractor
                .ExtractPreview(worksheet, headerRow, columnFields)
                .Take(20)
                .ToList();
            _logger.LogInformation(
                "Preview extraction yielded {PreviewCount} sample transactions for file '{FileName}'.",
                previewTransactions.Count,
                fileName);

            // 5. MERGE both dictionaries into one unified map for our StatementPreview
            var unifiedFields = new Dictionary<string, DetectedField>(StringComparer.OrdinalIgnoreCase);

            foreach (var (key, field) in columnFields)
                unifiedFields[key] = field;

            foreach (var (key, field) in metadataFields)
                unifiedFields[key] = field;

            // 6. Calculate overall confidence score using our detected dictionaries
            var confidence = _confidenceService.CalculateConfidence(unifiedFields, previewTransactions);

            // 7. Generate signature using our dictionary (no manual cell reading required!)
            string entityName = metadataFields.TryGetValue(MetadataField.ENTITY_NAME.ToString(), out var ef)
                ? ef.ExtractedValue
                : "UNKNOWN_ENTITY";

            var signature = GenerateHeaderSignature(entityName, columnFields);

            // Return the analysis results in a StatementPreview object

            return new StatementPreview
            {
                FileName = fileName,
                HeaderRow = headerRow,
                Fields = unifiedFields,
                HeaderSignature = signature,
                PreviewTransactions = previewTransactions,
                ConfidenceScore = confidence,
                RequiresVerification = confidence < 80 || !previewTransactions.Any()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error while analyzing statement file '{FileName}'.", fileName);
            return new StatementPreview
            {
                FileName = fileName,
                HeaderRow = -1,
                Fields = new Dictionary<string, DetectedField>(StringComparer.OrdinalIgnoreCase),
                HeaderSignature = string.Empty,
                PreviewTransactions = new List<TransactionPreview>(),
                ConfidenceScore = 0,
                RequiresVerification = true
            };
        }
    }

    // Generates a simple signature string based on the detected header names and their positions.
    private string GenerateHeaderSignature(string entityName, Dictionary<string, DetectedField> columnFields)
    {
        // 1. Filter for valid detected columns (> 0)
        // 2. Sort by ColumnIndex so the signature string is 100% deterministic
        // 3. Format each as "INDEX:FIELDNAME" (e.g., "1:Date|2:Description|4:Amount")
        var columnParts = columnFields.Values
            .Where(f => f.ColumnIndex > 0)
            .OrderBy(f => f.ColumnIndex)
            .Select(f => $"{f.ColumnIndex}:{f.SuggestedField.ToUpperInvariant()}");

        // Join everything together dynamically
        var signature = $"ENTITY:{entityName.ToUpperInvariant()}|{string.Join("|", columnParts)}";

        // Compute SHA256 Hash
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(signature));

        return Convert.ToHexString(bytes);
    }
}