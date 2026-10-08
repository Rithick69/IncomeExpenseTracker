using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Orchestration;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public partial class SubCategoryFormViewModel : FormViewModelBase
    {
        // --- AUTO-COMPLETE BINDINGS ---
        public ObservableCollection<string> AvailableCategories { get; } = new();
        private List<SubCategoryHierarchyDto> _allSubCategories = new();

        // --- FORM FIELDS ---
        private string _categoryName = string.Empty;

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    TriggerCompositeValidation();

                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string CategoryName
        {
            get => _categoryName;
            set
            {
                if (SetProperty(ref _categoryName, value))
                {
                    TriggerCompositeValidation();
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public SubCategoryFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? subCategoryId = null)
            : base(orchestrator, broker, subCategoryId)
        {
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {

                // Hydrate Categories for the AutoCompleteBox
                var categories = await Orchestrator.GetAllCategoriesAsync(ViewCts.Token);

                // Fetch the existing record
                _allSubCategories = (await Orchestrator.GetAllSubCategoriesWithHierarchyAsync(ViewCts.Token)).ToList();
                var existing = _allSubCategories.FirstOrDefault(p => p.SubCategoryId == ExcludeId);

                RunOnUIThread(() =>
                {
                    AvailableCategories.Clear();
                    foreach (var c in categories) if (!string.IsNullOrEmpty(c.Name)) AvailableCategories.Add(c.Name);

                    if (existing != null)
                    {
                        // Set the backing field directly to bypass the debounced uniqueness check on load
                        _name = existing.SubCategoryName ?? "";
                        OnPropertyChanged(nameof(Name));
                        CategoryName = existing.CategoryName ?? string.Empty;
                    }
                });
            }
            catch (OperationCanceledException) { /* Silently abort */ }
        }

        // --- COMPOSITE VALIDATION ENGINE ---
        private void TriggerCompositeValidation()
        {
            // We can only validate the combination if both fields have values
            if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(CategoryName))
            {
                ClearError(nameof(Name));
                return;
            }

            // Instantly resolve the CategoryName to the CategoryId using the in-memory cache
            var targetCat = _allSubCategories.FirstOrDefault(t => t.CategoryName != null && t.CategoryName.Equals(CategoryName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (targetCat == null)
            {
                // If the user typed a non-existent Category, we can't check composite uniqueness yet
                ClearError(nameof(Name));
                return;
            }

            var props = new Dictionary<string, object>
            {
                { "Name", Name.Trim() },
                { "CategoryId", targetCat.CategoryId } // Validate against the actual foreign key
            };

            // Will check UNIQUE(Name, CategoryId) via the Orchestrator
            ValidateUniqueCompositeAsync(DomainEntity.SubCategory, props, nameof(Name), "This Name is already mapped to this exact Category.");
        }

        // --- BASE CLASS OVERRIDES ---

        protected override bool CanSave()
            => !HasErrors && !string.IsNullOrWhiteSpace(Name);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            // Dynamically resolve or create the parent Entity (Merchant/Institution) from the AutoCompleteBox string
            int? categoryId = null;
            if (!string.IsNullOrWhiteSpace(CategoryName))
            {
                categoryId = await Orchestrator.GetOrCreateCategoryAsync(CategoryName, ct);
            }

            var subCategory = new SubCategory
            {
                Id = ExcludeId ?? 0,
                Name = Name.Trim(),
                CategoryId = categoryId
            };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateSubCategoryAsync(subCategory, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.SubCategory, subCategory.Id));
            }
            else
            {
                await Orchestrator.GetOrCreateSubCategoryAsync(subCategory.Name, subCategory.CategoryId, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.SubCategory));
            }
        }
    }
}