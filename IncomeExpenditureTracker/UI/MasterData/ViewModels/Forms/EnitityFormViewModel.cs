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
    public partial class EntityFormViewModel : FormViewModelBase
    {
        private string _name = string.Empty;
        private string _country = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    // Utilize the base class debounce engine
                    ValidateUniqueAsync(DomainEntity.Entity, "Name", value, "This Entity name already exists.");

                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public string Country
        {
            get => _country;
            set
            {
                if (SetProperty(ref _country, value))
                {
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Pass dependencies to base
        public EntityFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? entityId = null)
            : base(orchestrator, broker, entityId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                if (IsUpdateMode)
                {
                    // Fetch the existing record
                    var allEntities = await Orchestrator.GetAllEntitiesAsync(ViewCts.Token);
                    var existing = allEntities.FirstOrDefault(e => e.Id == ExcludeId);

                    if (existing != null)
                    {
                        // Set the backing field directly to bypass the debounced uniqueness check on load
                        _name = existing.Name ?? "";
                        OnPropertyChanged(nameof(Name));

                        _country = existing.Country ?? "";
                        OnPropertyChanged(nameof(Country));
                    }
                }
            }
            catch (OperationCanceledException) { /* Silently abort if user closes modal instantly */ }
        }

        protected override bool CanSave() => !HasErrors && !string.IsNullOrWhiteSpace(Name);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            var entity = new Entity { Id = ExcludeId ?? 0, Name = Name.Trim(), Country = Country.Trim() };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateEntityAsync(entity, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Entity, entity.Id));
            }
            else
            {
                await Orchestrator.GetOrCreateEntityAsync(entity.Name, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Entity));
            }
        }
    }
}