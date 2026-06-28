using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Text;
using ClosedXML.Excel;
using IncomeExpenditureTracker.Models;

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
    private readonly ConfidenceService _confidenceService;

    public ExcelStatementExtractor(
        IHeaderDetector<IXLWorksheet> headerDetector,
        IFieldMapper<IXLWorksheet> fieldMapper,
        ITransactionExtractor<IXLWorksheet> transactionExtractor,
        ConfidenceService confidenceService)
    {
        _headerDetector = headerDetector;
        _fieldMapper = fieldMapper;
        _transactionExtractor = transactionExtractor;
        _confidenceService = confidenceService;
    }

    // Analyzes the given Excel file and returns a preview of the detected account information, column mappings, and a sample of transactions.
    public async Task<StatementPreview> Analyze(IXLWorksheet worksheet, string filePath, bool forceReload = false)
    {
        try
        {
            var accountInfo = await _fieldMapper.DetectAccountDetails(worksheet, forceReload);

            var headerRow = await _headerDetector.DetectHeaderRow(worksheet, forceReload);

            var columnMap = await _fieldMapper.DetectColumns(worksheet, headerRow, forceReload);

            // Extract a sample of transactions for preview (e.g., first 20 rows)

            var previewTransactions = _transactionExtractor
                .ExtractPreview(worksheet, headerRow, columnMap)
                .Take(20)
                .ToList();

            var confidence = _confidenceService.CalculateConfidence(accountInfo, columnMap, previewTransactions);

            var dateHeader = worksheet.Cell(headerRow, columnMap.DateColumn).GetString();
            var descHeader = worksheet.Cell(headerRow, columnMap.DescriptionColumn).GetString();

            var debitHeader = columnMap.DebitColumn > 0
                ? worksheet.Cell(headerRow, columnMap.DebitColumn).GetString()
                : "";

            var creditHeader = columnMap.CreditColumn > 0
                ? worksheet.Cell(headerRow, columnMap.CreditColumn).GetString()
                : "";

            var amountHeader = columnMap.AmountColumn > 0
                ? worksheet.Cell(headerRow, columnMap.AmountColumn).GetString()
                : "";

            // Generate a signature for this header configuration to help identify similar statements in the future

            var signature = GenerateHeaderSignature(
                                accountInfo.EntityName,
                                columnMap.DateColumn,
                                columnMap.DescriptionColumn,
                                columnMap.DebitColumn,
                                columnMap.CreditColumn,
                                columnMap.AmountColumn
                            );

            // Return the analysis results in a StatementPreview object

            return new StatementPreview
            {
                FilePath = filePath,
                AccountInfo = accountInfo,
                HeaderRow = headerRow,

                DateField = new DetectedField
                {
                    ColumnIndex = columnMap.DateColumn,
                    ColumnName = dateHeader
                },

                DescriptionField = new DetectedField
                {
                    ColumnIndex = columnMap.DescriptionColumn,
                    ColumnName = descHeader
                },

                DebitField = new DetectedField
                {
                    ColumnIndex = columnMap.DebitColumn,
                    ColumnName = debitHeader
                },

                CreditField = new DetectedField
                {
                    ColumnIndex = columnMap.CreditColumn,
                    ColumnName = creditHeader
                },
                AmountField = new DetectedField
                {
                    ColumnIndex = columnMap.AmountColumn,
                    ColumnName = amountHeader
                },

                HeaderSignature = signature,

                PreviewTransactions = previewTransactions,

                ConfidenceScore = confidence,
            };
        }
        catch (Exception ex)
        {
            // Log the error and return a result indicating failure
            Console.WriteLine($"Error analyzing statement: {ex.Message}");
            return new StatementPreview
            {
                FilePath = filePath,
                AccountInfo = new Account
                {
                    AccountNumber = "Unknown",
                    EntityName = "Unknown"
                },
                HeaderRow = -1,
                DateField = new DetectedField { ColumnIndex = -1, ColumnName = "Unknown" },
                DescriptionField = new DetectedField { ColumnIndex = -1, ColumnName = "Unknown" },
                DebitField = new DetectedField { ColumnIndex = -1, ColumnName = "Unknown" },
                CreditField = new DetectedField { ColumnIndex = -1, ColumnName = "Unknown" },
                AmountField = new DetectedField { ColumnIndex = -1, ColumnName = "Unknown" },
                PreviewTransactions = new List<TransactionPreview>()
            };
        }
    }

    // Generates a simple signature string based on the detected header names and their positions.
    private string GenerateHeaderSignature(
        string entityName,
        int dateColumn,
        int descriptionColumn,
        int debitColumn,
        int creditColumn,
        int amountColumn
    )
    {
        var signature = $"ENTITY:{entityName}|DATE:{dateColumn}|DESC:{descriptionColumn}|DEBIT:{debitColumn}|CREDIT:{creditColumn}|AMOUNT:{amountColumn}";

        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(signature));

        return Convert.ToHexString(bytes);
    }
}