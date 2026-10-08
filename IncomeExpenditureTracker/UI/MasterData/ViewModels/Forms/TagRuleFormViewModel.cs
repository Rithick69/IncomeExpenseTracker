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
    public partial class TagRuleFormViewModel : FormViewModelBase
    {
        public ObservableCollection<string> AvailableTags { get; } = new();

        // Cache the full tag DTOs to instantly resolve TagName to TagId in memory
        private List<TagHierarchyDto> _allTags = new();

        private string _tagName = string.Empty;
        public string TagName
        {
            get => _tagName;
            set
            {
                if (SetProperty(ref _tagName, value))
                {
                    SaveCommand.NotifyCanExecuteChanged();
                    TriggerCompositeValidation();
                }
            }
        }

        private string _keyword = string.Empty;
        public string Keyword
        {
            get => _keyword;
            set
            {
                if (SetProperty(ref _keyword, value))
                {
                    SaveCommand.NotifyCanExecuteChanged();
                    TriggerCompositeValidation();
                }
            }
        }

        public TagRuleFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? tagRuleId = null)
            : base(orchestrator, broker, tagRuleId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                // Hydrate the master list of tags
                _allTags = (await Orchestrator.GetAllTagsWithHierarchyAsync(ViewCts.Token)).ToList();

                var allRules = await Orchestrator.GetAllTagRulesWithHierarchyAsync(ViewCts.Token);
                var existing = allRules.FirstOrDefault(r => r.TagRuleId == ExcludeId);

                RunOnUIThread(() =>
                {
                    AvailableTags.Clear();
                    foreach (var t in _allTags) if (!string.IsNullOrEmpty(t.TagName)) AvailableTags.Add(t.TagName);

                    if (existing != null)
                    {
                        // Bypass setters to avoid triggering validation on load
                        _keyword = existing.Keyword ?? "";
                        OnPropertyChanged(nameof(Keyword));

                        _tagName = existing.TagName ?? "";
                        OnPropertyChanged(nameof(TagName));
                    }
                });

            }
            catch (OperationCanceledException) { /* Silently abort */ }
        }

        // --- COMPOSITE VALIDATION ENGINE ---
        private void TriggerCompositeValidation()
        {
            // We can only validate the combination if both fields have values
            if (string.IsNullOrWhiteSpace(Keyword) || string.IsNullOrWhiteSpace(TagName))
            {
                ClearError(nameof(Keyword));
                return;
            }

            // Instantly resolve the TagName to the TagId using the in-memory cache
            var targetTag = _allTags.FirstOrDefault(t => t.TagName != null && t.TagName.Equals(TagName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (targetTag == null)
            {
                // If the user typed a non-existent tag, we can't check composite uniqueness yet
                ClearError(nameof(Keyword));
                return;
            }

            var props = new Dictionary<string, object>
            {
                { "Keyword", Keyword.Trim() },
                { "TagId", targetTag.TagId } // Validate against the actual foreign key
            };

            // Will check UNIQUE(Keyword, TagId) via the Orchestrator
            ValidateUniqueCompositeAsync(DomainEntity.TagRule, props, nameof(Keyword), "This Keyword is already mapped to this exact Tag.");
        }

        protected override bool CanSave() => !HasErrors && !string.IsNullOrWhiteSpace(Keyword) && !string.IsNullOrWhiteSpace(TagName);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            // Use the cached list to resolve the TagId
            var targetTag = _allTags.FirstOrDefault(t => t.TagName != null && t.TagName.Equals(TagName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (targetTag == null)
            {
                throw new InvalidOperationException("You must select a valid, existing Tag for this rule.");
            }

            var rule = new TagRule
            {
                Id = ExcludeId ?? 0,
                Keyword = Keyword.Trim(),
                TagId = targetTag.TagId,
            };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateTagRuleAsync(rule.Id, rule.Keyword, rule.TagId, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.TagRule, rule.Id));
            }
            else
            {
                await Orchestrator.AddTagRuleAsync(rule.Keyword, rule.TagId, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.TagRule));
            }
        }
    }
}