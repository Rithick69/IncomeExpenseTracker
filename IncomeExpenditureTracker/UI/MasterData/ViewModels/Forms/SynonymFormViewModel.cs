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
    public partial class SynonymFormViewModel : FormViewModelBase
    {
        // Unique list of category types derived from existing synonyms in the database
        public ObservableCollection<string> AvailableCategoryTypes { get; } = new();
        public ObservableCollection<string> AvailableFieldTypes { get; } = new();

        private string _categoryTypeName = string.Empty;
        public string CategoryTypeName
        {
            get => _categoryTypeName;
            set
            {
                if (SetProperty(ref _categoryTypeName, value))
                {
                    UpdateDependentFieldTypes();
                    TriggerCompositeValidation();
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private string _fieldType = string.Empty;
        public string FieldType
        {
            get => _fieldType;
            set
            {
                if (SetProperty(ref _fieldType, value))
                {
                    TriggerCompositeValidation();
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        private string _synonymName = string.Empty;
        public string SynonymName
        {
            get => _synonymName;
            set
            {
                if (SetProperty(ref _synonymName, value))
                {
                    TriggerCompositeValidation();
                    SaveCommand.NotifyCanExecuteChanged();
                }
            }
        }

        public SynonymFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? synonymId = null)
            : base(orchestrator, broker, synonymId) { }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                var allSynonyms = await Orchestrator.GetAllSynonymsWithHierarchyAsync(ViewCts.Token);
                var existing = allSynonyms.FirstOrDefault(s => s.SynonymsId == ExcludeId);

                RunOnUIThread(() =>
                {
                    // 1. Filter out empty values, select the specific property, and extract unique values
                    var uniqueCategories = allSynonyms
                        .Where(c => !string.IsNullOrEmpty(c.CategoryName))
                        .Select(c => c.CategoryName)
                        .Distinct();

                    var uniqueFieldTypes = allSynonyms
                        .Where(f => !string.IsNullOrEmpty(f.FieldType))
                        .Select(f => f.FieldType)
                        .Distinct();

                    // 2. Clear and populate the ObservableCollections
                    AvailableCategoryTypes.Clear();
                    foreach (var c in uniqueCategories)
                    {
                        AvailableCategoryTypes.Add(c);
                    }

                    AvailableFieldTypes.Clear();
                    foreach (var f in uniqueFieldTypes)
                    {
                        AvailableFieldTypes.Add(f);
                    }

                    if (existing != null)
                    {
                        // Bypass setters to avoid triggering validation on load
                        _synonymName = existing.SynonymName ?? "";
                        OnPropertyChanged(nameof(SynonymName));

                        _fieldType = existing.FieldType ?? "";
                        OnPropertyChanged(nameof(FieldType));

                        _categoryTypeName = existing.CategoryName ?? "";
                        OnPropertyChanged(nameof(CategoryTypeName));

                        UpdateDependentFieldTypes();
                    }
                });
            }
            catch (OperationCanceledException) { }
        }

        private void TriggerCompositeValidation()
        {
            if (string.IsNullOrWhiteSpace(SynonymName) || string.IsNullOrWhiteSpace(FieldType) || string.IsNullOrWhiteSpace(CategoryTypeName))
            {
                ClearError(nameof(SynonymName));
                return;
            }

            var props = new Dictionary<string, object>
            {
                { "Synonym", SynonymName.Trim() },
                { "FieldType", FieldType.Trim() },
                { "Category", CategoryTypeName.Trim()}
            };

            ValidateUniqueCompositeAsync(DomainEntity.Synonym, props, nameof(SynonymName), "This exact Synonym, Field Type and Category Type combination already exists.");
        }

        private void UpdateDependentFieldTypes()
        {
            RunOnUIThread(() =>
            {
                // Cache the current FieldType because clearing the ObservableCollection
                // will cause TwoWay bindings (like in a ComboBox) to reset FieldType to null/empty.
                var currentFieldType = FieldType;

                AvailableFieldTypes.Clear();

                if (CategoryTypeName == "METADATA")
                {
                    foreach (var field in Enum.GetNames(typeof(MetadataField)))
                    {
                        AvailableFieldTypes.Add(field);
                    }
                }
                else if (CategoryTypeName == "TRANSACTION")
                {
                    foreach (var field in Enum.GetNames(typeof(TransactionColumnField)))
                    {
                        AvailableFieldTypes.Add(field);
                    }
                }

                // Safety Reset: If the user had "ACCOUNT_NUMBER" selected, but then switched
                // the category to "TRANSACTION", we must clear the FieldType because
                // "ACCOUNT_NUMBER" is no longer a valid option.
                if (!string.IsNullOrEmpty(currentFieldType))
                {
                    if (AvailableFieldTypes.Contains(currentFieldType))
                    {
                        FieldType = currentFieldType;
                    }
                    else
                    {
                        FieldType = string.Empty;
                    }
                }
                else
                {
                    FieldType = string.Empty;
                }
            });
        }

        protected override bool CanSave()
            => !HasErrors && !string.IsNullOrWhiteSpace(SynonymName) && !string.IsNullOrWhiteSpace(FieldType) && !string.IsNullOrWhiteSpace(CategoryTypeName);

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {

            var synonym = new Synonyms
            {
                Id = ExcludeId ?? 0,
                Synonym = SynonymName.Trim(),
                FieldType = FieldType.Trim(),
                Category = CategoryTypeName
            };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateSynonymAsync(synonym, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Synonym, synonym.Id));
            }
            else
            {
                await Orchestrator.AddSynonymAsync(synonym, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Synonym));
            }
        }
    }
}