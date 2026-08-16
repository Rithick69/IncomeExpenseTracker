using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using IncomeExpenditureTracker.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using IncomeExpenditureTracker.Services.Helpers;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Database;

namespace IncomeExpenditureTracker.Tests.Tests.Logic
{
    /// <summary>
    /// Tests the IHeaderDetector implementation by manually building ClosedXML worksheets in memory
    /// to ensure the system accurately identifies the starting row of a transaction table while ignoring metadata.
    /// </summary>
    // 1. Implement IDisposable on your test class
    public class HeaderDetectorTests : IDisposable
    {
        private readonly SqliteConnection _masterConnection;
        private readonly HeaderDetector _headerDetector;

        public HeaderDetectorTests()
        {
            // 2. Define a NAMED shared memory connection string
            var connectionString = "Data Source=TestSynonymDb;Mode=Memory;Cache=Shared";

            // 3. OPEN THE MASTER CONNECTION AND KEEP IT OPEN!
            // This prevents SQLite from destroying the DB between queries.
            _masterConnection = new SqliteConnection(connectionString);
            _masterConnection.Open();

            // 4. Pass that exact same connection string to your configuration
            var inMemorySettings = new Dictionary<string, string?>
        {
            { "ConnectionStrings:DefaultConnection", connectionString }
        };

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(inMemorySettings)
                .Build();

            var databaseService = new DatabaseService(configuration, NullLogger<DatabaseService>.Instance);

            // 5. Run your helper synchronously (ensure your helper actually executes the INSERT sql!)
            databaseService.SetupInMemorySynonymsTable().GetAwaiter().GetResult();

            var synonymService = new SynonymService(databaseService, NullLogger<SynonymService>.Instance);
            _headerDetector = new HeaderDetector(synonymService);
        }

        [Fact]
        public async Task DetectHeaderRow_WithStandardHeadersInRowFive_ReturnsRowFive()
        {
            // =========================================================================
            // ARRANGE: Manually create an in-memory workbook and push headers down to Row 5.
            // Rows 1 through 4 remain completely blank.
            // =========================================================================
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("StandardStatement");

            int expectedHeaderRow = 5;
            worksheet.Cell(expectedHeaderRow, 1).Value = "Date";
            worksheet.Cell(expectedHeaderRow, 2).Value = "Description";
            worksheet.Cell(expectedHeaderRow, 3).Value = "Amount";
            worksheet.Cell(expectedHeaderRow, 4).Value = "Balance";

            // =========================================================================
            // ACT: Execute the async header detection logic against our sheet
            // =========================================================================
            int actualHeaderRow = await _headerDetector.DetectHeaderRow(worksheet, forceReload: true);

            // =========================================================================
            // ASSERT: Verify the detector bypassed the empty rows and located Row 4.
            // NOTE: If your method returns an object instead of an int, adjust to actualHeaderRow.RowNumber!
            // =========================================================================
            Assert.Equal(expectedHeaderRow - 1, actualHeaderRow);
        }

        [Fact]
        public async Task DetectHeaderRow_WithMessyTopLevelMetadata_SkipsMetadataAndFindsCorrectTableStart()
        {
            // =========================================================================
            // ARRANGE: Simulate a bank statement where Rows 1 and 2 contain misleading account
            // metadata (including the word "Date"), but the real table starts on Row 4.
            // =========================================================================
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("MessyStatement");

            // Row 1 & 2: Misleading metadata
            worksheet.Cell(1, 1).Value = "Statement Date:";
            worksheet.Cell(1, 2).Value = "2026-07-01";
            worksheet.Cell(2, 1).Value = "Opening Balance:";
            worksheet.Cell(2, 2).Value = "$5,000.00";

            // Row 4: The ACTUAL transaction table headers
            int expectedHeaderRow = 4;
            worksheet.Cell(expectedHeaderRow, 1).Value = "Txn Date";
            worksheet.Cell(expectedHeaderRow, 2).Value = "Memo";
            worksheet.Cell(expectedHeaderRow, 3).Value = "Amount ($)";

            // =========================================================================
            // ACT: Scan the sheet
            // =========================================================================
            int actualHeaderRow = await _headerDetector.DetectHeaderRow(worksheet, forceReload: true);

            // =========================================================================
            // ASSERT: Verify the detector ignored Row 1's "Statement Date" and picked Row 3
            // =========================================================================
            Assert.Equal(expectedHeaderRow - 1, actualHeaderRow);
        }

        [Fact]
        public async Task DetectHeaderRow_WhenNoValidHeadersExist_HandlesGracefullyWithoutCrashing()
        {
            // =========================================================================
            // ARRANGE: Create a sheet containing random text that does NOT represent a transaction table
            // =========================================================================
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("GarbageSheet");

            worksheet.Cell(1, 1).Value = "Just some random shopping list notes";
            worksheet.Cell(2, 1).Value = "Apples, Bananas, Milk";

            // =========================================================================
            // ACT & ASSERT:
            // Verify that the detector correctly throws an InvalidOperationException
            // when presented with a garbage sheet that contains no valid headers.
            // =========================================================================
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _headerDetector.DetectHeaderRow(worksheet, forceReload: true)
            );

            // Optional: You can also assert that the exception message is exact
            Assert.Equal("Failed to detect header row.", exception.Message);
        }

        // 6. Dispose the master connection after the test completes so memory is freed
        public void Dispose()
        {
            _masterConnection?.Close();
            _masterConnection?.Dispose();
        }
    }
}