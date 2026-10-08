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
    public partial class UserSettingFormViewModel : FormViewModelBase
    {
        [ObservableProperty] private string _settingKey = string.Empty;
        private string _settingValue = string.Empty;

        public string SettingValue
        {
            get => _settingValue;
            set
            {
                if (SetProperty(ref _settingValue, value))
                {
                    // Tell the UI to re-evaluate the Save button state on every keystroke
                    SaveCommand.NotifyCanExecuteChanged(); // Not using the Base engine, Value doesn't require uniqueness check
                }
            }
        }

        private int? _settingKeyHash;

        // Pass dependencies to base
        public UserSettingFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? settingKeyHash = null)
            : base(orchestrator, broker, null)
        {
            _settingKeyHash = settingKeyHash;
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {

                // Fetch the existing record
                var allSettings = await Orchestrator.GetAllSettingsAsync(ViewCts.Token);
                var existing = _settingKeyHash.HasValue
                    ? allSettings.FirstOrDefault(c => c.SettingKey != null && c.SettingKey.GetHashCode() == _settingKeyHash.Value)
                    : null;

                if (existing != null)
                {
                    SettingKey = existing.SettingKey;
                    // Set the backing field directly to bypass the debounced uniqueness check on load
                    _settingValue = existing.SettingValue ?? "";
                    OnPropertyChanged(nameof(SettingValue));
                }

            }
            catch (OperationCanceledException) { /* Silently abort if user closes modal instantly */ }
        }

        protected override bool CanSave() => !HasErrors && !string.IsNullOrWhiteSpace(SettingValue);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            await Orchestrator.SetUserSettingAsync(SettingKey, SettingValue.Trim(), ct);
            Broker.Send(new EntityUpdatedMessage(DomainEntity.UserSetting, null, SettingKey));

        }
    }
}