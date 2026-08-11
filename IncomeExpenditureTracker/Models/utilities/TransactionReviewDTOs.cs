using System.Collections.Generic;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Models;

/// <summary>
/// A memory-efficient struct for executing batch corrections from the UI Grid.
/// </summary>
public readonly record struct TransactionCorrectionDTO(
    int TransactionId,
    int? TargetTagId,
    string RawDescription, // Kept for the background learning engine
    System.DateTime Date,
    string Source,         // The cleaned counterparty name
    decimal Debit,
    decimal Credit
);

/// <summary>
/// A memory-efficient struct for paginated and filtered UI queries.
/// </summary>
public readonly record struct TransactionFilterArgs(
    int? BatchId = null,
    int? AccountId = null,
    string? Source = null,
    string? SearchText = null,
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