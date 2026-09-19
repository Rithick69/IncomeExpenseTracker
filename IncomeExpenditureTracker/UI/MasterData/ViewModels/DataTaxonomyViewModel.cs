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

        // --- SYNONYMS ---
        [ObservableProperty] private string? _selectedSynonymFilter;
        public ObservableCollection<string> SynonymFilterOptions { get; } = new() { "All", "Mapped to Category", "Unmapped" };
        partial void OnSelectedSynonymFilterChanged(string? value) => FilterSynonyms();

        // --- TAG FILTERS (Category -> SubCategory) ---
        [ObservableProperty] private string? _selectedTagCategoryFilter;
        [ObservableProperty] private string? _selectedTagSubCategoryFilter;

        public ObservableCollection<string> AvailableTagCategories { get; } = new() { "All Categories" };
        public ObservableCollection<string> AvailableTagSubCategories { get; } = new() { "All Sub-Categories" };

        partial void OnSelectedTagCategoryFilterChanged(string? value) => FilterTags();
        partial void OnSelectedTagSubCategoryFilterChanged(string? value) => FilterTags();

        // --- ACCOUNT FILTERS (Entity -> Account) ---
        [ObservableProperty] private string? _selectedAccountEntityFilter;

        public ObservableCollection<string> AvailableAccountEntities { get; } = new() { "All" };

        partial void OnSelectedAccountEntityFilterChanged(string? value) => FilterAccounts();

        // --- TAG RULE FILTERS (Tag -> Rule) ---
        [ObservableProperty] private string? _selectedTagRuleTagFilter;

        public ObservableCollection<string> AvailableTagRuleTags { get; } = new() { "All Tags" };

        // --- SYNONYM FILTERS(CategoryType -> Synonym)
        [ObservableProperty] private string? _selectedSynonymCategoryFilter;

        public ObservableCollection<string> AvailableSynonymCategory { get; } = new() { "All CategoryTypes" };

        // --- SUBCATEGORY FILTERS(Category -> SubCategory)
        [ObservableProperty] private string? _selectedSubCategoryCategoryFilter;

        public ObservableCollection<string> AvailableSubCategoryCategory { get; } = new() { "All Categories" };

        partial void OnSelectedTagRuleTagFilterChanged(string? value) => FilterTagRules();

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

        partial void OnCategorySearchTextChanged(string value) => FilterCategories();
        partial void OnSubCategorySearchTextChanged(string value) => FilterSubCategories();
        partial void OnAccountSearchTextChanged(string value) => FilterAccounts();
        partial void OnEntitySearchTextChanged(string value) => FilterEntities();
        partial void OnTagRuleSearchTextChanged(string value) => FilterTagRules();
        partial void OnSynonymSearchTextChanged(string value) => FilterSynonyms();
        partial void OnUserSettingSearchTextChanged(string value) => FilterUserSettings();
        partial void OnPayeeSearchTextChanged(string value) => FilterPayees();
        partial void OnTagSearchTextChanged(string value) => FilterTags();

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
                RunOnUIThread(() =>
                {
                    Categories.Clear();
                    foreach (var item in data) Categories.Add(item);
                });
            }
            catch (OperationCanceledException) { }
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
                    // Hydrate the dynamic dropdowns
                    AvailableTagCategories.Clear();
                    AvailableTagCategories.Add("All Categories");
                    foreach (var cat in data.Where(t => !string.IsNullOrEmpty(t.CategoryName)).Select(t => t.CategoryName!).Distinct().OrderBy(c => c))
                        AvailableTagCategories.Add(cat);

                    AvailableTagSubCategories.Clear();
                    AvailableTagSubCategories.Add("All Sub-Categories");
                    foreach (var sub in data.Where(t => !string.IsNullOrEmpty(t.SubCategoryName)).Select(t => t.SubCategoryName!).Distinct().OrderBy(s => s))
                        AvailableTagSubCategories.Add(sub);

                    // Hydrate the grid
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
                    // Hydrate the dynamic dropdowns
                    AvailableAccountEntities.Clear();
                    AvailableAccountEntities.Add("All");
                    foreach (var entity in data.Where(a => !string.IsNullOrEmpty(a.EntityName)).Select(a => a.EntityName!).Distinct().OrderBy(e => e))
                        AvailableAccountEntities.Add(entity);

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
                    AvailableSubCategoryCategory.Clear();
                    AvailableSubCategoryCategory.Add("All Categories");
                    foreach (var category in data.Where(a => !string.IsNullOrEmpty(a.CategoryName)).Select(a => a.CategoryName!).Distinct().OrderBy(e => e))
                        AvailableAccountEntities.Add(category);

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
                    // Hydrate the dynamic dropdowns
                    AvailableTagRuleTags.Clear();
                    AvailableTagRuleTags.Add("All Tags");
                    foreach (var tag in data.Where(r => !string.IsNullOrEmpty(r.TagName)).Select(r => r.TagName!).Distinct().OrderBy(t => t))
                        AvailableTagRuleTags.Add(tag);

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
            try
            {
                var data = await _orchestrator.GetAllSynonymsWithHierarchyAsync(_cts.Token);
                RunOnUIThread(() =>
                {
                    AvailableSynonymCategory.Clear();
                    AvailableSynonymCategory.Add("All CategoryTypes");

                    foreach (var category in data.Where(r => !string.IsNullOrEmpty(r.CategoryName)).Select(r => r.CategoryName!).Distinct().OrderBy(t => t))
                        AvailableTagRuleTags.Add(category);

                    Synonyms.Clear();
                    foreach (var item in data) Synonyms.Add(item);
                    FilterSynonyms();
                });
            }
            catch (OperationCanceledException) { }
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
                    foreach (var item in data) UserSettings.Add(item);
                });
            }
            catch (OperationCanceledException) { }
            finally { if (!isSilent) IsLoading = false; }
        }

        #endregion

        #region In-Memory UI Filters

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
                        (a.EntityName != null && a.EntityName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Dropdown Filter (Based on AccountType or CreditLimit)
                if (!string.IsNullOrEmpty(SelectedAccountFilter) && SelectedAccountFilter != "All")
                {
                    if (SelectedAccountFilter == "Credit Lines (Cards/Loans)")
                        query = query.Where(a => a.AccountType != null && (a.AccountType.Contains("Credit") || a.AccountType.Contains("Loan") || a.CreditLimit > 0));
                    else if (SelectedAccountFilter == "Bank Accounts (Cash/Checking)")
                        query = query.Where(a => a.AccountType != null && (a.AccountType.Contains("Checking") || a.AccountType.Contains("Savings") || a.AccountType.Contains("Cash")));
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

                foreach (var item in query) FilteredTagRules.Add(item);
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
                        (s.SynonymsName != null && s.SynonymsName.ToLowerInvariant().Contains(search)) ||
                        (s.CategoryName != null && s.CategoryName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Dropdown Filter
                if (!string.IsNullOrEmpty(SelectedSynonymFilter) && SelectedSynonymFilter != "All")
                {
                    if (SelectedSynonymFilter == "Mapped to Category")
                        query = query.Where(s => !string.IsNullOrEmpty(s.CategoryName));
                    else if (SelectedSynonymFilter == "Unmapped")
                        query = query.Where(s => string.IsNullOrEmpty(s.CategoryName));
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

                // 1. Apply Text Search
                if (!string.IsNullOrWhiteSpace(TagSearchText))
                {
                    var search = TagSearchText.Trim().ToLowerInvariant();
                    query = query.Where(t =>
                        (t.TagName != null && t.TagName.ToLowerInvariant().Contains(search)) ||
                        (t.SubCategoryName != null && t.SubCategoryName.ToLowerInvariant().Contains(search)) ||
                        (t.CategoryName != null && t.CategoryName.ToLowerInvariant().Contains(search)));
                }

                // 2. Apply Dropdown Filter
                if (!string.IsNullOrEmpty(SelectedTagFilter) && SelectedTagFilter != "All")
                {
                    if (SelectedTagFilter == "Categorized")
                        query = query.Where(t => t.CategoryId.HasValue);
                    else if (SelectedTagFilter == "Uncategorized (Misc)")
                        query = query.Where(t => !t.CategoryId.HasValue);
                }

                foreach (var item in query) FilteredTags.Add(item);
            });
        }

        #endregion

        #region Row-Level Action Commands & Guardrails

        // --- CATEGORIES (Create & Update) ---
        [RelayCommand] public void OpenCreateCategory() => _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Category, FormMode.Create));
        [RelayCommand] public void UpdateCategory(Category category) { if (category != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Category, FormMode.Update, category.CategoryId)); }

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
        [RelayCommand] public void UpdateAccount(Account account) { if (account != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Account, FormMode.Update, account.AccountId)); }

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
                    await _orchestrator.DeleteAccountAsync(account.AccountId, _cts.Token);
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
        [RelayCommand] public void UpdateEntity(Entity entity) { if (entity != null) _broker.Send(new ShowEntityFormModalMessage(DomainEntity.Entity, FormMode.Update, entity.EntityId)); }
        [RelayCommand] public void MergeEntity(Entity entity) { if (entity != null) _broker.Send(new ShowEntityMergeModalMessage(DomainEntity.Entity, entity.EntityId, entity.Name)); }

        [RelayCommand]
        public async Task DeleteEntityAsync(Entity entity)
        {
            if (entity == null) return;
            var tcs = new TaskCompletionSource<bool>();
            _broker.Send(new ShowConfirmationMessage("Delete Institution", $"Are you sure you want to delete '{entity.Name}'?", tcs));
            if (await tcs.Task)
            {
                await _orchestrator.DeleteEntityAsync(entity.EntityId, _cts.Token);
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
            _broker.Send(new ShowConfirmationMessage("Delete Synonym", $"Delete synonym '{synonym.SynonymsName}'?", tcs));
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

        [RelayCommand]
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
                    break;
                case DomainEntity.Tag:
                    await LoadTagsAsync(isSilent: true);
                    break;
                case DomainEntity.Payee:
                    await LoadPayeesAsync(isSilent: true);
                    break;
                case DomainEntity.ImportBatch:
                    await LoadImportBatchesAsync(isSilent: true);
                    break;
                case DomainEntity.Account:
                    await LoadAccountsAsync(isSilent: true);
                    break;
                case DomainEntity.SubCategory:
                    await LoadSubCategoriesAsync(isSilent: true);
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