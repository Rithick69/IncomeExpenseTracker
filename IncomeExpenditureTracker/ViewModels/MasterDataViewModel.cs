using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.ViewModels
{
    public partial class MasterDataViewModel : ViewModelBase
    {
        private readonly IMasterDataOrchestrator _orchestrator;

        // =========================================================================
        // OBSERVABLE PROPERTIES
        // =========================================================================

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "Ready";

        // Observable collections automatically update the Avalonia UI Lists/DataGrids when modified
        public ObservableCollection<Category> Categories { get; } = new();
        public ObservableCollection<SubCategory> SubCategories { get; } = new();
        public ObservableCollection<Tag> Tags { get; } = new();
        public ObservableCollection<RuleBookSnapshot> TagRules { get; } = new();
        public ObservableCollection<Entity> Entities { get; } = new();
        public ObservableCollection<Account> Accounts { get; } = new();
        public ObservableCollection<Synonyms> Synonyms { get; } = new();
        public ObservableCollection<ImportBatch> ImportBatches { get; } = new();

        // =========================================================================
        // CONSTRUCTOR
        // =========================================================================
        public MasterDataViewModel(
            IMasterDataOrchestrator orchestrator,
            IApplicationBroker broker)
            : base(broker)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

            // The Waiter listens for Kitchen announcements!
            Broker.Register<EntitySavedMessage>(this, OnEntitySaved);
            Broker.Register<EntityUpdatedMessage>(this, OnEntityUpdated);
            Broker.Register<EntityDeletedMessage>(this, OnEntityDeleted);
            Broker.Register<CrudErrorMessage>(this, OnCrudError);
        }

        // =========================================================================
        // COMMANDS
        // =========================================================================

        [RelayCommand]
        public async Task LoadDataAsync()
        {
            IsLoading = true;
            StatusText = "Loading Master Data...";

            try
            {
                // Fetch all data from SQLite via the Orchestrator
                var categories = await _orchestrator.GetAllCategoriesAsync();
                var subCategories = await _orchestrator.GetAllSubCategoriesAsync();
                var tags = await _orchestrator.GetAllTagsAsync();
                var entities = await _orchestrator.GetAllEntitiesAsync();
                var accounts = await _orchestrator.GetAllAccountsAsync();
                var synonyms = await _orchestrator.GetAllSynonymsAsync();
                var importBatches = await _orchestrator.GetAllImportBatchesAsync();
                var tagRules = await _orchestrator.GetRuleBookSnapshotAsync();

                // Clear and repopulate collections safely
                Categories.Clear();
                foreach (var c in categories) Categories.Add(c);

                SubCategories.Clear();
                foreach (var sc in subCategories) SubCategories.Add(sc);

                Tags.Clear();
                foreach (var t in tags) Tags.Add(t);

                Entities.Clear();
                foreach (var e in entities) Entities.Add(e);

                Accounts.Clear();
                foreach (var a in accounts) Accounts.Add(a);

                Synonyms.Clear();
                foreach (var s in synonyms) Synonyms.Add(s);

                ImportBatches.Clear();
                foreach (var i in importBatches) ImportBatches.Add(i);

                TagRules.Add(tagRules);

                StatusText = "Data loaded successfully.";
            }
            catch (Exception)
            {
                // We don't need to log here; the backend services are already logging errors!
                StatusText = "Failed to load data. Check system logs.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // =========================================================================
        // EVENT HANDLERS (Reactive UI Updates)
        // =========================================================================

        private void OnEntitySaved(EntitySavedMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"✅ {message.EntityType} '{message.Name}' was created!";

                // If a Category or Tag was saved, auto-refresh the grids so the new data appears instantly!
                if (message.EntityType == "Category" || message.EntityType == "Tag")
                {
                    _ = LoadDataAsync();
                }
            });
        }

        private void OnEntityUpdated(EntityUpdatedMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"✅ {message.EntityType} '{message.Name}' was updated!";
                if (message.EntityType == "Category" || message.EntityType == "Tag") _ = LoadDataAsync();
            });
        }

        private void OnEntityDeleted(EntityDeletedMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"🗑️ {message.EntityType} '{message.Name}' was deleted.";
                if (message.EntityType == "Category" || message.EntityType == "Tag") _ = LoadDataAsync();
            });
        }

        private void OnCrudError(CrudErrorMessage message)
        {
            Dispatcher.UIThread.Post(() =>
            {
                StatusText = $"❌ Failed to {message.Operation} {message.EntityType}: {message.UserFriendlyMessage}";
            });
        }
    }
}