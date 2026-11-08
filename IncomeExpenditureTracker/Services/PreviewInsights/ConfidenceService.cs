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

public sealed class ConfidenceService : IConfidenceService
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

        // =========================================================
        // 1. MANDATORY ACCOUNT DETAILS (Max 30 points)
        // =========================================================
        // XOR LOGIC: We only need ONE identifier (Account OR Card).
        // We evaluate both and only grant points for the highest one. No penalties for the missing one.
        double accNoScore = EvaluateField(unifiedFields, new[] { "Meta:ACCOUNT_NUMBER", "AccountNumber" }, 30);
        double cardNoScore = EvaluateField(unifiedFields, new[] { "Meta:CARD_NUMBER", "CardNumber" }, 30);
        totalScore += Math.Max(accNoScore, cardNoScore);

        // =========================================================
        // 2. OPTIONAL ACCOUNT DETAILS (Bonus Points)
        // =========================================================
        // These do not deduct from the score if missing, but they act as a safety net.
        totalScore += EvaluateField(unifiedFields, new[] { "Meta:ENTITY_NAME", "EntityName" }, 5);
        totalScore += EvaluateField(unifiedFields, new[] { "Meta:ACCOUNT_TYPE", "AccountType" }, 3);
        totalScore += EvaluateField(unifiedFields, new[] { "Meta:CURRENCY", "Currency" }, 2);

        // =========================================================
        // 3. CORE TRANSACTION COLUMNS (Max 30 points)
        // =========================================================
        totalScore += EvaluateField(unifiedFields, new[] { "Col:DATE", "Date" }, 15);
        totalScore += EvaluateField(unifiedFields, new[] { "Col:DESCRIPTION", "Description" }, 15);

        // =========================================================
        // 4. AMOUNT COLUMNS (Max 15 points)
        // =========================================================
        double singleAmountScore = EvaluateField(unifiedFields, new[] { "Col:AMOUNT", "Amount" }, 10);
        if (singleAmountScore > 0)
        {
            totalScore += singleAmountScore;
            totalScore += EvaluateField(unifiedFields, new[] { "Col:TRANSACTIONTYPE", "TransactionType" }, 5);
        }
        else
        {
            // Split evenly so a standard Dr/Cr layout yields the full 15 points
            totalScore += EvaluateField(unifiedFields, new[] { "Col:DEBIT", "Debit" }, 7.5);
            totalScore += EvaluateField(unifiedFields, new[] { "Col:CREDIT", "Credit" }, 7.5);
        }

        // =========================================================
        // 5. TRANSACTION SAMPLE VALIDATION (Max 25 points)
        // =========================================================
        totalScore += CalculateGridQuality(previewTransactions);

        // =========================================================
        // FINAL CALCULATION
        // =========================================================
        // Perfect Core (75) + Perfect Grid (25) = 100 Base Points.
        // Bonus Labels (Bank, Type, Currency) grant up to 10 extra points.
        // Cap strictly at 100.
        return Math.Clamp((int)Math.Round(totalScore), 0, 100);
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
                // RULE 1: Account Detail Value Fields (Meta)
                // If this is an Account field, score it purely on whether the actual VALUE is populated.
                // This prevents AI Training labels (which have no value) from falsely altering the score.
                if (key.StartsWith("Meta:", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(field.ExtractedValue))
                    {
                        return maxWeight; // The value is populated (either by user or AI) -> 100% confidence
                    }
                    else
                    {
                        return 0; // No value, no points. The label itself doesn't grant confidence.
                    }
                }

                // RULE 2: Transaction Columns (Col)
                // For core structural columns, rely on the standard ConfidenceScore multiplier.
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
    // CalculateGridQuality
    // ------------------------------------------------------------
    // Evaluates how cleanly the detected columns map to actual transaction rows.
    // Replaces the redundant Density/Consistency methods. Grants up to 25 points.
    // ------------------------------------------------------------
    private int CalculateGridQuality(List<TransactionPreview> previewRows)
    {
        if (previewRows == null || previewRows.Count == 0) return 0;

        int validRows = 0;
        foreach (var tx in previewRows)
        {
            // A valid row must have a Date, Description, and some form of Amount
            if (tx.Date != default &&
                !string.IsNullOrWhiteSpace(tx.Description) &&
                (tx.Debit > 0 || tx.Credit > 0 || tx.Amount != 0))
            {
                validRows++;
            }
        }

        double ratio = (double)validRows / previewRows.Count;

        if (ratio > 0.90) return 25;
        if (ratio > 0.75) return 18;
        if (ratio > 0.50) return 10;

        return 0;
    }
}