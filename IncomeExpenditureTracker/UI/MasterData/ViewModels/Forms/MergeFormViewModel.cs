using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Collections.Generic;
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
    public partial class MergeFormViewModel : ViewModelBase
    {
        private readonly IMasterDataOrchestrator _orchestrator;
        private readonly IApplicationBroker _broker;
        private readonly CancellationTokenSource _viewCts = new();

        public DomainEntity EntityType { get; }
        public int SourceId { get; }
        public string SourceName { get; }

        public string ModalTitle => $"Merge {EntityType}";
        public string HelperText => $"Select the target {EntityType} that '{SourceName}' should be merged into. All historical transactions will be safely re-mapped. This action cannot be undone.";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ExecuteMergeCommand))]
        private string _targetEntityName = string.Empty;

        // Stores a mapping of Name -> ID to resolve the AutoComplete string back to a database ID
        private readonly Dictionary<string, int> _targetMap = new(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<string> AvailableTargets { get; } = new();

        public MergeFormViewModel(
            IMasterDataOrchestrator orchestrator,
            IApplicationBroker broker,
            DomainEntity entityType,
            int sourceId,
            string sourceName) : base(broker)
        {
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _broker = broker ?? throw new ArgumentNullException(nameof(broker));

            EntityType = entityType;
            SourceId = sourceId;
            SourceName = sourceName;
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                _targetMap.Clear();
                AvailableTargets.Clear();

                if (EntityType == DomainEntity.Payee)
                {
                    var payees = await _orchestrator.GetAllPayeesAsync(_viewCts.Token);
                    foreach (var p in payees.Where(p => p.Id != SourceId && !string.IsNullOrWhiteSpace(p.Name)))
                    {
                        _targetMap[p.Name!] = p.Id;
                        AvailableTargets.Add(p.Name!);
                    }
                }
                else if (EntityType == DomainEntity.Entity)
                {
                    var entities = await _orchestrator.GetAllEntitiesAsync(_viewCts.Token);
                    foreach (var e in entities.Where(e => e.Id != SourceId && !string.IsNullOrWhiteSpace(e.Name)))
                    {
                        _targetMap[e.Name!] = e.Id;
                        AvailableTargets.Add(e.Name!);
                    }
                }
            }
            catch (OperationCanceledException) { /* Silently abort */ }
        }

        private bool CanMerge() =>
            !string.IsNullOrWhiteSpace(TargetEntityName) &&
            _targetMap.ContainsKey(TargetEntityName.Trim());

        [RelayCommand(CanExecute = nameof(CanMerge))]
        public async Task ExecuteMergeAsync()
        {
            _broker.Send(new ToggleLoadingMessage(true));

            try
            {
                var targetName = TargetEntityName.Trim();
                if (!_targetMap.TryGetValue(targetName, out int targetId))
                {
                    throw new InvalidOperationException("Invalid target selected.");
                }

                if (EntityType == DomainEntity.Payee)
                {
                    // Orchestrator expects a list of source IDs for Payees
                    await _orchestrator.MergePayeesAsync(targetId, new List<int> { SourceId }, _viewCts.Token);
                }
                else if (EntityType == DomainEntity.Entity)
                {
                    // Orchestrator expects int IDs for Entities
                    await _orchestrator.MergeEntitiesAsync(SourceId, targetId, _viewCts.Token);
                }

                // Broadcast updates so the grids refresh in the background
                _broker.Send(new EntityUpdatedMessage(EntityType, targetId));
                _broker.Send(new EntityDeletedMessage(EntityType, SourceName));

                _broker.Send(new CloseModalMessage());
            }
            catch (Exception ex)
            {
                _broker.Send(new ToastNotificationMessage(ToastType.Error, $"Merge failed: {ex.Message}"));
            }
            finally
            {
                _broker.Send(new ToggleLoadingMessage(false));
            }
        }

        [RelayCommand]
        public void Cancel() => _broker.Send(new CloseModalMessage());

        public override void Dispose()
        {
            _viewCts.Cancel();
            _viewCts.Dispose();
            base.Dispose();
        }
    }
}