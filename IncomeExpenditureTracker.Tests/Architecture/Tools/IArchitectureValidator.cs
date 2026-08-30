using System.Reflection;

namespace IncomeExpenditureTracker.Tests.Architecture.Tools
{
    /// <summary>
    /// Abstracts the underlying architecture scanning library (e.g., NetArchTest).
    /// Prevents tight coupling between our test suite and third-party validation tools.
    /// </summary>
    public interface IArchitectureValidator
    {
        /// <summary>
        /// Validates that no class outside the specified exempt namespace
        /// bypasses the database wrappers by referencing raw SQL or Dapper types.
        /// </summary>
        /// <param name="targetAssembly">The compiled application assembly to scan.</param>
        /// <param name="baseNamespace">The root namespace to check (e.g., Services).</param>
        /// <param name="exemptNamespace">The namespace allowed to touch the DB (e.g., DatabaseService).</param>
        /// <returns>True if the architecture is clean, false if rogue queries exist.</returns>
        (bool IsValid, string[] FailingTypes) HasNoRogueDatabaseDependencies(Assembly targetAssembly, string baseNamespace, string exemptNamespace);
    }
}