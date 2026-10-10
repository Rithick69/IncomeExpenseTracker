using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IncomeExpenditureTracker.Models;

namespace IncomeExpenditureTracker.Services.Importing;

public interface IStatementImport<in TDocument>
{
    Task ImportConfirmedBatchAsync(TDocument document, IEnumerable<PreviewTracker> trackers, CancellationToken ct = default);
}