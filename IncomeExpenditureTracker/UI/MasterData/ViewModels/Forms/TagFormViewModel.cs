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
    public partial class TagFormViewModel : FormViewModelBase
    {
        // UI Bindings for Dropdowns
        public ObservableCollection<string> AvailableCategories { get; } = new();
        public ObservableCollection<string> AvailableSubCategories { get; } = new();

        // In-memory master lists for filtering
        private List<Category> _allCategories = new();
        private List<SubCategory> _allSubCategories = new();

        private string _subCategoryName = string.Empty;
        public string SubCategoryName
        {
            get => _subCategoryName;
            set
            {
                if (SetProperty(ref _subCategoryName, value))
                {
                    // 1. Hierarchy Validation
                    if (!string.IsNullOrWhiteSpace(CategoryName) && string.IsNullOrWhiteSpace(value))
                    {
                        AddError(nameof(SubCategoryName), "Sub-Category is required when a Category is selected.");
                    }
                    else
                    {
                        ClearError(nameof(SubCategoryName));
                    }
                    // Re-evaluate the Save button state when the user types
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Reactive Category Property
        private string _categoryName = string.Empty;
        public string CategoryName
        {
            get => _categoryName;
            set
            {
                if (SetProperty(ref _categoryName, value))
                {
                    UpdateDependentSubCategories();

                    // 1. Hierarchy Validation
                    if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(SubCategoryName))
                    {
                        AddError(nameof(SubCategoryName), "Sub-Category is required when a Category is selected.");
                    }
                    else
                    {
                        ClearError(nameof(SubCategoryName));
                    }

                    // Re-evaluate the Save button state when the user types
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    // Immediately tell the Save button to re-evaluate when typing
                    SaveCommand.NotifyCanExecuteChanged();
                    ValidateUniqueAsync(DomainEntity.Tag, "Name", value, "This Tag name already exists.");
                }
            }
        }

        public TagFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? tagId = null)
            : base(orchestrator, broker, tagId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                // Hydrate master lists
                _allCategories = (await Orchestrator.GetAllCategoriesAsync(ViewCts.Token)).ToList();
                _allSubCategories = (await Orchestrator.GetAllSubCategoriesAsync(ViewCts.Token)).ToList();

                var allTags = IsUpdateMode ? await Orchestrator.GetAllTagsAsync(ViewCts.Token) : null;
                var existing = allTags?.FirstOrDefault(t => t.Id == ExcludeId);

                RunOnUIThread(() =>
                {
                    AvailableCategories.Clear();
                    foreach (var c in _allCategories) if (!string.IsNullOrEmpty(c.Name)) AvailableCategories.Add(c.Name);

                    // Initially populate subcategories (will be filtered if update mode sets a category)
                    UpdateDependentSubCategories();

                    if (existing != null)
                    {
                        Name = existing.Name ?? ""; // This will trigger the OnPropertyChanged for Name

                        if (existing.SubCategoryId.HasValue && existing.SubCategoryId > 0)
                        {
                            var subCat = _allSubCategories.FirstOrDefault(s => s.Id == existing.SubCategoryId);
                            if (subCat != null)
                            {
                                var cat = _allCategories.FirstOrDefault(c => c.Id == subCat.CategoryId);
                                CategoryName = cat?.Name ?? "";
                                SubCategoryName = subCat.Name ?? "";
                            }
                        }
                    }
                });
            }
            catch (OperationCanceledException) { /* Silently abort */ }
        }

        // --- DEPENDENT DROPDOWN LOGIC ---
        private void UpdateDependentSubCategories()
        {
            AvailableSubCategories.Clear();

            if (string.IsNullOrWhiteSpace(CategoryName))
            {
                // Only show subcategories that are NOT assigned to any parent category
                foreach (var s in _allSubCategories.Where(sub => sub.CategoryId == null || sub.CategoryId == 0))
                {
                    if (!string.IsNullOrEmpty(s.Name)) AvailableSubCategories.Add(s.Name);
                }
            }

            // Find the ID of the selected category
            var selectedCat = _allCategories.FirstOrDefault(c => c.Name != null && c.Name.Equals(CategoryName, StringComparison.OrdinalIgnoreCase));

            if (selectedCat != null)
            {
                // Only show subcategories belonging to this category
                foreach (var s in _allSubCategories.Where(sub => sub.CategoryId == selectedCat.Id))
                {
                    if (!string.IsNullOrEmpty(s.Name)) AvailableSubCategories.Add(s.Name);
                }
            }
            // If selectedCat is null (user typed a brand new category), AvailableSubCategories stays naturally empty.
        }

        // --- BASE CLASS OVERRIDES ---
        protected override bool CanSave()
        {
            // 1. Block if base errors exist or Name is empty
            if (HasErrors || string.IsNullOrWhiteSpace(Name))
                return false;

            // 2. THE HIERARCHY RULE: If Category is provided, Sub-Category is strictly required.
            bool hasCategory = !string.IsNullOrWhiteSpace(CategoryName);
            bool hasSubCategory = !string.IsNullOrWhiteSpace(SubCategoryName);

            if (hasCategory && !hasSubCategory)
            {
                return false; // Disables the Save button
            }

            return true; // Allows save (handles fully empty, fully populated, or Sub-Category only)
        }

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            int? categoryId = null;
            if (!string.IsNullOrWhiteSpace(CategoryName))
            {
                // FIXED: Assign the result to the variable
                categoryId = await Orchestrator.GetOrCreateCategoryAsync(CategoryName.Trim(), ct);
            }

            int? subId = null;
            if (!string.IsNullOrWhiteSpace(SubCategoryName))
            {
                subId = await Orchestrator.GetOrCreateSubCategoryAsync(SubCategoryName.Trim(), categoryId, ct);
            }

            var tag = new Tag
            {
                Id = ExcludeId ?? 0,
                Name = Name.Trim(),
                SubCategoryId = subId
            };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateTagAsync(tag.Id, tag.Name, tag.SubCategoryId, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Tag, tag.Id));
            }
            else
            {
                await Orchestrator.GetOrCreateTagAsync(tag.Name, tag.SubCategoryId, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Tag));
            }
        }
    }
}