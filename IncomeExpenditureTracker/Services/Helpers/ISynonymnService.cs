using IncomeExpenditureTracker.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Helpers;

// This interface defines the contract for a synonym service that provides
// methods for loading column synonyms used by the Excel importer.
// This is used by the FieldMapper during Excel import to match column headers with expected fields.
public interface ISynonymnService
{
    // Loads all synonyms from the database.
    //
    // This is used by the FieldMapper during Excel import to match column headers with expected fields.
    public Task<List<Synonyms>> GetAllSynonyms();
}