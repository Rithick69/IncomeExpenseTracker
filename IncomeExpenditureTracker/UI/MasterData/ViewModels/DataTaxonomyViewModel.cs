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
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.MasterData
{
    /// <summary>
    /// Centralized state manager for the Data Management hub.
    /// Manages Categories, Tags, Payees, Accounts, Rules, Synonyms, and Import Batches.
    /// Registered as Transient.
    /// </summary>
    public partial class DataTaxonomyViewModel : ViewModelBase
    {
        private readonly IMasterDataOrchestrator _orchestrator;
        private readonly ITransactionReviewOrchestrator _transactionOrchestrator;
        private readonly IApplicationBroker _broker;
        private readonly CancellationTokenSource _cts = new();

        [ObservableProperty]
        private bool _isLoading;

        #region Search & Menu Filter State

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMenuSearchText))]
        private string _menuSearchText = string.Empty;

        public bool HasMenuSearchText => !string.IsNullOrWhiteSpace(MenuSearchText);

        [ObservableProperty] private bool _isPayeesTabVisible = true;
        [ObservableProperty] private bool _isTagsTabVisible = true;
        [ObservableProperty] private bool _isCategoriesTabVisible = true;
        [ObservableProperty] private bool _isSubCategoriesTabVisible = true;
        [ObservableProperty] private bool _isAccountsTabVisible = true;
        [ObservableProperty] private bool _isEntitiesTabVisible = true;
        [ObservableProperty] private bool _isTagRulesTabVisible = true;
        [ObservableProperty] private bool _isSynonymsTabVisible = true;
        [ObservableProperty] private bool _isImportBatchesTabVisible = true;
        [ObservableProperty] private bool _isUserSettingsTabVisible = true;

        partial void OnMenuSearchTextChanged(string value)
        {
            ApplyMenuFilter(value);
        }

        [RelayCommand]
        public void ClearMenuSearch()
        {
            MenuSearchText = string.Empty;
        }

        private void ApplyMenuFilter(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                IsPayeesTabVisible = true;
                IsTagsTabVisible = true;
                IsCategoriesTabVisible = true;
                IsSubCategoriesTabVisible = true;
                IsAccountsTabVisible = true;
                IsEntitiesTabVisible = true;
                IsTagRulesTabVisible = true;
                IsSynonymsTabVisible = true;
                IsImportBatchesTabVisible = true;
                IsUserSettingsTabVisible = true;
                return;
            }

            var q = query.Trim().ToLowerInvariant();
            IsPayeesTabVisible = "payees".Contains(q);
            IsTagsTabVisible = "tags".Contains(q);
            IsCategoriesTabVisible = "categories".Contains(q);
            IsSubCategoriesTabVisible = "subcategories".Contains(q) || "sub-categories".Contains(q);
            IsAccountsTabVisible = "accounts".Contains(q);
            IsEntitiesTabVisible = "entities".Contains(q) || "merchants".Contains(q) || "institutions".Contains(q);
            IsTagRulesTabVisible = "tag rules".Contains(q) || "rules".Contains(q);
            IsSynonymsTabVisible = "synonyms".Contains(q) || "headers".Contains(q);
            IsImportBatchesTabVisible = "audit logs".Contains(q) || "batches".Contains(q) || "imports".Contains(q);
            IsUserSettingsTabVisible = "settings".Contains(q) || "preferences".Contains(q);
        }

        #endregion

        #region Entity Search & Filter State (Zero DB Call In-Memory Filtering)

        // --- PAYEES ---
        [ObservableProperty] private string? _selectedPayeeFilter;
        public ObservableCollection<string> PayeeFilterOptions { get; } = new() { "All", "With Mappings", "Unmapped" };
        partial void OnSelectedPayeeFilterChanged(string? value) => FilterPayees();

        // --- TAGS ---
        [ObservableProperty] private string? _selectedTagFilter;
        public ObservableCollection<string> TagFilterOptions { get; } = new() { "All", "Categorized", "Uncategorized (Misc)" };
        partial void OnSelectedTagFilterChanged(string? value) => FilterTags();

        // --- ACCOUNTS ---
        [ObservableProperty] private string? _selectedAccountFilter;
        public ObservableCollection<string> AccountFilterOptions { get; } = new() { "All", "Bank Accounts (Cash/Checking)", "Credit Lines (Cards/Loans)" };
        partial void OnSelectedAccountFilterChanged(string? value) => FilterAccounts();

        // --- TAG RULES ---
        [ObservableProperty] private string? _selectedTagRuleFilter;
        public ObservableCollection<string> TagRuleFilterOptions { get; } = new() { "All", "High Priority", "Standard Priority" };
        partial void OnSelectedTagRuleFilterChanged(string? value) => FilterTagRules();

        // // --- SYNONYMS ---
        // [ObservableProperty] private string? _selectedSynonymFilter;
        // public ObservableCollection<string> SynonymFilterOptions { get; } = new() { "All", "Mapped to Category", "Unmapped" };
        // partial void OnSelectedSynonymFilterChanged(string? value) => FilterSynonyms();

        // --- TAG FILTERS (Category -> SubCategory) ---
        [ObservableProperty] private string? _selectedTagCategoryFilter;
        [ObservableProperty] private string? _selectedTagSubCategoryFilter;

        public ObservableCollection<SelectableFilter> AvailableTagCategories { get; } = new();
        public ObservableCollection<SelectableFilter> AvailableTagSubCategories { get; } = new();

        partial void OnSelectedTagCategoryFilterChanged(string? value) => FilterTags();
        partial void OnSelectedTagSubCategoryFilterChanged(string? value) => FilterTags();

        // --- ACCOUNT FILTERS (Entity -> Account) ---
        [ObservableProperty] private string? _selectedAccountEntityFilter;

        public ObservableCollection<string> AvailableAccountEntities { get; } = new();

        partial void OnSelectedAccountEntityFilterChanged(string? value) => FilterAccounts();

        // --- TAG RULE FILTERS (Tag -> Rule) ---
        [ObservableProperty] private string? _selectedTagRuleTagFilter;

        public ObservableCollection<SelectableFilter> AvailableTagRuleTags { get; } = new();
        partial void OnSelectedTagRuleTagFilterChanged(string? value) => FilterTagRules();

        // --- SYNONYM FILTERS(CategoryType -> Synonym)
        [ObservableProperty] private string? _selectedSynonymCategoryFilter;
        [ObservableProperty] private string? _selectedSynonymFieldFilter;

        public ObservableCollection<string> AvailableSynonymCategory { get; } = new();
        public ObservableCollection<SelectableFilter> AvailableSynonymField { get; } = new();
        partial void OnSelectedSynonymFieldFilterChanged(string? value) => FilterSynonyms();

        // --- SUBCATEGORY FILTERS(Category -> SubCategory)
        [ObservableProperty] private string? _selectedSubCategoryCategoryFilter;

        public ObservableCollection<SelectableFilter> AvailableSubCatgCategory { get; } = new();

        partial void OnSelectedSubCategoryCategoryFilterChanged(string? value) => FilterSubCategories();


        // --- SEARCH PROPERTIES ---
        [ObservableProperty] private string _categorySearchText = string.Empty;
        [ObservableProperty] private string _subCategorySearchText = string.Empty;
        [ObservableProperty] private string _accountSearchText = string.Empty;
        [ObservableProperty] private string _entitySearchText = string.Empty;
        [ObservableProperty] private string _tagRuleSearchText = string.Empty;
        [ObservableProperty] private string _synonymSearchText = string.Empty;
        [ObservableProperty] private string _userSettingSearchText = string.Empty;
        [ObservableProperty] private string _payeeSearchText = string.Empty;
        [ObservableProperty] private string _tagSearchText = string.Empty;
        [ObservableProperty] private string _importBatchSearchText = string.Empty;

        partial void OnCategorySearchTextChanged(string value) => FilterCategories();
        partial void OnSubCategorySearchTextChanged(string value) => FilterSubCategories();
        partial void OnAccountSearchTextChanged(string value) => FilterAccounts();
        partial void OnEntitySearchTextChanged(string value) => FilterEntities();
        partial void OnTagRuleSearchTextChanged(string value) => FilterTagRules();
        partial void OnSynonymSearchTextChanged(string value) => FilterSynonyms();
        partial void OnUserSettingSearchTextChanged(string value) => FilterUserSettings();
        partial void OnPayeeSearchTextChanged(string value) => FilterPayees();
        partial void OnTagSearchTextChanged(string value) => FilterTags();
        partial void OnImportBatchSearchTextChanged(string value) => FilterImportBatches();

        // --- FILTERED COLLECTIONS ---
        public ObservableCollection<Category> FilteredCategories { get; } = new();
        public ObservableCollection<SubCategoryHierarchyDto> FilteredSubCategories { get; } = new();
        public ObservableCollection<Account> FilteredAccounts { get; } = new();
        public ObservableCollection<Entity> FilteredEntities { get; } = new();
        public ObservableCollection<TagRuleHierarchyDto> FilteredTagRules { get; } = new();
        public ObservableCollection<SynonymsHierarchyDto> FilteredSynonyms { get; } = new();
        public ObservableCollection<UserSetting> FilteredUserSettings { get; } = new();
        public ObservableCollection<TagHierarchyDto> FilteredTags { get; } = new();
        public ObservableCollection<Payee> FilteredPayees { get; } = new();
        public ObservableCollection<ImportBatch> FilteredImportBatches { get; } = new();

        #endregion

        #region Backing Master Collections

        public ObservableCollection<Category> Categories { get; } = new();
        public ObservableCollection<SubCategoryHierarchyDto> SubCategories { get; } = new();
        public ObservableCollection<TagHierarchyDto> Tags { get; } = new();
        public ObservableCollection<Payee> Payees { get; } = new();
        public ObservableCollection<Account> Accounts { get; } = new();
        public ObservableCollection<Entity> Entities { get; } = new();
        public ObservableCollection<TagRuleHierarchyDto> TagRules { get; } = new();
        public ObservableCollection<SynonymsHierarchyDto> Synonyms { get; } = new();
        public ObservableCollection<UserSetting> UserSettings { get; } = new();
        public ObservableCollection<ImportBatch> ImportBatches { get; } = new();

        #endregion

        public DataTaxonomyViewModel(IMasterDataOrchestrator orchestrator, ITransactionReviewOrchestrator transactionReviewOrchestrator, IApplicationBroker broker) : base(broker)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _transactionOrchestrator = transactionReviewOrchestrator ?? throw new ArgumentNullException(nameof(transactionReviewOrchestrator));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));

            _broker.Register<EntitySavedMessage>(this, OnEntityChanged);
            _broker.Register<EntityUpdatedMessage>(this, OnEntityChanged);
            _broker.Register<EntityDeletedMessage>(this, OnEntityChanged);
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            IsLoading = true;
            try
            {
                await Task.WhenAll(
                    LoadCategoriesAsync(isSilent: true),
                    LoadTagsAsync(isSilent: true),
                    LoadPayeesAsync(isSilent: true),
                    LoadAccountsAsync(isSilent: true),
                    LoadSubCategoriesAsync(isSilent: true),
                    LoadUserSettingsAsync(isSilent: true),
                    LoadEntitiesAsync(isSilent: true),
                    LoadSynonymsAsync(isSilent: true),
                    LoadTagRulesAsync(isSilent: true),
                    LoadImportBatchesAsync(isSilent: true)
                );
            }
            finally
            {
                IsLoading = false;
            }
        }

        #region Data Loaders (With Silent Refresh Support & Hierarchy DTOs)

        private async Task LoadCategoriesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllCategoriesAsync(_cts.Token);
                Console.WriteLine($"Loaded {data.Count()} categories from the orchestrator.");
                RunOnUIThread(() =>
                {
                    Categories.Clear();
                    foreach (var item in data) Categories.Add(item);
                    FilterCategories();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // TRAP THE ERROR: If Dapper fails, it will print here.
                Console.WriteLine($"CRITICAL ERROR in LoadCategoriesAsync: {ex.Message}");
            }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadTagsAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = (await _orchestrator.GetAllTagsWithHierarchyAsync(_cts.Token)).ToList();
                RunOnUIThread(() =>
                {
                    // 1. Hydrate Dynamic Multi-Select Dropdowns
                    AvailableTagCategories.Clear();
                    var distinctCategories = data
                        .Where(t => !string.IsNullOrEmpty(t.CategoryName))
                        .Select(t => t.CategoryName!)
                        .Distinct()
                        .OrderBy(c => c);

                    foreach (var cat in distinctCategories)
                    {
                        // Wrap in SelectableFilter and inject the FilterTags callback
                        AvailableTagCategories.Add(new SelectableFilter(cat, OnCategoryFilterToggled));
                    }

                    AvailableTagSubCategories.Clear();
                    var distinctSubCategories = data
                        .Where(t => !string.IsNullOrEmpty(t.SubCategoryName))
                        .Select(t => t.SubCategoryName!)
                        .Distinct()
                        .OrderBy(s => s);

                    foreach (var sub in distinctSubCategories)
                    {
                        // Wrap in SelectableFilter and inject the FilterTags callback
                        AvailableTagSubCategories.Add(new SelectableFilter(sub, FilterTags));
                    }

                    // 2. Hydrate the Grid
                    Tags.Clear();
                    foreach (var item in data) Tags.Add(item);
                    FilterTags();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadPayeesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllPayeesAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    Payees.Clear();
                    foreach (var item in data) Payees.Add(item);
                    FilterPayees();
                    MergePayeeCommand.NotifyCanExecuteChanged();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadAccountsAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = (await _orchestrator.GetAllAccountsAsync(_cts.Token)).ToList();
                RunOnUIThread(() =>
                {
                    // 1. Hydrate Dynamic Dropdowns
                    AvailableAccountEntities.Clear();
                    var distinctEntities = data
                        .Where(a => !string.IsNullOrEmpty(a.EntityName))
                        .Select(a => a.EntityName!)
                        .Distinct()
                        .OrderBy(e => e);

                    foreach (var entity in distinctEntities)
                    {
                        // Wrap in SelectableFilter and inject the Filter* callback
                        AvailableAccountEntities.Add(entity);
                    }

                    // Hydrate the grid
                    Accounts.Clear();
                    foreach (var item in data) Accounts.Add(item);
                    FilterAccounts();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadEntitiesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllEntitiesAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    Entities.Clear();
                    foreach (var item in data) Entities.Add(item);

                    FilterEntities();
                    MergeEntityCommand.NotifyCanExecuteChanged();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadSubCategoriesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllSubCategoriesWithHierarchyAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    // 1. Hydrate Dynamic Multi-Select Dropdowns
                    AvailableSubCatgCategory.Clear();
                    var distinctCategories = data
                        .Where(s => !string.IsNullOrEmpty(s.CategoryName))
                        .Select(s => s.CategoryName!)
                        .Distinct()
                        .OrderBy(c => c);

                    foreach (var cat in distinctCategories)
                    {
                        // Wrap in SelectableFilter and inject the Filter* callback
                        AvailableSubCatgCategory.Add(new SelectableFilter(cat, FilterSubCategories));
                    }

                    SubCategories.Clear();
                    foreach (var item in data) SubCategories.Add(item);
                    FilterSubCategories();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadTagRulesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = (await _orchestrator.GetAllTagRulesWithHierarchyAsync(_cts.Token)).ToList();
                RunOnUIThread(() =>
                {
                    // 1. Hydrate Dynamic Multi-Select Dropdowns
                    AvailableTagRuleTags.Clear();
                    var distinctTags = data
                        .Where(t => !string.IsNullOrEmpty(t.TagName))
                        .Select(t => t.TagName!)
                        .Distinct()
                        .OrderBy(c => c);

                    foreach (var tag in distinctTags)
                    {
                        // Wrap in SelectableFilter and inject the Filter* callback
                        AvailableTagRuleTags.Add(new SelectableFilter(tag, FilterTagRules));
                    }

                    // Hydrate the grid
                    TagRules.Clear();
                    foreach (var item in data) TagRules.Add(item);
                    FilterTagRules();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadSynonymsAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            Console.WriteLine("Loading synonyms from the orchestrator...");
            try
            {
                var data = await _orchestrator.GetAllSynonymsWithHierarchyAsync(_cts.Token);
                Console.WriteLine($"Loaded {data.Count()} synonyms from the orchestrator.");
                RunOnUIThread(() =>
                {
                    // 1. Hydrate Dynamic Multi-Select Dropdowns
                    AvailableSynonymCategory.Clear();
                    AvailableSynonymField.Clear();
                    var distinctCategoryTypes = data
                        .Where(s => !string.IsNullOrEmpty(s.CategoryName))
                        .Select(s => s.CategoryName!)
                        .Distinct()
                        .OrderBy(c => c);

                    var distinctFields = data
                        .Where(s => !string.IsNullOrEmpty(s.FieldType))
                        .Select(s => s.FieldType!)
                        .Distinct()
                        .OrderBy(f => f);

                    foreach (var cat in distinctCategoryTypes)
                    {
                        // Wrap in SelectableFilter and inject the Filter* callback
                        AvailableSynonymCategory.Add(cat);
                    }

                    foreach (var field in distinctFields)
                    {
                        AvailableSynonymField.Add(new SelectableFilter(field, FilterSynonyms));
                    }

                    Synonyms.Clear();
                    foreach (var item in data) Synonyms.Add(item);
                    FilterSynonyms();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // TRAP THE ERROR: If Dapper fails, it will print here.
                Console.WriteLine($"CRITICAL ERROR in LoadSynonymsAsync: {ex.Message}");
            }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadImportBatchesAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllImportBatchesAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    ImportBatches.Clear();
                    foreach (var item in data) ImportBatches.Add(item);
                    FilterImportBatches();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        private async Task LoadUserSettingsAsync(bool isSilent = false)
        {
            if (!isSilent) IsLoading = true;
            try
            {
                var data = await _orchestrator.GetAllSettingsAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    UserSettings.Clear();
                    foreach (var item in data)
                    {
                        Console.WriteLine($"Loaded UserSetting: Key={item.SettingKey}, Value={item.SettingValue}");
                        UserSettings.Add(item);
                    }
                    FilterUserSettings();
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        #endregion

        #region In-Memory UI Filters

        [RelayCommand]
        public void ClearCategoryFilters()
        {
            // Bypasses the database entirely. Setting IsSelected to false
            // automatically fires the FilterTags() callback you wired in the wrapper.
            foreach (var filter in AvailableTagCategories)
            {
                filter.IsSelected = false;
            }
        }

        [RelayCommand]
        public void ClearSubCategoryFilters()
        {
            foreach (var filter in AvailableTagSubCategories)
            {
                filter.IsSelected = false;
            }
        }

        [RelayCommand]
        public void ClearTagFilters()
        {
            foreach (var filter in AvailableTagRuleTags)
            {
                filter.IsSelected = false;
            }
        }

        [RelayCommand]
        public void ClearSubCatgFilters()
        {
            foreach (var filter in AvailableSubCatgCategory)
            {
                filter.IsSelected = false;
            }
        }

        [RelayCommand]
        public void ClearFieldTypeFilters()
        {
            foreach (var filter in AvailableSynonymField)
            {
                filter.IsSelected = false;
            }
        }

        private void FilterImportBatches()
        {
            RunOnUIThread(() =>
            {
                FilteredImportBatches.Clear();
                var search = ImportBatchSearchText?.Trim().ToLowerInvariant() ?? "";
                var query = string.IsNullOrEmpty(search) ? ImportBatches : ImportBatches.Where(b => b.FileName != null && b.FileName.ToLowerInvariant().Contains(search));
                foreach (var item in query) FilteredImportBatches.Add(item);
            });
        }

        private void FilterCategories()
        {
            RunOnUIThread(() =>
            {
                FilteredCategories.Clear();
                var search = CategorySearchText?.Trim().ToLowerInvariant() ?? "";
                var query = string.IsNullOrEmpty(search) ? Categories : Categories.Where(c => c.Name != null && c.Name.ToLowerInvariant().Contains(search));
                foreach (var item in query) FilteredCategories.Add(item);
            });
        }

        private void FilterSubCategories()
        {
            RunOnUIThread(() =>
            {
                FilteredSubCategories.Clear();
                var search = SubCategorySearchText?.Trim().ToLowerInvariant() ?? "";

                var query = string.IsNullOrEmpty(search) ? SubCategories : SubCategories.Where(s =>
                    (s.SubCategoryName != null && s.SubCategoryName.ToLowerInvariant().Contains(search)) ||
                    (s.CategoryName != null && s.CategoryName.ToLowerInvariant().Contains(search)));

                // 3. Strict Relational Column Filters (Multi-Select)
                var selectedCategories = AvailableSubCatgCategory
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                if (selectedCategories.Any())
                {
                    query = query.Where(s => s.CategoryName != null && selectedCategories.Contains(s.CategoryName));
                }

                foreach (var item in query) FilteredSubCategories.Add(item);
            });
        }

        private void FilterAccounts()
        {
            RunOnUIThread(() =>
            {
                FilteredAccounts.Clear();
                var query = Accounts.AsEnumerable();

                // 1. Apply Text Search
                if (!string.IsNullOrWhiteSpace(AccountSearchText))
                {
                    var search = AccountSearchText.Trim().ToLowerInvariant();
                    query = query.Where(a =>
                        (a.AccountNumber != null && a.AccountNumber.ToLowerInvariant().Contains(search)) ||
                        (a.CardNumber != null && a.CardNumber.ToLowerInvariant().Contains(search)) ||
                        (a.EntityName != null && a.EntityName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Dropdown Filter (Based on AccountType or CreditLimit)
                if (!string.IsNullOrEmpty(SelectedAccountFilter) && SelectedAccountFilter != "All")
                {
                    if (SelectedAccountFilter == "Credit Lines (Cards/Loans)")
                        query = query.Where(a => a.AccountType != null && (a.AccountType.Contains("Credit") || a.AccountType.Contains("Loan") || (decimal.TryParse(a.CreditLimit, out var creditLimit) && creditLimit > 0)));
                    else if (SelectedAccountFilter == "Bank Accounts (Cash/Checking)")
                        query = query.Where(a => a.AccountType != null && (a.AccountType.Contains("Checking") || a.AccountType.Contains("Savings") || a.AccountType.Contains("Cash")));
                }

                // 3. Strict Relational Column Filters
                if (!string.IsNullOrEmpty(SelectedAccountEntityFilter))
                {
                    query = query.Where(a => a.EntityName == SelectedAccountEntityFilter);
                }

                foreach (var item in query) FilteredAccounts.Add(item);
            });
        }

        private void FilterEntities()
        {
            RunOnUIThread(() =>
            {
                FilteredEntities.Clear();
                var search = EntitySearchText?.Trim().ToLowerInvariant() ?? "";
                var query = string.IsNullOrEmpty(search) ? Entities : Entities.Where(e => e.Name != null && e.Name.ToLowerInvariant().Contains(search));
                foreach (var item in query) FilteredEntities.Add(item);
            });
        }

        private void OnCategoryFilterToggled()
        {
            RunOnUIThread(() =>
            {
                // 1. Get all currently checked Categories
                var selectedCategories = AvailableTagCategories
                    .Where(c => c.IsSelected)
                    .Select(c => c.Name)
                    .ToList();

                // 2. Remember what Sub-Categories were already checked
                var previouslySelectedSubs = AvailableTagSubCategories
                    .Where(s => s.IsSelected)
                    .Select(s => s.Name)
                    .ToList();

                // 3. Clear the Sub-Category dropdown entirely
                AvailableTagSubCategories.Clear();

                // 4. Determine which Sub-Categories should be visible
                IEnumerable<string> validSubCatsQuery = Tags
                    .Where(t => !string.IsNullOrEmpty(t.SubCategoryName))
                    .Select(t => t.SubCategoryName!);

                // If ANY categories are checked, restrict the sub-categories to ONLY those parents
                if (selectedCategories.Any())
                {
                    validSubCatsQuery = Tags
                        .Where(t => t.CategoryName != null && selectedCategories.Contains(t.CategoryName) && !string.IsNullOrEmpty(t.SubCategoryName))
                        .Select(t => t.SubCategoryName!);
                }

                var newDistinctSubCats = validSubCatsQuery.Distinct().OrderBy(s => s);

                // 5. Repopulate the Sub-Category dropdown
                foreach (var subCat in newDistinctSubCats)
                {
                    // Note: The child filter only needs to trigger FilterTags(), not another cascade
                    var filter = new SelectableFilter(subCat, FilterTags);

                    // Re-apply the checkmark if it's still a valid option
                    if (previouslySelectedSubs.Contains(subCat))
                    {
                        filter.IsSelected = true;
                    }

                    AvailableTagSubCategories.Add(filter);
                }

                // 6. Finally, refresh the DataGrid
                FilterTags();
            });
        }

        private void FilterTagRules()
        {
            RunOnUIThread(() =>
            {
                FilteredTagRules.Clear();
                var query = TagRules.AsEnumerable();

                // 1. Apply Text Search
                if (!string.IsNullOrWhiteSpace(TagRuleSearchText))
                {
                    var search = TagRuleSearchText.Trim().ToLowerInvariant();
                    query = query.Where(r =>
                        (r.Keyword != null && r.Keyword.ToLowerInvariant().Contains(search)) ||
                        (r.TagName != null && r.TagName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Dropdown Filter (Assuming Priority >= 10 is High)
                if (!string.IsNullOrEmpty(SelectedTagRuleFilter) && SelectedTagRuleFilter != "All")
                {
                    if (SelectedTagRuleFilter == "High Priority")
                        query = query.Where(r => r.Priority >= 10);
                    else if (SelectedTagRuleFilter == "Standard Priority")
                        query = query.Where(r => r.Priority < 10);
                }

                // 3. Strict Relational Column Filters (Multi-Select)
                var selectedTags = AvailableTagRuleTags
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                if (selectedTags.Any())
                {
                    query = query.Where(t => t.TagName != null && selectedTags.Contains(t.TagName));
                }

                foreach (var item in query) FilteredTagRules.Add(item);
            });
        }

        partial void OnSelectedSynonymCategoryFilterChanged(string? value)
        {
            RunOnUIThread(() =>
            {
                // 1. Remember what was checked so we can re-check it if it still exists in the new category
                var previouslySelected = AvailableSynonymField
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                // 2. Clear the dropdown list entirely
                AvailableSynonymField.Clear();

                // 3. Determine which fields should be visible in the dropdown
                IEnumerable<string> validFieldsQuery = Synonyms
                    .Where(s => !string.IsNullOrEmpty(s.FieldType))
                    .Select(s => s.FieldType!);

                // If a specific category is selected, filter the dropdown options to ONLY that category
                if (!string.IsNullOrEmpty(value))
                {
                    validFieldsQuery = Synonyms
                        .Where(s => s.CategoryName == value && !string.IsNullOrEmpty(s.FieldType))
                        .Select(s => s.FieldType!);
                }

                var newDistinctFields = validFieldsQuery.Distinct().OrderBy(f => f);

                // 4. Repopulate the dropdown with only the valid fields
                foreach (var field in newDistinctFields)
                {
                    // Re-create the filter wrapper
                    var filter = new SelectableFilter(field, FilterSynonyms);

                    // If the user previously had this checked, keep it checked
                    if (previouslySelected.Contains(field))
                    {
                        filter.IsSelected = true;
                    }

                    AvailableSynonymField.Add(filter);
                }

                // 5. Finally, refresh the DataGrid
                FilterSynonyms();
            });
        }

        private void FilterSynonyms()
        {
            RunOnUIThread(() =>
            {
                FilteredSynonyms.Clear();
                var query = Synonyms.AsEnumerable();

                // 1. Apply Text Search
                if (!string.IsNullOrWhiteSpace(SynonymSearchText))
                {
                    var search = SynonymSearchText.Trim().ToLowerInvariant();
                    query = query.Where(s =>
                        (s.SynonymName != null && s.SynonymName.ToLowerInvariant().Contains(search)) ||
                        (s.CategoryName != null && s.CategoryName.ToLowerInvariant().Contains(search)) ||
                        (s.FieldType != null && s.FieldType.ToLowerInvariant().Contains(search)));
                }

                // 2. Strict Relational Column Filters (Multi-Select)

                if (!string.IsNullOrEmpty(SelectedSynonymCategoryFilter))
                {
                    query = query.Where(s => s.CategoryName == SelectedSynonymCategoryFilter);
                }

                // Check Fields (FieldType Filter)
                var selectedFieldTypes = AvailableSynonymField
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                if (selectedFieldTypes.Any())
                {
                    query = query.Where(t => t.FieldType != null && selectedFieldTypes.Contains(t.FieldType));
                }

                foreach (var item in query) FilteredSynonyms.Add(item);
            });
        }

        private void FilterUserSettings()
        {
            RunOnUIThread(() =>
            {
                FilteredUserSettings.Clear();
                var search = UserSettingSearchText?.Trim().ToLowerInvariant() ?? "";
                var query = string.IsNullOrEmpty(search) ? UserSettings : UserSettings.Where(u =>
                    (u.SettingKey != null && u.SettingKey.ToLowerInvariant().Contains(search)) ||
                    (u.SettingValue != null && u.SettingValue.ToLowerInvariant().Contains(search)));
                foreach (var item in query) FilteredUserSettings.Add(item);
            });
        }

        private void FilterPayees()
        {
            RunOnUIThread(() =>
            {
                FilteredPayees.Clear();
                var query = Payees.AsEnumerable();

                if (!string.IsNullOrWhiteSpace(PayeeSearchText))
                {
                    var search = PayeeSearchText.Trim().ToLowerInvariant();
                    query = query.Where(p => p.Name != null && p.Name.ToLowerInvariant().Contains(search));
                }

                foreach (var item in query)
                {
                    FilteredPayees.Add(item);
                }
            });
        }

        private void FilterTags()
        {
            RunOnUIThread(() =>
            {
                FilteredTags.Clear();
                var query = Tags.AsEnumerable();

                // 1. Apply Text Search (Fuzzy)
                if (!string.IsNullOrWhiteSpace(TagSearchText))
                {
                    var search = TagSearchText.Trim().ToLowerInvariant();
                    query = query.Where(t =>
                        (t.TagName != null && t.TagName.ToLowerInvariant().Contains(search)) ||
                        (t.SubCategoryName != null && t.SubCategoryName.ToLowerInvariant().Contains(search)) ||
                        (t.CategoryName != null && t.CategoryName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Standard Dropdown Filter (Binary: Categorized vs Misc)
                if (!string.IsNullOrEmpty(SelectedTagFilter) && SelectedTagFilter != "All")
                {
                    if (SelectedTagFilter == "Categorized")
                        query = query.Where(t => t.CategoryId.HasValue);
                    else if (SelectedTagFilter == "Uncategorized (Misc)")
                        query = query.Where(t => !t.CategoryId.HasValue);
                }

                // 3. Strict Relational Column Filters (Multi-Select)

                // Check Categories
                var selectedCategories = AvailableTagCategories
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                if (selectedCategories.Any())
                {
                    query = query.Where(t => t.CategoryName != null && selectedCategories.Contains(t.CategoryName));
                }

                // Check Sub-Categories
                var selectedSubCategories = AvailableTagSubCategories
                    .Where(f => f.IsSelected)
                    .Select(f => f.Name)
                    .ToList();

                if (selectedSubCategories.Any())
                {
                    query = query.Where(t => t.SubCategoryName != null && selectedSubCategories.Contains(t.SubCategoryName));
                }

                // 4. Push Results to UI
                foreach (var item in query) FilteredTags.Add(item);
            });
        }

        #endregion

        #region Row-Level Action Commands & Guardrails

        // --- CATEGORIES (Create & Update) ---
        [RelayCommand] public void OpenCreateCategory() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Category, FormMode.Create));
        [RelayCommand] public void UpdateCategory(Category category) { if (category != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Category, FormMode.Update, category.Id)); }

        // --- CATEGORIES ---
        [RelayCommand]
        public async Task DeleteCategoryAsync(Category category)
        {
            if (category == null) return;

            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage(
                "Safe Re-Parenting Warning",
                $"Deleting category '{category.Name}' will decouple child subcategories without deleting them. Proceed?",
                tcs));

            var confirmed = await tcs.Task;
            if (!confirmed) return;

            await _orchestrator.DeleteCategorySafeAsync(category.Id, _cts.Token);
            await LoadCategoriesAsync(isSilent: true);
        }

        // --- SUBCATEGORIES ---
        [RelayCommand] public void OpenCreateSubCategory() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.SubCategory, FormMode.Create));
        [RelayCommand] public void UpdateSubCategory(SubCategoryHierarchyDto sub) { if (sub != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.SubCategory, FormMode.Update, sub.SubCategoryId)); }

        [RelayCommand]
        public async Task DeleteSubCategoryAsync(SubCategoryHierarchyDto sub)
        {
            if (sub == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Safe Re-Parenting Warning", $"Deleting '{sub.SubCategoryName}' will unlink it from tags. Proceed?", tcs));
            if (await tcs.Task)
            {
                await _orchestrator.DeleteSubCategorySafeAsync(sub.SubCategoryId, _cts.Token);
                await LoadSubCategoriesAsync(isSilent: true);
            }
        }

        // --- ACCOUNTS ---
        [RelayCommand] public void OpenCreateAccount() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Account, FormMode.Create));
        [RelayCommand] public void UpdateAccount(Account account) { if (account != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Account, FormMode.Update, account.Id)); }

        [RelayCommand]
        public async Task DeleteAccountAsync(Account account)
        {
            if (account == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Delete Account", $"Are you sure you want to delete {account.AccountNumber}?", tcs));
            if (await tcs.Task)
            {
                try
                {
                    await _orchestrator.DeleteAccountAsync(account.Id, _cts.Token);
                    await LoadAccountsAsync(isSilent: true);
                }
                catch (InvalidOperationException ex) // Catch Hard Block
                {
                    _broker.Send(new ToastNotificationMessage(ToastType.Warning, ex.Message));
                }
            }
        }

        // --- ENTITIES (Institutions) ---
        [RelayCommand] public void OpenCreateEntity() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Entity, FormMode.Create));
        [RelayCommand] public void UpdateEntity(Entity entity) { if (entity != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Entity, FormMode.Update, entity.Id)); }

        private bool CanMergeEntity(Entity entity) => Entities.Count > 1;
        [RelayCommand(CanExecute = nameof(CanMergeEntity))] public void MergeEntity(Entity entity) { if (entity != null) _broker.Send(new ShowEntityMergeModalMessage(DomainEntity.Entity, entity.Id, entity.Name)); }

        [RelayCommand]
        public async Task DeleteEntityAsync(Entity entity)
        {
            if (entity == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Delete Institution", $"Are you sure you want to delete '{entity.Name}'?", tcs));
            if (await tcs.Task)
            {
                await _orchestrator.DeleteEntityAsync(entity.Id, _cts.Token);
                await LoadEntitiesAsync(isSilent: true);
            }
        }

        // --- TAG RULES ---
        [RelayCommand] public void OpenCreateTagRule() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.TagRule, FormMode.Create));
        [RelayCommand] public void UpdateTagRule(TagRuleHierarchyDto rule) { if (rule != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.TagRule, FormMode.Update, rule.TagRuleId)); }

        [RelayCommand]
        public async Task DeleteTagRuleAsync(TagRuleHierarchyDto rule)
        {
            if (rule == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Delete Rule", $"Delete rule for keyword '{rule.Keyword}'?", tcs));
            if (await tcs.Task)
            {
                await _orchestrator.DeleteTagRuleAsync(rule.TagRuleId, _cts.Token);
                await LoadTagRulesAsync(isSilent: true);
            }
        }

        // --- SYNONYMS ---
        [RelayCommand] public void OpenCreateSynonym() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Synonym, FormMode.Create));
        [RelayCommand] public void UpdateSynonym(SynonymsHierarchyDto synonym) { if (synonym != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Synonym, FormMode.Update, synonym.SynonymsId)); }

        [RelayCommand]
        public async Task DeleteSynonymAsync(SynonymsHierarchyDto synonym)
        {
            if (synonym == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Delete Synonym", $"Delete synonym '{synonym.SynonymName}'?", tcs));
            if (await tcs.Task)
            {
                await _orchestrator.DeleteSynonymAsync(synonym.SynonymsId, _cts.Token);
                await LoadSynonymsAsync(isSilent: true);
            }
        }

        // --- USER SETTINGS ---
        // Only update is provided since deleting core configuration keys can break the application[cite: 3].
        [RelayCommand]
        public void UpdateUserSetting(UserSetting setting)
        {
            if (setting != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.UserSetting, FormMode.Update, setting.SettingKey.GetHashCode())); // Treat HashCode as ID if no integer ID exists
        }

        // --- PAYEES ---
        [RelayCommand]
        public void OpenCreatePayee()
        {
            _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Payee, FormMode.Create));
        }

        [RelayCommand]
        public void UpdatePayee(Payee payee)
        {
            if (payee == null) return;
            _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Payee, FormMode.Update, payee.Id));
        }

        private bool CanMergePayee(Payee payee) => Payees.Count > 1;

        [RelayCommand(CanExecute = nameof(CanMergePayee))]
        public void MergePayee(Payee payee)
        {
            if (payee == null) return;
            _broker.Send(new ShowEntityMergeModalMessage(DomainEntity.Payee, payee.Id, payee.Name));
        }

        [RelayCommand]
        public async Task DeletePayeeAsync(Payee payee)
        {
            if (payee == null) return;

            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage(
                "Delete Payee",
                $"Are you sure you want to delete '{payee.Name}'? If historical transactions are linked, this will be strictly blocked to protect AIS tax records.",
                tcs));

            var confirmed = await tcs.Task;
            if (!confirmed) return;

            try
            {
                await _orchestrator.DeletePayeeAsync(payee.Id, _cts.Token);
                await LoadPayeesAsync(isSilent: true);
            }
            catch (InvalidOperationException ex)
            {
                _broker.Send(new ToastNotificationMessage(ex.Message, NotificationType.Warning));
            }
        }

        // --- TAGS ---
        [RelayCommand]
        public void OpenCreateTag()
        {
            _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Tag, FormMode.Create));
        }

        [RelayCommand]
        public void UpdateTag(TagHierarchyDto tag)
        {
            if (tag == null) return;
            _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Tag, FormMode.Update, tag.TagId));
        }

        [RelayCommand]
        public async Task DeleteTagAsync(TagHierarchyDto tag)
        {
            if (tag == null) return;

            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage(
                "Safe Re-Parenting Warning",
                $"Deleting tag '{tag.TagName}' will safely re-parent all historical transactions to the system 'Misc Tag'. Associated tag rules will be removed. Proceed?",
                tcs));

            var confirmed = await tcs.Task;
            if (!confirmed) return;

            await _orchestrator.DeleteTagSafeAsync(tag.TagId, _cts.Token);
            await LoadTagsAsync(isSilent: true);
        }

        // --- AUDIT LOGS (IMPORT BATCHES) ---
        [RelayCommand]
        public async Task RevertBatchAsync(ImportBatch batch)
        {
            if (batch == null) return;

            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage(
                "CRITICAL: Revert Import Batch",
                $"Reverting batch #{batch.Id} will PERMANENTLY delete all extracted transactions imported in this batch and wipe the audit log. This cannot be undone. Are you certain?",
                tcs));

            var confirmed = await tcs.Task;
            if (!confirmed) return;

            // RevertImportBatch is executed via orchestrator token
            await _transactionOrchestrator.RevertImportBatchAsync(batch.Id, _cts.Token);
            await LoadImportBatchesAsync(isSilent: true);
        }

        #endregion

        #region Reactive Broker Synchronization

        private async void OnEntityChanged(object message)
        {
            DomainEntity entityType = message switch
            {
                EntitySavedMessage saved => saved.EntityType,
                EntityUpdatedMessage updated => updated.EntityType,
                EntityDeletedMessage deleted => deleted.EntityType,
                _ => throw new InvalidOperationException("Unknown domain message.")
            };

            switch (entityType)
            {
                case DomainEntity.Category:
                    await LoadCategoriesAsync(isSilent: true);
                    await LoadSubCategoriesAsync(isSilent: true); // SubCategories rely on Categories
                    await LoadTagsAsync(isSilent: true);          // Tags rely on Categories
                    break;
                case DomainEntity.Tag:
                    await LoadTagsAsync(isSilent: true);
                    await LoadTagRulesAsync(isSilent: true);      // Tag Rules rely on Tags
                    break;
                case DomainEntity.Payee:
                    await LoadPayeesAsync(isSilent: true);
                    break;
                case DomainEntity.ImportBatch:
                    await LoadImportBatchesAsync(isSilent: true);
                    break;
                case DomainEntity.Account:
                    await LoadAccountsAsync(isSilent: true);
                    await LoadAccountsAsync(isSilent: true);      // Accounts rely on Entities
                    break;
                case DomainEntity.SubCategory:
                    await LoadSubCategoriesAsync(isSilent: true);
                    await LoadTagsAsync(isSilent: true);          // Tags rely on SubCategories
                    break;
                case DomainEntity.TagRule:
                    await LoadTagRulesAsync(isSilent: true);
                    break;
                case DomainEntity.UserSetting:
                    await LoadUserSettingsAsync(isSilent: true);
                    break;
                case DomainEntity.Synonym:
                    await LoadSynonymsAsync(isSilent: true);
                    break;
                case DomainEntity.Entity:
                    await LoadEntitiesAsync(isSilent: true);
                    break;
            }
        }

        #endregion

        public override void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            base.Dispose();
        }
    }
}