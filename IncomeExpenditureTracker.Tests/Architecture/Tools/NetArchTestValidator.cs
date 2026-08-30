using System.Reflection;
using NetArchTest.Rules;

namespace IncomeExpenditureTracker.Tests.Architecture.Tools
{
    public class NetArchTestValidator : IArchitectureValidator
    {
        public (bool IsValid, string[] FailingTypes) HasNoRogueDatabaseDependencies(Assembly targetAssembly, string baseNamespace, string exemptNamespace)
        {
            // 1. Target the compiled application assembly
            var types = Types.InAssembly(targetAssembly);

            // We refine the rule to ONLY ban the SQLite driver.
            // Domain services are allowed to use IDbConnection and Dapper inside the
            // ExecuteWithRetryAsync lambda delegates, but they can never instantiate a raw connection.

            // 2. Define the rule: Any class in the Services namespace...
            var rule = types
                .That()
                .ResideInNamespace(baseNamespace)
                // ...EXCEPT the actual Database namespace where the wrappers live...
                .And()
                .DoNotResideInNamespace(exemptNamespace)

                // ...MUST NOT depend on raw database connectors or Dapper.
                .Should()
                .NotHaveDependencyOn("Microsoft.Data.Sqlite");

            // 3. Execute the scan against the compiled Intermediate Language (IL)
            var result = rule.GetResult();

            // Extract the names of the classes that violated the rule
            var failingTypeNames = result.FailingTypes != null
                ? result.FailingTypes.Select(t => t.Name).ToArray()
                : Array.Empty<string>();

            return (result.IsSuccessful, failingTypeNames);
        }
    }
}