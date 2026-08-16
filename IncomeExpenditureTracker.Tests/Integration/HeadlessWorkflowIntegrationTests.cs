using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using Moq;
using Xunit;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Helpers;

namespace IncomeExpenditureTracker.Tests.Integration
{
    public class HeadlessWorkflowIntegrationTests : IDisposable
    {
        private readonly SqliteConnection _inMemoryConnection;
        private readonly TagService _tagService;
        private readonly Mock<IDatabaseService> _dbServiceWrapper;

        private readonly ITestOutputHelper _output;

        public HeadlessWorkflowIntegrationTests(ITestOutputHelper output)
        {
            _output = output;

            // ---------------------------------------------------------------------------------
            // DECISION: Spin up a real, completely isolated in-memory SQLite database.
            // ---------------------------------------------------------------------------------
            _inMemoryConnection = new SqliteConnection("DataSource=:memory:");
            _inMemoryConnection.Open(); // Must stay open for the lifespan of the test

            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Build the minimal schema required for the domain logic to function.
            // This proves our SQL syntax in the actual services will work in production.
            // ---------------------------------------------------------------------------------
            _inMemoryConnection.Execute(@"
                CREATE TABLE Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    SubCategoryId INTEGER NULL
                );

                CREATE TABLE TagRules (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Keyword TEXT NOT NULL,
                    TagId INTEGER NOT NULL,
                    Priority INTEGER NOT NULL DEFAULT 10,
                    FOREIGN KEY(TagId) REFERENCES Tags(Id) ON DELETE CASCADE
                );
            ");

            // ---------------------------------------------------------------------------------
            // DECISION: We mock the IDatabaseService purely to route ExecuteWithRetryAsync
            // directly to our open in-memory connection, bypassing the real file path logic.
            // The SQL executed inside it is 100% REAL.
            // ---------------------------------------------------------------------------------
            _dbServiceWrapper = new Mock<IDatabaseService>();

            _dbServiceWrapper
                .Setup(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<int>>>()))
                .Returns<Func<IDbConnection, Task<int>>>(func => func(_inMemoryConnection));

            _dbServiceWrapper
                .Setup(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task<RuleBookSnapshot>>>()))
                .Returns<Func<IDbConnection, Task<RuleBookSnapshot>>>(func => func(_inMemoryConnection));

            _dbServiceWrapper
                .Setup(db => db.ExecuteWithRetryAsync(It.IsAny<Func<IDbConnection, Task>>()))
                .Returns<Func<IDbConnection, Task>>(async func => await func(_inMemoryConnection));

            // Initialize REAL services for the workflow
            var descriptionLogger = new Mock<ILogger<DescriptionParser>>();
            IDescriptionParser descriptionParser = new DescriptionParser(descriptionLogger.Object);
            var loggerMock = new Mock<ILogger<TagService>>();

