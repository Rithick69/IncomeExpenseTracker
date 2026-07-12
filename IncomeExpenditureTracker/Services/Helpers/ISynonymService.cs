using IncomeExpenditureTracker.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace IncomeExpenditureTracker.Services.Helpers;

// This interface defines the contract for a synonym service that provides
// methods for loading column synonyms used by the Excel importer.
// This is used by the FieldMapper during Excel import to match column headers with expected fields.
public interface ISynonymService
{
    // Loads all synonyms from the database.
    //
    // This is used by the FieldMapper during Excel import to match column headers with expected fields.

    // Fetches all records for the processors to build their indexes

    // IEnumerable<Synonyms> is used to allow for deferred execution and efficient memory usage, especially when dealing with large datasets.
    // Read only access is provided to ensure that the collection cannot be modified, maintaining data integrity.
    Task<IEnumerable<Synonyms>> GetAllSynonyms();

    // Called when the user maps an unknown column in the preview UI
    Task LearnFromCorrectionAsync(string rawSynonym, string fieldType, string category);

    // CRUD operations for the dedicated manual management UI
    Task AddSynonymAsync(Synonyms synonym);

    // Method for the management UI to fix mistakes
    Task UpdateSynonymAsync(Synonyms synonym);

    // Deletion now targets the primary key for precision in the UI
    Task DeleteSynonymAsync(int id);

    /// <summary>
    /// Ensures that all standard domain concepts exist in the database.
    /// Should be called during application startup by DatabaseInitializer.
    /// </summary>
    Task SeedDefaultFieldTypesAsync(IEnumerable<string> standardFieldTypes, string category);

    Task<IReadOnlyDictionary<string, Synonyms>> GetSynonymsByCategory(string category);
}