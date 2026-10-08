using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Orchestration;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.MasterData
{
    public abstract partial class FormViewModelBase : ViewModelBase, INotifyDataErrorInfo
    {
        protected readonly IMasterDataOrchestrator Orchestrator;
        protected readonly IApplicationBroker _broker;
        protected readonly CancellationTokenSource ViewCts = new();
        private CancellationTokenSource? _debounceCts;

        public bool IsUpdateMode { get; }
        protected int? ExcludeId { get; }

        protected FormViewModelBase(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? entityId) : base(broker)
        {
            Orchestrator = orchestrator;
            _broker = broker;
            ExcludeId = entityId;
            IsUpdateMode = entityId.HasValue;
        }

        // --- ABSTRACT METHODS FOR CHILD CLASSES ---
        protected abstract Task ExecuteSaveAsync(CancellationToken ct);
        protected abstract bool CanSave();

        // --- DEBOUNCED UNIQUE VALIDATION ENGINE ---
        private readonly Dictionary<string, List<string>> _errors = new();
        public bool HasErrors => _errors.Any();
        public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

        protected async void ValidateUniqueAsync(DomainEntity entityType, string propertyName, string value, string errorMessage)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            ClearError(propertyName);

            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(propertyName, $"{propertyName} is required.");
                return;
            }

            try
            {
                await Task.Delay(300, token); // Debounce
                bool isUnique = await Orchestrator.IsUniqueAsync(entityType, propertyName, value, ExcludeId, token);

                if (!isUnique) AddError(propertyName, errorMessage);
            }
            catch (TaskCanceledException) { /* Ignored */ }
        }

        protected async void ValidateUniqueCompositeAsync(DomainEntity entityType, Dictionary<string, object> properties, string errorPropertyName, string errorMessage)
        {
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            ClearError(errorPropertyName);

            try
            {
                await Task.Delay(300, token); // Debounce

                // Calls the overload: IsUniqueAsync(DomainEntity, Dictionary<string, object>, int?, CancellationToken)
                bool isUnique = await Orchestrator.IsUniqueAsync(entityType, properties, ExcludeId, token);

                if (!isUnique) AddError(errorPropertyName, errorMessage);
            }
            catch (TaskCanceledException) { /* Ignored if typing continues */ }
        }

        protected void AddError(string propertyName, string error)
        {
            _errors[propertyName] = new List<string> { error };
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
            SaveCommand.NotifyCanExecuteChanged();
        }

        protected void ClearError(string propertyName)
        {
            if (_errors.Remove(propertyName))
            {
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
                SaveCommand.NotifyCanExecuteChanged();
            }
        }

        public IEnumerable GetErrors(string? propertyName) => propertyName != null && _errors.TryGetValue(propertyName, out var errors) ? errors : Enumerable.Empty<string>();

        // --- STANDARDIZED MODAL LIFECYCLE ---
        [RelayCommand(CanExecute = nameof(CanSave))]
        public async Task SaveAsync()
        {
            _broker.Send(new ToggleLoadingMessage(true)); // Lock UI
            try
            {
                await ExecuteSaveAsync(ViewCts.Token);
                _broker.Send(new CloseModalMessage()); // Close on success
            }
            catch (Exception ex)
            {
                _broker.Send(new ToastNotificationMessage(ToastType.Error, $"Save failed: {ex.Message}"));
            }
            finally
            {
                _broker.Send(new ToggleLoadingMessage(false)); // Unlock UI
            }
        }

        [RelayCommand]
        public void Cancel() => _broker.Send(new CloseModalMessage());

        public override void Dispose()
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            ViewCts.Cancel();
            ViewCts.Dispose();
            base.Dispose();
        }
    }
}