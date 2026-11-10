using System;
using System.Threading.Tasks;

namespace IncomeExpenditureTracker.Services.Export
{
    /// <summary>
    /// Provides export functionality for analytical reports and transaction data.
    /// Integrates with ClosedXML for Excel generation.
    /// </summary>
    public interface IExportService
    {
        /// <summary>
        /// Exports the current report data to an Excel file.
        /// </summary>
        /// <param name="reportType">Type of report to export (e.g., "SpendTracker", "CashFlow")</param>
        /// <param name="startDate">Start date of the reporting period</param>
        /// <param name="endDate">End date of the reporting period</param>
        /// <param name="outputPath">File path to save the Excel export</param>
        /// <returns>True if export succeeded, false otherwise</returns>
        Task<bool> ExportToExcelAsync(string reportType, DateTime startDate, DateTime endDate, string? outputPath = null);
    }
}
