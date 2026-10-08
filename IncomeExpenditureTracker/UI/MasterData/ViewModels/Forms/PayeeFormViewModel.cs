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
    public partial class PayeeFormViewModel : FormViewModelBase
    {
        private string _name = string.Empty;
        private bool _defaultIncomeSource = false;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    // Utilize the base class debounce engine
                    ValidateUniqueAsync(DomainEntity.Payee, "Name", value, "This Payee name already exists.");
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public bool DefaultIncomeSource
        {
            get => _defaultIncomeSource;
            set
            {
                if (SetProperty(ref _defaultIncomeSource, value))
                {
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        // Pass dependencies to base
        public PayeeFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? payeeId = null)
            : base(orchestrator, broker, payeeId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                if (IsUpdateMode)
                {
                    // Fetch the existing record
                    var allPayees = await Orchestrator.GetAllPayeesAsync(ViewCts.Token);
                    var existing = allPayees.FirstOrDefault(p => p.Id == ExcludeId);

                    if (existing != null)
                    {
                        // Set the backing field directly to bypass the debounced uniqueness check on load
                        _name = existing.Name ?? "";
                        OnPropertyChanged(nameof(Name));

                        DefaultIncomeSource = existing.IsDefaultIncomeSource;
                    }
                }
            }
            catch (OperationCanceledException) { /* Silently abort if user closes modal instantly */ }
        }

        protected override bool CanSave() => !HasErrors && !string.IsNullOrWhiteSpace(Name);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            var payee = new Payee { Id = ExcludeId ?? 0, Name = Name.Trim(), IsDefaultIncomeSource = DefaultIncomeSource };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdatePayeeAsync(payee, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Payee, payee.Id));
            }
            else
            {
                await Orchestrator.CreatePayeeAsync(payee, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Payee));
            }
        }
    }
}