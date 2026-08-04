using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Tagging;

namespace IncomeExpenditureTracker.Tests.Logic
{
    /// <summary>
    /// Validates the deterministic 3-tier scoring matrix and memory-safe execution of the TagEngine.
    /// </summary>
    public class TagEngineTests
    {
        private readonly Mock<ITagService> _mockTagService;
        private readonly Mock<ILogger<TagEngine>> _mockLogger;
        private readonly TagEngine _sut; // System Under Test

        // Common test constants
        private const int MiscTagId = 999;
        private const int TagFood = 10;
        private const int TagTransport = 20;

        public TagEngineTests()
        {
            _mockTagService = new Mock<ITagService>();
            _mockLogger = new Mock<ILogger<TagEngine>>();

            // Inject dependencies into the TagEngine
            _sut = new TagEngine(_mockTagService.Object, _mockLogger.Object);
        }

        /// <summary>
        /// Helper to generate a standardized snapshot mimicking the async lazy cache registry.
        /// </summary>
        private void SetupRuleBookSnapshot(Dictionary<string, TagRuleDTO[]> ruleIndex)
        {
            var snapshot = new RuleBookSnapshot(ruleIndex, MiscTagId);
            _mockTagService
                .Setup(s => s.GetRuleBookSnapshotAsync())
                .ReturnsAsync(snapshot);
        }

        [Fact]
        public async Task ProcessTransactions_Tier1_HighestPriorityWins()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate Tier 1 of the deterministic scoring matrix.
            // If multiple tags are matched, the rule with the highest database priority must win.
            // ---------------------------------------------------------------------------------

            // Arrange
            var ruleIndex = new Dictionary<string, TagRuleDTO[]>
            {
                { "UBER", new[] { new TagRuleDTO { TagId = TagTransport, Priority = 5 } } },
                { "EATS", new[] { new TagRuleDTO { TagId = TagFood, Priority = 10 } } } // Higher Priority
            };
            SetupRuleBookSnapshot(ruleIndex);

            var transactions = new List<Transaction> { new Transaction { Description = "UBER EATS" } };
            var tokenRows = new List<List<string>> { new List<string> { "UBER", "EATS" } };

            // Act
            await _sut.ProcessTransactions(transactions, tokenRows);

            // Assert
            Assert.Equal(TagFood, transactions[0].TagId);
        }

        [Fact]
        public async Task ProcessTransactions_Tier2_MatchCountTieBreaker()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate Tier 2 tie-breaker logic.
            // If two tags have the EXACT same priority, the tag with the most keyword hits wins.
            // ---------------------------------------------------------------------------------

            // Arrange
            var ruleIndex = new Dictionary<string, TagRuleDTO[]>
            {
                // Both tags have a priority of 5.
                { "MCDONALDS", new[] { new TagRuleDTO { TagId = TagFood, Priority = 5 } } },
                { "DOORDASH", new[] { new TagRuleDTO { TagId = TagFood, Priority = 5 } } },
                { "UBER", new[] { new TagRuleDTO { TagId = TagTransport, Priority = 5 } } }
            };
            SetupRuleBookSnapshot(ruleIndex);

            var transactions = new List<Transaction> { new Transaction { Description = "MCDONALDS DOORDASH UBER" } };

            // Token row has 2 hits for Food (MCDONALDS, DOORDASH) and 1 hit for Transport (UBER).
            var tokenRows = new List<List<string>> { new List<string> { "MCDONALDS", "DOORDASH", "UBER" } };

            // Act
            await _sut.ProcessTransactions(transactions, tokenRows);

