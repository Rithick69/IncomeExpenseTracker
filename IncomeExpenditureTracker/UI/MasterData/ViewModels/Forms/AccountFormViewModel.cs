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
    public partial class AccountFormViewModel : FormViewModelBase
    {
        [ObservableProperty] private bool _isAccountNumberEnabled = true;
        [ObservableProperty] private bool _isCardNumberEnabled = true;

        // --- AUTO-COMPLETE BINDINGS ---
        public ObservableCollection<string> AvailableEntities { get; } = new();
        public ObservableCollection<string> AvailableAccountTypes { get; } = new()
        {
            "Checking", "Savings", "Credit Card", "Cash", "Demat", "Loan", "FD", "RD"
        };

        public ObservableCollection<string> AvailableCurrencies { get; } = new();

        // --- FORM FIELDS ---
        private string _entityName = string.Empty;
        private string _accountType = string.Empty;
        [ObservableProperty] private string _currency = string.Empty;
        private string _creditLimit = string.Empty;

        private string _accountNumber = string.Empty;
        public string AccountNumber
        {
            get => _accountNumber;
            set
            {
                if (SetProperty(ref _accountNumber, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        IsCardNumberEnabled = false;
                        CardNumber = string.Empty; // Safely clears the other box
                        ClearError(nameof(CardNumber));

                        ValidateUniqueAsync(DomainEntity.Account, nameof(AccountNumber), value, "This Account Number is already registered.");
                    }
                    else
                    {
                        IsCardNumberEnabled = true;
                        ClearError(nameof(AccountNumber));
                    }

                    SaveCommand.NotifyCanExecuteChanged(); // Re-evaluate the OR condition
                }
            }
        }

        private string _cardNumber = string.Empty;
        public string CardNumber
        {
            get => _cardNumber;
            set
            {
                if (SetProperty(ref _cardNumber, value))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        IsAccountNumberEnabled = false;
                        AccountNumber = string.Empty; // Safely clears the other box
                        ClearError(nameof(AccountNumber));

                        ValidateUniqueAsync(DomainEntity.Account, nameof(CardNumber), value, "This Card Number is already registered.");
                    }
                    else
                    {
                        IsAccountNumberEnabled = true;
                        ClearError(nameof(CardNumber));
                    }

                    SaveCommand.NotifyCanExecuteChanged(); // Re-evaluate the OR condition
                }
            }
        }

        public string AccountType
        {
            get => _accountType;
            set
            {
                if (SetProperty(ref _accountType, value))
                {
                    SaveCommand.NotifyCanExecuteChanged(); // Re-evaluate the OR condition
                }
            }
        }

        public string EntityName
        {
            get => _entityName;
            set
            {
                if (SetProperty(ref _entityName, value))
                {
                    SaveCommand.NotifyCanExecuteChanged(); // Re-evaluate the OR condition
                }
            }
        }

        public string CreditLimit
        {
            get => _creditLimit;
            set
            {
                if (SetProperty(ref _creditLimit, value))
                {
                    SaveCommand.NotifyCanExecuteChanged(); // Re-evaluate the OR condition
                }
            }
        }

        public AccountFormViewModel(IMasterDataOrchestrator orchestrator, IApplicationBroker broker, int? accountId = null)
            : base(orchestrator, broker, accountId)
        {
        }

        [RelayCommand]
        public async Task InitializeAsync()
        {
            try
            {
                // Load base currency from encrypted user settings (Single-Currency Profile Rule)
                var baseCurrency = await Orchestrator.GetUserSettingsAsync("BaseCurrency", ViewCts.Token) ?? "INR";

                // Hydrate Entities for the AutoCompleteBox
                var entities = await Orchestrator.GetAllEntitiesAsync(ViewCts.Token);

                var allAccounts = await Orchestrator.GetAllAccountsAsync(ViewCts.Token);
                var existing = allAccounts.FirstOrDefault(a => a.Id == ExcludeId);

                RunOnUIThread(() =>
                {
                    AvailableCurrencies.Clear();
                    AvailableCurrencies.Add(baseCurrency);
                    // Auto-select the currency
                    Currency = baseCurrency;
                    AvailableEntities.Clear();
                    foreach (var e in entities) if (!string.IsNullOrEmpty(e.Name)) AvailableEntities.Add(e.Name);

                    if (existing != null)
                    {
                        _accountNumber = existing.AccountNumber ?? ""; // Set underlying field to bypass validation on load
                        _cardNumber = existing.CardNumber ?? "";

                        // Enforce mutual exclusion on initial load
                        IsCardNumberEnabled = string.IsNullOrWhiteSpace(_accountNumber);
                        IsAccountNumberEnabled = string.IsNullOrWhiteSpace(_cardNumber);

                        OnPropertyChanged(nameof(AccountNumber));
                        OnPropertyChanged(nameof(CardNumber));

                        EntityName = existing.EntityName ?? "";
                        AccountType = existing.AccountType ?? "";
                        CreditLimit = existing.CreditLimit?.ToString() ?? "";
                    }
                });
            }
            catch (OperationCanceledException) { /* Silently abort */ }
        }

        // --- BASE CLASS OVERRIDES ---

        protected override bool CanSave()
            => !HasErrors && (!string.IsNullOrWhiteSpace(AccountNumber) || !string.IsNullOrWhiteSpace(CardNumber));

        protected override async Task ExecuteSaveAsync(CancellationToken ct)
        {
            int? entityId = null;
            if (!string.IsNullOrWhiteSpace(EntityName))
            {
                // Dynamically resolve or create the parent Entity (Merchant/Institution) from the AutoCompleteBox string
                entityId = await Orchestrator.GetOrCreateEntityAsync(EntityName.Trim(), ct);
            }

            decimal? limit = decimal.TryParse(CreditLimit, out var parsed) ? parsed : null;

            var account = new Account
            {
                Id = ExcludeId ?? 0,
                // Ensure empty strings are saved as NULL in SQLite
                AccountNumber = string.IsNullOrWhiteSpace(AccountNumber) ? string.Empty : AccountNumber.Trim(),
                CardNumber = string.IsNullOrWhiteSpace(CardNumber) ? string.Empty : CardNumber.Trim(),
                EntityId = entityId,
                Currency = Currency,
                EntityName = EntityName,
                AccountType = AccountType,
                CreditLimit = limit?.ToString() ?? string.Empty
            };

            if (IsUpdateMode)
            {
                await Orchestrator.UpdateAccountAsync(account, ct);
                Broker.Send(new EntityUpdatedMessage(DomainEntity.Account, account.Id));
            }
            else
            {
                await Orchestrator.GetOrCreateAccountAsync(account, ct);
                Broker.Send(new EntitySavedMessage(DomainEntity.Account));
            }
        }
    }
}