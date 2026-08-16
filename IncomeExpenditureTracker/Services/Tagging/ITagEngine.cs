using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Tagging
{
    /// <summary>
    /// Interface for the TagEngine service, which processes transactions and applies tagging logic.
    /// </summary>
    public interface ITagEngine
    {
        /// <summary>
        /// Processes a list of transactions and applies tagging based on provided token rows.
        /// </summary>
        /// <param name="transactions">The list of transactions to process.</param>
        /// <param name="tokenRows">The token rows used for tagging logic.</param>
        Task ProcessTransactions(List<Transaction> transactions, List<List<string>> tokenRows);
    }
}