using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using IncomeExpenditureTracker.Models;
using Microsoft.Extensions.Configuration;
using IncomeExpenditureTracker.Tests.Helpers;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using Moq;


namespace IncomeExpenditureTracker.Tests.Tests.Logic
{
    /// <summary>
    /// Tests the IFieldMapper implementation to verify column headers and account metadata
    /// are correctly identified from an Excel worksheet.
    /// </summary>
    public class FieldMapperTests : IDisposable
    {
        // 1. Notice the interface is generic, but we do NOT declare the static ExcelStatementGenerator here.
        private readonly IFieldMapper<IXLWorksheet> _fieldMapper;

        private readonly Mock<IApplicationBroker> _brokerMock = new();
        private readonly SqliteConnection _masterConnection;

        public FieldMapperTests()
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

            var databaseService = new DatabaseService(configuration, NullLogger<DatabaseService>.Instance, _brokerMock.Object);

            // 5. Run your helper synchronously (ensure your helper actually executes the INSERT sql!)
            databaseService.SetupInMemorySynonymsTable().GetAwaiter().GetResult();

            var synonymService = new SynonymService(databaseService, NullLogger<SynonymService>.Instance, _brokerMock.Object);

            // 4. Finally, inject SynonymService and NullLogger into your FieldMapper
            _fieldMapper = new FieldMapper(synonymService, NullLogger<FieldMapper>.Instance);
        }

        [Fact]
        public async Task DetectColumns_WithStandardHeaders_ReturnsCorrectColumnMappings()
        {
            // =========================================================================
            // ARRANGE: Create a temporary in-memory workbook using our local helper method below.
            // =========================================================================
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("TestStatement");

            int headerRowIndex = 5;
            worksheet.Cell(headerRowIndex + 1, 1).Value = "Txn Date";      // Column A (Index 1)
            worksheet.Cell(headerRowIndex + 1, 2).Value = "Memo";          // Column B (Index 2)
            worksheet.Cell(headerRowIndex + 1, 3).Value = "Amount ($)";    // Column C (Index 3)
            worksheet.Cell(headerRowIndex + 1, 4).Value = "Running Bal";   // Column D (Ignore)

            // =========================================================================
            // ACT: Execute your real async column detection logic
            // =========================================================================
            Dictionary<string, DetectedField> detectedColumns = await _fieldMapper.DetectColumns(
                worksheet,
                headerRow: headerRowIndex,
                forceReload: true
            );

            // =========================================================================
            // ASSERT: Verify that your dictionary contains the expected transaction fields
            // =========================================================================
            Assert.True(detectedColumns.ContainsKey("Col:Date"));
            Assert.True(detectedColumns.ContainsKey("Col:Description"));
            Assert.True(detectedColumns.ContainsKey("Col:Amount"));

            // Zero based Indexing

            Assert.Equal(0, detectedColumns["Col:Date"].ColumnIndex);
            Assert.Equal(1, detectedColumns["Col:Description"].ColumnIndex);
            Assert.Equal(2, detectedColumns["Col:Amount"].ColumnIndex);
        }

        [Fact]
        public async Task DetectAccountDetails_WithBankMetadata_SuccessfullyExtractsAccountFields()
        {
            // =========================================================================
            // ARRANGE: Setup top-level account metadata in Rows 1 and 2
            // =========================================================================
            using var workbook = new XLWorkbook();
            var worksheet = workbook.Worksheets.Add("TestStatement");

            worksheet.Cell(1, 1).Value = "Account Number:";
            worksheet.Cell(1, 2).Value = "1234-5678-9012";
            worksheet.Cell(2, 1).Value = "Account Holder:";
            worksheet.Cell(2, 2).Value = "JOHN DOE";

            // =========================================================================
            // ACT: Scan the sheet for account metadata
            // =========================================================================
            Dictionary<string, DetectedField> metadataFields = await _fieldMapper.DetectAccountDetails(
                worksheet,
                forceReload: true
            );

            // =========================================================================
            // ASSERT: Verify the metadata dictionary grabbed the right info
            // =========================================================================
            Assert.NotNull(metadataFields);
            Assert.True(metadataFields.ContainsKey("Meta:Account_Number"));

            Assert.Equal("1234-5678-9012", metadataFields["Meta:Account_Number"].ExtractedValue);

        }
        public void Dispose()
        {
            _masterConnection?.Close();
            _masterConnection?.Dispose();
        }
    }
}