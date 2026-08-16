using System.Collections.Generic;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.PreviewInsights;
using Xunit;

namespace IncomeExpenditureTracker.Tests.Logic;

public class ConfidenceServiceTests
{
    private readonly IConfidenceService _confidenceService;

    public ConfidenceServiceTests()
    {
        // ConfidenceService has no external DI dependencies, making it easily testable
        _confidenceService = new ConfidenceService();
    }

    /// <summary>
    /// Objective: Validate that empty or null inputs gracefully return a score of 0
    /// without throwing NullReferenceExceptions.
    /// </summary>
    [Fact]
    public void CalculateConfidence_EmptyInputs_ReturnsZero()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>();
        var previews = new List<TransactionPreview>();

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        Assert.Equal(0, score);
    }

    /// <summary>
    /// Objective: Validate the Amount layout branching (Single vs. Dual Column Mode).
    /// If a single "Amount" column is present, it should skip scoring "Debit" and "Credit".
    /// </summary>
    [Fact]
    public void CalculateConfidence_SingleAmountColumn_SkipsDebitCreditScoring()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>
            {
                // Single amount column gets up to 10 points
                { "Amount", new DetectedField { ConfidenceScore = 100.0 } }
            };
        var previews = new List<TransactionPreview>();

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        Assert.Equal(10, score);
    }

    /// <summary>
    /// Objective: Validate that confidence normalization scales correctly and caps at 100
    /// even if floating-point drift or exceptional sample sizes occur.
    /// </summary>
    [Fact]
    public void CalculateConfidence_PerfectMapping_CapsAt100()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>
            {
                { "EntityName", new DetectedField { ConfidenceScore = 100 } },
                { "AccountNumber", new DetectedField { ConfidenceScore = 100 } },
                { "CardNumber", new DetectedField { ConfidenceScore = 100 } },
                { "AccountType", new DetectedField { ConfidenceScore = 100 } },
                { "Currency", new DetectedField { ConfidenceScore = 100 } },
                { "Date", new DetectedField { ConfidenceScore = 100 } },
                { "Description", new DetectedField { ConfidenceScore = 100 } },
                { "Amount", new DetectedField { ConfidenceScore = 100 } }
            };

        var previews = new List<TransactionPreview>
            {
                // Generates high consistency and density scores
                new TransactionPreview { Date = System.DateTime.Now, Description = "Valid", Amount = 50 },
                new TransactionPreview { Date = System.DateTime.Now, Description = "Valid", Amount = 20 }
            };

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        Assert.Equal(100, score);
    }

    /// <summary>
    /// Objective: Validate that passing explicit null arguments does not throw a NullReferenceException
    /// and safely returns a score of 0.
    /// </summary>
    [Fact]
    public void CalculateConfidence_ExplicitNullArguments_ReturnsZero()
    {
        // Act
        int score = _confidenceService.CalculateConfidence(null!, null!);

        // Assert
        Assert.Equal(0, score);
    }

    /// <summary>
    /// Objective: Validate the Math.Clamp protection against rogue or corrupted confidence scores.
    /// Scores below 0 should be treated as 0, and scores absurdly high should cap at 1.0 multiplier.
    /// </summary>
    [Fact]
    public void CalculateConfidence_RogueConfidenceScores_ClampsToValidRange()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>
            {
                // Should clamp from -50.0 to 0.0 (Yields 0 points)
                { "EntityName", new DetectedField { ConfidenceScore = -50.0 } },
                // Should clamp from 500.0 to 100.0/1.0 (Yields max 15 points)
                { "Date", new DetectedField { ConfidenceScore = 500.0 } }
            };
        var previews = new List<TransactionPreview>();

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        // 0 points for EntityName + 15 points for Date = 15 total
        Assert.Equal(15, score);
    }

    /// <summary>
    /// Objective: Validate transaction sample scoring with completely invalid rows.
    /// Rows missing dates, descriptions, or valid amounts should yield 0 bonus points for density/consistency.
    /// </summary>
    [Fact]
    public void CalculateConfidence_InvalidPreviewTransactions_YieldsZeroBonus()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>(); // 0 points from fields

        var previews = new List<TransactionPreview>
            {
                // Invalid: Default Date, empty description, 0 amounts
                new TransactionPreview { Date = default, Description = "", Amount = 0, Debit = 0, Credit = 0 },
                new TransactionPreview { Date = default, Description = "   ", Amount = 0, Debit = 0, Credit = 0 }
            };

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        // Density and Consistency should both evaluate to a 0.0 ratio and return 0 points
        Assert.Equal(0, score);
    }

    /// <summary>
    /// Objective: Validate dual-column amount fallback logic. If "Amount" is completely missing,
    /// it must evaluate and aggregate both "Debit" and "Credit" columns instead.
    /// </summary>
    [Fact]
    public void CalculateConfidence_DualColumnAmounts_ScoresDebitAndCredit()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>
            {
                // Max 5 points
                { "Debit", new DetectedField { ConfidenceScore = 100.0 } },
                // Max 5 points
                { "Credit", new DetectedField { ConfidenceScore = 100.0 } }
            };
        var previews = new List<TransactionPreview>();

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        // Should add 5 for Debit and 5 for Credit
        Assert.Equal(10, score);
    }

    /// <summary>
    /// Objective: Validate that fractional confidence scales appropriately.
    /// A 50% confidence should yield exactly half the max weight for a rule.
    /// </summary>
    [Fact]
    public void CalculateConfidence_FractionalConfidence_ScalesProportionally()
    {
        // Arrange
        var fields = new Dictionary<string, DetectedField>
            {
                // Date Max Weight is 15. A 50 score -> 0.5 multiplier -> 7.5 points
                { "Date", new DetectedField { ConfidenceScore = 50.0 } }
            };
        var previews = new List<TransactionPreview>();

        // Act
        int score = _confidenceService.CalculateConfidence(fields, previews);

        // Assert
        // 7.5 rounds mathematically depending on Math.Round midpoint rounding (usually rounds to even, but Math.Round(7.5) is 8 in standard .NET unless specified)
        // Expecting 8 based on Math.Round(totalScore)
        Assert.Equal(8, score);
    }
}
