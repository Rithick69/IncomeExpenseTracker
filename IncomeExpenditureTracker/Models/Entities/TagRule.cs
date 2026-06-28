// Import basic .NET types
using System;

namespace IncomeExpenditureTracker.Models;

// TagRule defines how transactions should be categorized automatically
// based on keywords found in their descriptions.
public class TagRule
{
    // Unique identifier for the rule in the database
    public int Id { get; set; }

    // Keyword that we search for inside transaction descriptions
    // Example: "SWIGGY", "ZERODHA", "AMAZON"
    public string Keyword { get; set; } = "";

    // ------------------------------------------------------------
    // TAG ID
    // ------------------------------------------------------------
    // Foreign key referencing the Tags table.
    //
    // Example:
    // TagId = 5 → "Groww"
    // TagId = 6 → "Mutual Fund"
    // ------------------------------------------------------------
    public int TagId { get; set; }

    // ------------------------------------------------------------
    // PRIORITY
    // ------------------------------------------------------------
    // Determines which rule wins if multiple rules match.
    //
    // Higher number = higher priority.
    //
    // Example:
    // MUTUALFUNDS → Priority 100
    // GROWW       → Priority 80
    //
    // If both match, MUTUALFUNDS wins.
    // ------------------------------------------------------------
    public int Priority { get; set; }
}