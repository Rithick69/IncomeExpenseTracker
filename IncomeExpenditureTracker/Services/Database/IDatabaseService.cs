using System;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
namespace IncomeExpenditureTracker.Services.Database;

// This interface defines the contract for a database service that provides
// a connection to the SQLite database used by the application.
// By using an interface, we can easily swap out the implementation of the database service
// in the future if needed (e.g., for testing or if we want to switch to a different database).

public interface IDatabaseService
{
    SqliteConnection GetConnection();

    // Executes an action with automatic retries for locked databases
    Task ExecuteWithRetryAsync(Func<SqliteConnection, Task> dbOperation, int maxRetries = 3);

    // Executes a query that returns a result with automatic retries
    Task<T> ExecuteWithRetryAsync<T>(Func<SqliteConnection, Task<T>> dbOperation, int maxRetries = 3);
}