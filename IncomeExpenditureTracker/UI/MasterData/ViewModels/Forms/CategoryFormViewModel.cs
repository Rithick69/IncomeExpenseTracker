using System;
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
    public partial class CategoryFormViewModel : FormViewModelBase
    {
        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    // Utilize the base class debounce engine
                    ValidateUniqueAsync(DomainEntity.Category, "Name", value, "This Category name already exists.");

                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Pass dependencies to base
        public CategoryFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? categoryId = null)
            : base(orchestrator, broker, categoryId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                if (IsUpdateMode)
                {
                    // Fetch the existing record
                    var allCategories = await Orchestrator.GetAllCategoriesAsync(ViewCts.Token);
                    var existing = allCategories.FirstOrDefault(c => c.Id == ExcludeId);

                    if (existing != null)
                    {
                        // Set the backing field directly to bypass the debounced uniqueness check on load
                        _name = existing.Name ?? "";
                        OnPropertyChanged(nameof(Name));
                    }
                }
            }
            catch (OperationCanceledException) { /* Silently abort if user closes modal instantly */ }
        }

        protected override bool CanSave() => !HasErrors && !string.IsNullOrWhiteSpace(Name);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            var category = new Category { Id = ExcludeId ?? 0, Name = Name.Trim() };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateCategoryAsync(category, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Category, category.Id));
            }
            else
            {
                await Orchestrator.GetOrCreateCategoryAsync(category.Name, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Category));
            }
        }
    }
}