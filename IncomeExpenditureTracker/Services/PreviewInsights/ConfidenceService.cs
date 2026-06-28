using System;
using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.PreviewInsights;

// ------------------------------------------------------------
// ConfidenceService.cs
// ------------------------------------------------------------
// This service calculates a confidence score for the provided account and transaction column mapping.
// The score is based on the presence of key account fields and transaction columns, with a maximum score of 100.
// This score can be used to provide feedback to users about the quality of the detected information before they proceed with importing their statement.
// ------------------------------------------------------------

public sealed class ConfidenceService
{
    public int CalculateConfidence(
        Account account,
        TransColumnMap map,
        List<TransactionPreview> previewTransactions)
    {
        int score = 0;

        // ------------------------------------------------------------
        // ACCOUNT DETECTION (40)
        // ------------------------------------------------------------

        if (!string.IsNullOrWhiteSpace(account.EntityName))
            score += 15;

        if (!string.IsNullOrWhiteSpace(account.AccountNumber))
            score += 15;

        if (!string.IsNullOrWhiteSpace(account.CardNumber))
            score += 5;

        if (!string.IsNullOrWhiteSpace(account.AccountType))
            score += 3;

        if (!string.IsNullOrWhiteSpace(account.Currency))
            score += 2;

        // ------------------------------------------------------------
        // COLUMN DETECTION (40)
        // ------------------------------------------------------------

        if (map.DateColumn > 0)
            score += 15;

        if (map.DescriptionColumn > 0)
            score += 15;

        if (map.DebitColumn > 0 || map.CreditColumn > 0 || map.AmountColumn > 0)
            score += 10;

        // ------------------------------------------------------------
        // SAMPLE TRANSACTION VALIDATION (20)
        // ------------------------------------------------------------

        // A higher consistency of valid transactions in the preview sample increases confidence
        score += CalculateColumnConsistency(previewTransactions);

        // A higher density of valid transactions in the preview sample increases confidence
        score += CalculateTransactionDensity(previewTransactions);

        return Math.Min(score, 100);
    }

    // ------------------------------------------------------------
    // PRIVATE HELPER METHODS
    // ------------------------------------------------------------
    // These methods analyze the sample transactions to assess how well the detected columns are capturing valid transaction data.
    // They return additional confidence points based on the consistency and density of valid transactions in the sample.
    // ------------------------------------------------------------


    // ------------------------------------------------------------
    // CalculateTransactionDensity
    // ------------------------------------------------------------
    // This method calculates the density of valid transactions in the preview sample.
    // A valid transaction is one that has a valid date, a non-empty description, and a valid amount (either a positive debit, a positive credit, or a non-zero amount for combined columns).
    // The method returns a confidence score based on the percentage of valid transactions in the sample.
    // ------------------------------------------------------------

    private int CalculateTransactionDensity(List<TransactionPreview> previewRows)
    {
        if (previewRows == null || previewRows.Count == 0)
            return 0;

        int validRows = 0;

        foreach (var tx in previewRows)
        {
            if (tx.Date != default &&
                !string.IsNullOrWhiteSpace(tx.Description) &&
                (tx.Debit > 0 || tx.Credit > 0 || tx.Amount != 0))
            {
                validRows++;
            }
        }

        double density = (double)validRows / previewRows.Count;

        if (density > 0.9)
            return 15;

        if (density > 0.75)
            return 12;

        if (density > 0.6)
            return 8;

        if (density > 0.4)
            return 4;

        return 0;
    }

    // ------------------------------------------------------------
    // CalculateColumnConsistency
    // ------------------------------------------------------------
    // This method calculates the consistency of valid transactions in the preview sample.
    // A valid transaction is one that has a valid date, a non-empty description, and a valid amount (either a positive debit, a positive credit, or a non-zero amount for combined
    // columns).
    // The method returns a confidence score based on the percentage of valid transactions in the sample, with higher consistency yielding a higher score.
    // ------------------------------------------------------------

    private int CalculateColumnConsistency(List<TransactionPreview> previewRows)
    {
        if (previewRows == null || previewRows.Count == 0)
            return 0;

        int validRows = 0;

        foreach (var tx in previewRows)
        {
            bool validDate = tx.Date != default;
            bool validDesc = !string.IsNullOrWhiteSpace(tx.Description);

            // A valid amount is either a positive debit, a positive credit, or a non-zero amount (for combined columns)

            bool validAmount =
                tx.Debit > 0 ||
                tx.Credit > 0 ||
                tx.Amount != 0;

            if (validDate && validDesc && validAmount)
                validRows++;
        }

        double ratio = (double)validRows / previewRows.Count;

        if (ratio > 0.9)
            return 20;

        if (ratio > 0.7)
            return 15;

        if (ratio > 0.5)
            return 10;

        return 0;
    }
}