            // Assert
            // TagFood wins because Match Count = 2 vs Match Count = 1
            Assert.Equal(TagFood, transactions[0].TagId);
        }

        [Fact]
        public async Task ProcessTransactions_Tier3_AmbiguityResolvesToMisc()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate the Ambiguity Guardrail (Tier 3).
            // If two different tags tie on BOTH Priority AND Match Count, the engine refuses to guess.
            // It must set the fallback MiscTagId to prevent silent data misclassification.
            // ---------------------------------------------------------------------------------

            // Arrange
            var ruleIndex = new Dictionary<string, TagRuleDTO[]>
            {
                { "UBER", new[] { new TagRuleDTO { TagId = TagTransport, Priority = 5 } } },
                { "EATS", new[] { new TagRuleDTO { TagId = TagFood, Priority = 5 } } }
            };
            SetupRuleBookSnapshot(ruleIndex);

            var transactions = new List<Transaction> { new Transaction { Description = "UBER EATS" } };

            // Exactly 1 hit of Priority 5 for Transport, and 1 hit of Priority 5 for Food.
            var tokenRows = new List<List<string>> { new List<string> { "UBER", "EATS" } };

            // Act
            await _sut.ProcessTransactions(transactions, tokenRows);

            // Assert
            // The row is ambiguous, so it must fall back to MiscTagId
            Assert.Equal(MiscTagId, transactions[0].TagId);
        }

        [Fact]
        public async Task ProcessTransactions_RowLevelFaultIsolation_AssignsMiscAndContinues()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate row-level fault isolation.
            // A malformed row or internal exception should not crash the Parallel.For loop.
            // The faulted row should default to MiscTagId, and valid rows should process normally.
            // ---------------------------------------------------------------------------------

            // Arrange
            var ruleIndex = new Dictionary<string, TagRuleDTO[]>
            {
                { "VALID", new[] { new TagRuleDTO { TagId = TagFood, Priority = 5 } } },

                // POISON PILL: We insert a null array.
                // When the loop hits "CRASH", matchingRules.Length will throw a NullReferenceException.
                { "CRASH", null! }
            };
            SetupRuleBookSnapshot(ruleIndex);

            var transactions = new List<Transaction>
            {
                new Transaction { Description = "This row will crash" },
                new Transaction { Description = "This row is valid" }
            };

            var tokenRows = new List<List<string>>
            {
                new List<string> { "CRASH" }, // This token triggers the poison pill
                new List<string> { "VALID" }  // This token processes normally
            };

            // Act
            await _sut.ProcessTransactions(transactions, tokenRows);

            // Assert
            // Faulted row gracefully falls back
            Assert.Equal(MiscTagId, transactions[0].TagId);
            // Valid row processes correctly
            Assert.Equal(TagFood, transactions[1].TagId);
        }

        [Fact]
        public async Task ProcessTransactions_ClearsTokenArraysForGarbageCollection()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate Explicit Memory Reclamation.
            // The 'finally' block must invoke .Clear() on the token arrays to allow instantaneous GC reclamation.
            // ---------------------------------------------------------------------------------

            // Arrange
            var dummyRuleIndex = new Dictionary<string, TagRuleDTO[]>
            {
                { "DUMMY", new[] { new TagRuleDTO { TagId = 1, Priority = 1 } } }
            };
            SetupRuleBookSnapshot(dummyRuleIndex); // Provide a non-empty rulebook

            var transactions = new List<Transaction> { new Transaction { Description = "TEST" } };
            var tokenRow = new List<string> { "TEST" };
            var tokenRows = new List<List<string>> { tokenRow };

            // Act
            await _sut.ProcessTransactions(transactions, tokenRows);

            // Assert
            // The parent list and inner lists must be cleared
            Assert.Empty(tokenRows);
            Assert.Empty(tokenRow);
        }

        [Fact]
        public async Task ProcessTransactions_CountMismatch_ThrowsArgumentException()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate pre-flight error handling.
            // The method must abort if transaction counts and token rows are out of sync.
            // ---------------------------------------------------------------------------------

            // Arrange
            var transactions = new List<Transaction> { new Transaction(), new Transaction() };
            var tokenRows = new List<List<string>> { new List<string>() }; // Mismatched count

            // Act & Assert
            await Assert.ThrowsAsync<ArgumentException>(() => _sut.ProcessTransactions(transactions, tokenRows));
        }
    }
}