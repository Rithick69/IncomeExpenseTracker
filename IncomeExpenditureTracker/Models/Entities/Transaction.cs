// Import basic .NET types such as DateTime and decimal
using System;

namespace IncomeExpenditureTracker.Models;

// This class represents a single transaction row imported
// from a bank statement or credit card statement.
public class Transaction
{
    // Unique identifier for the transaction.
    // SQLite will automatically generate this value.
    public int Id { get; set; }

    // Date on which the transaction occurred.
    // This comes directly from the statement row.
    public DateTime Date { get; set; }


    // Name of the statement source.
    // This usually comes from the imported file.
    // Example:
    // "HDFC Bank"
    // "SBI Savings"
    // "ICICI Credit Card"
    // public string Account { get; set; } = "";

    // Foreign key reference to the Account table.
    public int AccountId { get; set; }

    // Full raw description exactly as it appears in the statement.
    // Example:
    // "UPI/P2M/640701415738/GROWW INVEST TECH PVT/Paid V/HDFC BANK LTD"
    public string Description { get; set; } = "";

    // Cleaned counterparty extracted from the description.
    // This is just for readability in the UI table.
    // Example values:
    // "GROWW INVEST TECH PVT"
    // "INFOSYS LIMITED"
    // "SWIGGY"
    public string Entity { get; set; } = "";

    // Amount credited to the account (money received).
    // Example: Salary, refunds, transfers received.
    public decimal Credit { get; set; }

    // Amount debited from the account (money spent).
    // Example: UPI payments, investments, withdrawals.
    public decimal Debit { get; set; }

    // Indicates whether the transaction came from
    // a bank account or a credit card statement.
    //
    // Example values:
    // "Bank"
    // "Credit"
    public string TransactionType { get; set; } = "";

    public int? ImportBatchId { get; set; }

    // Reference to the tag assigned by the rule engine.
    // The tag will determine the category and subcategory.
    // Example tags:
    // Salary
    // Food
    // Zerodha
    // Mutual Fund
    // This is a foreign key referencing the Tags table.
    public int? TagId { get; set; }

    public string TransactionHash { get; set; } = "";

    public DateTime CreatedDate { get; set; }
}