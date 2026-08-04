using Xunit;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions; // REQUIRED: Gives us access to NullLogger<T>
using IncomeExpenditureTracker.Services.Helpers;

namespace IncomeExpenditureTracker.Tests.Tests.Logic
{
    /// <summary>
    /// Tests the DescriptionParser to ensure bank transaction strings are cleanly tokenized,
    /// stripping digits, special characters, and extra spaces while preserving important words.
    /// </summary>
    public class DescriptionParserTests
    {
        private readonly DescriptionParser _descriptionParser;

        public DescriptionParserTests()
        {
            // =========================================================================
            // In standard xUnit, we instantiate the class we want to test directly in the constructor.
            // (If DescriptionParser requires dependencies like ILogger, we can pass mocks here later!)
            // =========================================================================
            // =========================================================================
            // We pass NullLogger<DescriptionParser>.Instance into the constructor.
            // This acts as a safe, dummy logger that absorbs all logging calls during our tests
            // without needing your main application's Dependency Injection system!
            // =========================================================================
            _descriptionParser = new DescriptionParser(NullLogger<DescriptionParser>.Instance);
        }

        [Fact]
        public void ExtractTokens_WithStandardBankString_StripsDigitsAndReturnsCleanTokenList()
        {
            // =========================================================================
            // ARRANGE: Set up the messy bank string and the exact list of tokens we expect back.
            // =========================================================================
            string rawBankDescription = "POS PURCHASE #1234 * STARBUCKS COFFEE 99021 ";

            // Because ExtractTokens returns a List<string>, our expected result must also be a List<string>.
            var expectedTokens = new List<string>
            {
                "POS",
                "POS PURCHASE",
                "POS PURCHASE STARBUCKS",
                "POS PURCHASE STARBUCKS COFFEE",
                "PURCHASE",
                "PURCHASE STARBUCKS",
                "PURCHASE STARBUCKS COFFEE",
                "STARBUCKS",
                "STARBUCKS COFFEE",
                "COFFEE"
            };

            // =========================================================================
            // ACT: Call your actual method to get the list of extracted words.
            // =========================================================================
            var actualTokens = _descriptionParser.ExtractTokens(rawBankDescription);

            // =========================================================================
            // ASSERT: Verify that the actual list of words matches our expected list exactly.
            // Assert.Equal on two lists checks that both have the same items in the exact same order!
            // =========================================================================
            Assert.Equal(expectedTokens, actualTokens);
        }

        [Theory]
        [InlineData("PAYMENT TO OF 1500", new[] { "TO", "TO OF", "OF" })]
        [InlineData("HP DIRECT PURCHASE", new[] { "HP", "HP DIRECT", "HP DIRECT PURCHASE", "DIRECT", "DIRECT PURCHASE", "PURCHASE" })]
        [InlineData("A B C MART", new[] { "MART" })]
        public void ExtractTokens_WithShortAcronymsAndWords_DoesNotRemoveShortTokens(string input, string[] expectedArray)
        {
            var actualTokens = _descriptionParser.ExtractTokens(input);
            Assert.Equal(expectedArray, actualTokens);
        }
    }
}