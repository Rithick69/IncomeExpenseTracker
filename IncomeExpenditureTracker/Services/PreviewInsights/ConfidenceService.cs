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

// FieldScoringRule defines a rule for scoring a specific field or set of fields.
// Each rule specifies the target keys to look for and the maximum weight (points) that can be awarded if the field is present and valid.
public record FieldScoringRule(string[] TargetKeys, double MaxWeight);

public sealed class ConfidenceService
{

    // 1. Define your scoring rules cleanly in one place (can easily be moved to a JSON config or database later!)
    private static readonly List<FieldScoringRule> AccountRules = new()
    {
        new(new[] { "Meta:ENTITY_NAME", "EntityName" }, 15),
        new(new[] { "Meta:ACCOUNT_NUMBER", "AccountNumber" }, 15),
        new(new[] { "Meta:CARD_NUMBER", "CardNumber" }, 5),
        new(new[] { "Meta:ACCOUNT_TYPE", "AccountType" }, 3),
        new(new[] { "Meta:CURRENCY", "Currency" }, 2)
    };

    private static readonly List<FieldScoringRule> CoreColumnRules = new()
    {
        new(new[] { "Col:DATE", "Date" }, 15),
        new(new[] { "Col:DESCRIPTION", "Description" }, 15)
    };

    public int CalculateConfidence(
        Dictionary<string, DetectedField> unifiedFields,
        List<TransactionPreview> previewTransactions)
    {

        double totalScore = 0;

        // 2. Dynamically evaluate Account & Core Column rules via loops
        foreach (var rule in AccountRules)
            totalScore += EvaluateField(unifiedFields, rule.TargetKeys, rule.MaxWeight);

        foreach (var rule in CoreColumnRules)
            totalScore += EvaluateField(unifiedFields, rule.TargetKeys, rule.MaxWeight);

        // 3. Handle Amount layout branching (Single vs. Dual Column Mode)
        double singleAmountScore = EvaluateField(unifiedFields, new[] { "Col:AMOUNT", "Amount" }, 10);
        if (singleAmountScore > 0)
        {
            totalScore += singleAmountScore;
        }
        else
        {
            totalScore += EvaluateField(unifiedFields, new[] { "Col:DEBIT", "Debit" }, 5);
            totalScore += EvaluateField(unifiedFields, new[] { "Col:CREDIT", "Credit" }, 5);
        }

        // ------------------------------------------------------------
        // SAMPLE TRANSACTION VALIDATION (20)
        // ------------------------------------------------------------

        // A higher consistency of valid transactions in the preview sample increases confidence
        totalScore += CalculateColumnConsistency(previewTransactions);

        // A higher density of valid transactions in the preview sample increases confidence
        totalScore += CalculateTransactionDensity(previewTransactions);

        return Math.Min((int)Math.Round(totalScore), 100);
    }

    // ------------------------------------------------------------
    // PRIVATE HELPER METHODS
    // ------------------------------------------------------------
    // These methods analyze the sample transactions to assess how well the detected columns are capturing valid transaction data.
    // They return additional confidence points based on the consistency and density of valid transactions in the sample.
    // ------------------------------------------------------------

    /// <summary>
    /// Safely retrieves a DetectedField from the dictionary by evaluating possible keys,
    /// and returns its weighted score contribution scaled by detection confidence.
    /// </summary>
    private double EvaluateField(Dictionary<string, DetectedField> dictionary, string[] possibleKeys, double maxWeight)
    {
        if (dictionary == null) return 0;

        foreach (var key in possibleKeys)
        {
            if (dictionary.TryGetValue(key, out var field) && field != null)
            {
                // Normalize confidence to a 0.0 - 1.0 multiplier.
                // Automatically handles both 0-100 percentage scales (e.g., 85.0 -> 0.85)
                // and 0.0-1.0 ratio scales (e.g., 0.85 -> 0.85).
                double confidenceFactor = field.ConfidenceScore > 1.0 ? field.ConfidenceScore / 100.0 : field.ConfidenceScore;

                // Clamp factor between 0 and 1 to guard against rogue values or floating-point drift
                confidenceFactor = Math.Clamp(confidenceFactor, 0.0, 1.0);

                return maxWeight * confidenceFactor;
            }
        }

        return 0;
    }

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