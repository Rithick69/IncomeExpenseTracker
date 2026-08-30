using Xunit;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Tests.Architecture.Tools;

namespace IncomeExpenditureTracker.Tests.Architecture
{
    public class DatabaseEncapsulationTests
    {
        private readonly IArchitectureValidator _validator;

        public DatabaseEncapsulationTests()
        {
            // Injecting the concrete validator from our new Tools namespace.
            _validator = new NetArchTestValidator();
        }

        [Fact]
        public void All_Database_Operations_Must_Route_Through_DatabaseService_Wrappers()
        {
            // Arrange
            // We use a known type to grab the core application assembly
            var coreAssembly = typeof(CategoryService).Assembly;

            // Define our boundaries based on the architecture principles
            var domainServicesNamespace = "IncomeExpenditureTracker.Services";
            var isolatedDatabaseNamespace = "IncomeExpenditureTracker.Services.Database";

            // Act
            // The validator scans the AST/IL to ensure no domain service is bypassing
            // ExecuteWithRetryAsync or ExecuteInTransactionWithRetryAsync.
            // Deconstruct the tuple returned by the validator
            var (isEncapsulated, failingTypes) = _validator.HasNoRogueDatabaseDependencies(
                coreAssembly,
                domainServicesNamespace,
                isolatedDatabaseNamespace
            );

            // Dynamically build the error message to list the exact rogue classes
            var errorMessage = $"Rogue database query detected in classes: {string.Join(", ", failingTypes)}. All SQLite operations must be routed through IDatabaseService.";

            // Assert
            Assert.True(isEncapsulated, errorMessage);
        }
    }
}