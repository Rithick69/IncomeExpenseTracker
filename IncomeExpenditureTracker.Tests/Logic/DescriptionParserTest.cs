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
        private readonly IDescriptionParser _descriptionParser;

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

        [Theory]
        // 1. Standard cleaning: removes dates, long IDs, and special chars, collapses spaces
        [InlineData("POS PURCHASE 12-10-2023 #924010035959984 STARBUCKS-COFFEE", "POS PURCHASE STARBUCKS COFFEE")]
        // 2. Short numbers are kept (under 5 digits) and special characters become spaces
        [InlineData("7-ELEVEN STORE 1234", "7 ELEVEN STORE 1234")]
        [InlineData("AMZN Mktp US*AMZN.COM/BILLWA", "AMZN MKTP US AMZN COM BILLWA")]
        // 3. Different Date Formats
        [InlineData("PAYMENT 01/10/24 REF 123456", "PAYMENT REF")]
        // 4. TIER 1 GUARDRAIL: If only noise is present, return original (trimmed and uppercase)
        [InlineData("12-10-2023 924010035959984", "12-10-2023 924010035959984")]
        [InlineData("## 12/12/2023", "## 12/12/2023")]
        // 5. Null or Whitespace handling
        [InlineData("   ", "")]
        [InlineData(null, "")]
        public void SanitizeMerchantString_WithVariousBankStrings_CleansCorrectlyAndRespectsGuardrails(string input, string expected)
        {
            // =========================================================================
            // ACT: Call the sanitization method
            // =========================================================================
            var actual = _descriptionParser.SanitizeMerchantString(input);

            // =========================================================================
            // ASSERT: Verify the cleaned string matches the expected output
            // =========================================================================
            Assert.Equal(expected, actual);
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