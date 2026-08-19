using System;
using System.Linq;
using Xunit;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Logging.Abstractions;
using IncomeExpenditureTracker.Services.StatementManagement;

namespace IncomeExpenditureTracker.Tests.Logic
{
    public class StatementEditSessionTests
    {
        // =================================================================================
        // POSITIVE PATHS: Simulating User Interactions
        // =================================================================================

        [Fact]
        public void EditSession_Positive_UserUpdatesMapping_AppliesToPreview_And_DeduplicatesCorrections()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Simulate a user actively changing column mappings in the UI.
            // DECISION: Prove that (1) the live preview updates immediately for the UI to bind to,
            // and (2) if the user changes their mind multiple times, the session only remembers
            // the FINAL decision (Smart Deduplication).
            // ---------------------------------------------------------------------------------

            // Arrange
            var session = new StatementEditSession(NullLogger<StatementEditSession>.Instance);
            var initialPreview = new StatementPreview();
            session.Initialize(initialPreview); // Step 1: Initialize the workbench

            // Act - The user clicks around in the UI...

            // User maps "PARTICULARS" to Description at Column 1
            session.UpdateColumnMapping("Description", "PARTICULARS", 1, "TRANSACTION");

            // User changes their mind, maps "TXN_DESC" to Description at Column 2
            session.UpdateColumnMapping("Description", "TXN_DESC", 2, "TRANSACTION");

            // User maps "DATE" to Date at Column 0
            session.UpdateColumnMapping("Date", "DATE", 0, "TRANSACTION");

            // Step 5: User clicks "Confirm & Import"
            var resultTracker = session.ConfirmAndPrepareForImport();

            // Assert 1: Live preview was mutated correctly
            var finalPreview = resultTracker.FinalPreview;
            Assert.True(finalPreview.Fields.ContainsKey("Description"));
            Assert.Equal(2, finalPreview.Fields["Description"].ColumnIndex); // Must be the final choice
            Assert.True(finalPreview.Fields["Description"].IsUserVerified);

            // Assert 2: Smart Deduplication worked.
            // Even though the user made 3 clicks, there should only be 2 corrections recorded.
            Assert.Equal(2, resultTracker.ColumnCorrections.Count);

            var descCorrection = resultTracker.ColumnCorrections.First(c => c.TargetField == "Description");
            Assert.Equal(2, descCorrection.NewColumnIndex);
            Assert.Equal("TXN_DESC", descCorrection.RawHeaderName);
        }

        // =================================================================================
        // NEGATIVE PATHS: Enforcing Guardrails & State Integrity
        // =================================================================================

        [Fact]
        public void EditSession_Negative_AccessWithoutInitialization_ThrowsInvalidOperationException()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Ensure strict workflow order.
            // DECISION: Prevent UI crashes by throwing explicitly if the UI tries to request
            // or modify the preview before the StatementManager has provided one.
            // ---------------------------------------------------------------------------------

            var session = new StatementEditSession(NullLogger<StatementEditSession>.Instance);

            // Act & Assert
            var ex1 = Assert.Throws<InvalidOperationException>(() => session.GetCurrentPreview());
            var ex2 = Assert.Throws<InvalidOperationException>(() => session.ConfirmAndPrepareForImport());

            Assert.Contains("Call Initialize() first", ex1.Message);
        }

        [Fact]
        public void EditSession_Negative_InvalidColumnIndex_ThrowsArgumentOutOfRangeException()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Protect zero-based indexing logic.
            // DECISION: Column 0 is valid (Column A). Anything less than 0 is structurally invalid.
            // ---------------------------------------------------------------------------------

            var session = new StatementEditSession(NullLogger<StatementEditSession>.Instance);
            session.Initialize(new StatementPreview());

            // Act & Assert
            var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
                session.UpdateColumnMapping("Description", "BAD_COL", -1, "TRANSACTION"));

            Assert.Contains("must be 0 or greater", exception.Message);
        }

        [Fact]
        public void EditSession_Negative_InitializeWithNull_ThrowsArgumentNullException()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Guard against null propagation from failed extractions.
            // ---------------------------------------------------------------------------------

            var session = new StatementEditSession(NullLogger<StatementEditSession>.Instance);

            // Act & Assert
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
            Assert.Throws<ArgumentNullException>(() => session.Initialize(null));
#pragma warning restore CS8625
        }

        [Fact]
        public void EditSession_Clear_WipesMemoryAndRequiresReinitialization()
        {
            // ---------------------------------------------------------------------------------
            // OBJECTIVE: Validate memory cleanup (Step 6).
            // DECISION: When Clear() is called, references must be dropped. Future calls
            // must fail until Initialize() is called again.
            // ---------------------------------------------------------------------------------

            // Arrange
            var session = new StatementEditSession(NullLogger<StatementEditSession>.Instance);
            session.Initialize(new StatementPreview());
            session.UpdateColumnMapping("Date", "DATE", 0, "TRANSACTION");

            // Act
            session.Clear();

            // Assert
            Assert.Throws<InvalidOperationException>(() => session.GetCurrentPreview());
            Assert.Throws<InvalidOperationException>(() => session.ConfirmAndPrepareForImport());
        }
    }
}