            _tagService = new TagService(_dbServiceWrapper.Object, descriptionParser, loggerMock.Object);
        }

        [Fact]
        public async Task EndToEnd_PreviewHalt_UserEdit_And_BackgroundLearning_Workflow()
        {
            // =================================================================================
            // PHASE 1: SYSTEM BOOTSTRAP (The Setup)
            // Objective: Seed the fresh database with initial user configuration.
            // =================================================================================

            // User creates a 'Groceries' tag and a 'Misc' tag
            int groceriesTagId = await _tagService.GetOrCreateTagAsync("Groceries", 1);
            int miscTagId = await _tagService.GetOrCreateTagAsync("Misc", 999);

            // User manually adds a baseline rule
            await _tagService.AddRuleAsync("WALMART", groceriesTagId, 10);

            // =================================================================================
            // PHASE 2: THE INGESTION & PREVIEW (The Halt)
            // Objective: Simulate a bank statement row coming in that the AI doesn't understand.
            // =================================================================================

            var snapshot1 = await _tagService.GetRuleBookSnapshotAsync();

            // Simulating TagEngine logic: "WALMART" is known, but "TARGET" is not.
            string rawBankDescription = "POS DEBIT TARGET STORE 0092";

            // Because "TARGET" isn't in snapshot1, the system flags this transaction as "Requires Review"
            // (In the real app, this transaction halts and enters the Preview UI)
            bool isRuleKnown = snapshot1.RuleIndex.ContainsKey("TARGET");
            Assert.False(isRuleKnown, "The system should not know about TARGET yet, simulating a Preview Halt.");

            // =================================================================================
            // PHASE 3: THE EDIT SERVICE (The User Intervention)
            // Objective: The user steps in, assigns 'Groceries', and triggers self-learning.
            // =================================================================================

            // The EditService routes the raw description to the learning loop
            await _tagService.LearnRuleFromOverrideAsync(rawBankDescription, groceriesTagId);

            // =================================================================================
            // PHASE 4: THE RIPPLE EFFECT (Final Validation)
            // Objective: Prove the system learned the rule, assigned correct priority,
            // and blew up the RAM cache so the next transaction uses the new brain.
            // =================================================================================

            // Fetch the snapshot again. Because LearnRule invalidated the cache,
            // this proves it successfully rebuilds from SQLite.
            var snapshot2 = await _tagService.GetRuleBookSnapshotAsync();

            // Since we are using the REAL DescriptionParser, we don't know exactly what the
            // longest token was (it might have stripped the numbers or bank prefixes).
            // Let's dynamically find the NEW rule that was just added for the Groceries tag
            // (ignoring the original "WALMART" baseline rule).
            var newlyLearnedRule = snapshot2.RuleIndex.Values
                .SelectMany(rules => rules) // Flatten the dictionary into a single list of rules
                .FirstOrDefault(r => r.TagId == groceriesTagId && r.Keyword != "WALMART");

            // LOG THE RESULT TO THE TEST EXPLORER
            _output.WriteLine("");
            _output.WriteLine("=========================================================");
            _output.WriteLine($"RAW INPUT : {rawBankDescription}");
            _output.WriteLine($"EXTRACTED : {newlyLearnedRule.Keyword}");
            _output.WriteLine("=========================================================");

            // Log what it actually learned to the test runner just for your own visibility
            // e.g., it might have been "TARGET STORE" or "TARGET"
            Assert.False(string.IsNullOrWhiteSpace(newlyLearnedRule.Keyword),
                "The learned keyword should not be empty.");

            // Assert: Verify Priority Math logic executed successfully in the DB
            Assert.Equal(groceriesTagId, newlyLearnedRule.TagId);

            // Base rule was 10, the new learned rule must be calculated as 11
            Assert.Equal(11, newlyLearnedRule.Priority);
        }

        public void Dispose()
        {
            // Close the in-memory database, completely wiping it from RAM.
            _inMemoryConnection.Close();
            _inMemoryConnection.Dispose();
        }

        [Fact]
        public async Task EndToEnd_Negative_LearnRuleWithEmptyDescription_FailsGracefully()
        {
            // =================================================================================
            // OBJECTIVE: Negative Path Testing for the Learning Service.
            // DECISION: If a user or the system passes a blank string to the learning engine,
            // it should immediately abort without crashing the application.
            // =================================================================================

            // Arrange
            int targetTagId = 1;
            string rawBankDescription = "   "; // Whitespace only

            // Act
            // The method handles this gracefully and logs a warning instead of throwing.
            await _tagService.LearnRuleFromOverrideAsync(rawBankDescription, targetTagId);

            // Assert
            // We verify the database wasn't touched by fetching the snapshot and ensuring it remains empty.
            var snapshot = await _tagService.GetRuleBookSnapshotAsync();
            Assert.Empty(snapshot.RuleIndex);
        }

        [Fact]
        public async Task EndToEnd_Negative_AddRuleWithEmptyKeyword_ThrowsArgumentException()
        {
            // =================================================================================
            // OBJECTIVE: Negative Path Testing for direct rule additions.
            // DECISION: Prevent corrupted, empty rules from entering the SQLite database.
            // =================================================================================

            // Arrange
            int tagId = 1;
            string invalidKeyword = "";

            // Act & Assert
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _tagService.AddRuleAsync(invalidKeyword, tagId, 10));

            Assert.Contains("cannot be empty", exception.Message);
        }
    }
}