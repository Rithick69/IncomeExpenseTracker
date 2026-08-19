using System;
using System.Data;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Logging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Orchestration;
using Moq;
using Xunit;
using Castle.Core.Logging;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.Tests.Integration
{
    public class MasterDataOrchestratorTests
    {
        private readonly Mock<IDatabaseService> _dbMock = new();
        private readonly Mock<ICategoryService> _categoryMock = new();
        private readonly Mock<ISubCategoryService> _subCategoryMock = new();
        private readonly Mock<ITagService> _tagMock = new();
        private readonly Mock<IEntityService> _entityMock = new();
        private readonly Mock<IAccountService> _accountMock = new();
        private readonly Mock<ITransactionService> _transactionMock = new();
        private readonly Mock<IImportBatchService> _importBatchMock = new();
        private readonly Mock<ISynonymService> _synonymMock = new();

        private readonly Mock<ILogger<MasterDataOrchestrator>> _loggerMock = new();
        private readonly Mock<IApplicationBroker> _brokerMock = new();

        private MasterDataOrchestrator CreateOrchestrator()
        {
            return new MasterDataOrchestrator(
                _dbMock.Object, _categoryMock.Object, _subCategoryMock.Object,
                _tagMock.Object, _entityMock.Object, _accountMock.Object,
                _transactionMock.Object, _importBatchMock.Object,
                _synonymMock.Object, _brokerMock.Object, _loggerMock.Object);
        }

        private void SetupDatabaseTransactionMock()
        {
            // Boilerplate setup to intercept the transaction wrapper and execute the inner closure synchronously
            _dbMock.Setup(x => x.ExecuteInTransactionWithRetryAsync(It.IsAny<Func<IDbConnection, IDbTransaction, Task>>()))
                   .Returns<Func<IDbConnection, IDbTransaction, Task>>(async action =>
                   {
                       await action.Invoke(null!, null!); // Utilizing null! to satisfy the compiler
                   });
        }

        /// <summary>
        /// Objective: Validate Taxonomy Safe Re-parenting. When deleting a SubCategory,
        /// all child tags must be "floated" (SubCategoryId = NULL) before the deletion occurs.
        /// </summary>
        [Fact]
        public async Task DeleteSubCategorySafeAsync_FloatsTags_AndDeletesSubCategory()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();
            int subCatId = 5;

            // Act
            await orchestrator.DeleteSubCategorySafeAsync(subCatId);

            // Assert
            _tagMock.Verify(x => x.FloatTagsBySubCategoryAsync(subCatId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _subCategoryMock.Verify(x => x.DeleteSubCategory(subCatId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Validate Fallback to Misc Tag (ID 999). Deleting a tag must safely re-parent
        /// all its transactions to the system Misc Tag and wipe its rules.
        /// </summary>
        [Fact]
        public async Task DeleteTagSafeAsync_ReassignsTransactionsToMiscTag_AndClearsRules()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();
            int targetTagId = 10;
            int miscTagId = 999;

            // Mock the system tag retrieval to return a valid ID
            _tagMock.Setup(x => x.GetTagIdByName(It.IsAny<string>())).ReturnsAsync(miscTagId);

            // Act
            await orchestrator.DeleteTagSafeAsync(targetTagId);

            // Assert
            _transactionMock.Verify(x => x.ReassignTransactionsToFallbackTagAsync(targetTagId, miscTagId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _tagMock.Verify(x => x.DeleteRulesByTagId(targetTagId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _tagMock.Verify(x => x.DeleteTagAsync(targetTagId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Ensure the ultimate system safeguard prevents deleting the Misc Tag itself.
        /// </summary>
        [Fact]
        public async Task DeleteTagSafeAsync_AttemptToDeleteMiscTag_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            int miscTagId = 999;

            _tagMock.Setup(x => x.GetTagIdByName(It.IsAny<string>())).ReturnsAsync(miscTagId);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await orchestrator.DeleteTagSafeAsync(miscTagId));
        }

        /// <summary>
        /// Objective: Validate Structural Hard Block Guardrails. Attempting to delete an Account
        /// that holds transactions must trigger an immediate hard block to protect historical ledgers.
        /// </summary>
        [Fact]
        public async Task DeleteAccountAsync_AccountHasTransactions_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            int accountId = 1;

            _accountMock.Setup(x => x.HasTransactionsAsync(accountId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>())).ReturnsAsync(true);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await orchestrator.DeleteAccountAsync(accountId));

            Assert.Contains("Cannot delete this Account because it contains historical transactions", ex.Message);
            _accountMock.Verify(x => x.DeleteAccount(It.IsAny<int>(), It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Never);
        }

        /// <summary>
        /// Objective: Validate Duplicate Entity Resolution. Merging an entity must shift all child
        /// accounts safely and then delete the source entity under a unified transaction.
        /// </summary>
        [Fact]
        public async Task MergeEntitiesAsync_ValidMerge_ReassignsAccountsAndDeletesSource()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            SetupDatabaseTransactionMock();
            int sourceEntityId = 100;
            int targetEntityId = 200;

            // Act
            await orchestrator.MergeEntitiesAsync(sourceEntityId, targetEntityId);

            // Assert
            _accountMock.Verify(x => x.ReassignAccountsAsync(sourceEntityId, targetEntityId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
            _entityMock.Verify(x => x.DeleteEntity(sourceEntityId, It.IsAny<IDbConnection>(), It.IsAny<IDbTransaction>()), Times.Once);
        }

        /// <summary>
        /// Objective: Prevent merging an entity into itself.
        /// </summary>
        [Fact]
        public async Task MergeEntitiesAsync_SameSourceAndTarget_ThrowsInvalidOperationException()
        {
            // Arrange
            var orchestrator = CreateOrchestrator();
            int entityId = 100;

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await orchestrator.MergeEntitiesAsync(entityId, entityId));

            Assert.Contains("Source and target entities cannot be the same", ex.Message);
        }
    }
}