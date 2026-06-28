using System;
namespace IncomeExpenditureTracker.Models;

// ------------------------------------------------------------
// ACCOUNT MODEL
// ------------------------------------------------------------
// Represents a bank account or credit card.
//
// Accounts are used for:
// • Dashboard views
// • Grouping imports
// • Account-level analytics
//-------------------------------------------------------------
public class Account
{
    public int Id { get; set; }

    public string AccountNumber { get; set; } = "";

    public string CardNumber { get; set; } = "";

    public int EntityId { get; set; }

    public string EntityName { get; set; } = "";

    public string AccountType { get; set; } = "";

    public string Currency { get; set; } = "";

    public DateTime CreatedDate { get; set; }

    public string CreditLimit { get; set; } = "";

}