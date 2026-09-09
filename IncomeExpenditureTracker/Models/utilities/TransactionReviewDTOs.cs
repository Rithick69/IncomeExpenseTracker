using System;
using System.Collections.Generic;

namespace IncomeExpenditureTracker.Models;

/// <summary>
/// Flags for marking transactions that require review in the UI.
/// </summary>
[Flags]
public enum ReviewFlags
{
    None = 0,
    InvalidAmount = 1,
    InvalidDate = 2,
    MissingPayee = 4,
    MissingTag = 8,
    InvalidDescription = 16,
}

/// <summary>
/// A memory-efficient struct for executing batch corrections from the UI Grid.
/// </summary>
public record TransactionCorrectionDTO
{
    public long TransactionId { get; init; }
    public string Source { get; init; } = string.Empty; // The CleanedDescription exact-match key
    public string RawDescription { get; init; } = string.Empty; // Used for Tag Engine learning
    public DateTime Date { get; init; }
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public long? PayeeId { get; init; }
    public int? TargetTagId { get; init; }
    public ReviewFlags ReviewStatus { get; init; } // Bound directly to pass down user UI flag clears
}
/// <summary>
/// A memory-efficient struct for paginated and filtered UI queries.
/// </summary>
public readonly record struct TransactionFilterArgs(
    int? BatchId = null,
    int? AccountId = null,
    string? Source = null,
    string? SearchText = null,
    bool? IsTriageMode = null, // ADDED: True = Only show rows with ReviewStatus > 0
    ReviewFlags? SpecificError = null,     // Target a specific error (e.g., MissingPayee)
    int? Limit = 50,
    int? Offset = 0
);

/// <summary>
/// A generic envelope for paginated results returning to the UI.
/// </summary>
public class PagedResult<T>
{
    public IReadOnlyCollection<T> Items { get; init; } = new List<T>();
    public int TotalCount { get; init; }
}