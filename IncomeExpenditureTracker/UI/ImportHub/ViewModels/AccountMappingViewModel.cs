using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Settings;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Messaging;

namespace IncomeExpenditureTracker.UI.ImportHub;

public partial class AccountMappingViewModel : ViewModelBase
{
    private readonly IAccountService _accountService;
    private readonly IEntityService _entityService;
    private readonly IUserSettingsService _userSettingsService;

    private List<Account> _allAccounts = new();

    // Dropdown Data Sources
    public ObservableCollection<string> AvailableEntityNames { get; } = new();
    public ObservableCollection<string> AvailableAccountNumbers { get; } = new();
    public ObservableCollection<string> AvailableCardNumbers { get; } = new();
    public ObservableCollection<string> AvailableAccountTypes { get; } = new();
    public ObservableCollection<string> AvailableCurrencies { get; } = new();

    // Bound Properties
    [ObservableProperty] private string _entityName = string.Empty;
    [ObservableProperty] private string _accountNumber = string.Empty;
    [ObservableProperty] private string _cardNumber = string.Empty;
    [ObservableProperty] private string _accountType = string.Empty;
    [ObservableProperty] private string _currency = string.Empty;
    [ObservableProperty] private string _creditLimit = string.Empty;

    // UI States
    [ObservableProperty] private bool _isCreditLimitVisible;
    [ObservableProperty] private bool _isAccountNumberEnabled = true;
    [ObservableProperty] private bool _isCardNumberEnabled = true;

    // Prevents cyclic updates during Auto-Fill
    private bool _isAutoFilling;

    public AccountMappingViewModel(
        IAccountService accountService,
        IEntityService entityService,
        IUserSettingsService userSettingsService,
        IApplicationBroker broker) : base(broker)
    {
        _accountService = accountService;
        _entityService = entityService;
        _userSettingsService = userSettingsService;
    }

    /// <summary>
    /// Fetches existing database records to populate the editable dropdowns.
    /// </summary>
    public async Task InitializeAsync()
    {
        // 1. Load Entities
        var entities = await _entityService.GetAllEntities();
        foreach (var e in entities) AvailableEntityNames.Add(e.Name);

        // 2. Load Accounts[cite: 4]
        _allAccounts = await _accountService.GetAllAccounts();
        foreach (var a in _allAccounts)
        {
            if (!string.IsNullOrWhiteSpace(a.AccountNumber)) AvailableAccountNumbers.Add(a.AccountNumber);
            if (!string.IsNullOrWhiteSpace(a.CardNumber)) AvailableCardNumbers.Add(a.CardNumber);
            if (!string.IsNullOrWhiteSpace(a.AccountType) && !AvailableAccountTypes.Contains(a.AccountType))
            {
                AvailableAccountTypes.Add(a.AccountType);
            }
            if (!string.IsNullOrWhiteSpace(a.Currency) && !AvailableCurrencies.Contains(a.Currency))
            {
                AvailableCurrencies.Add(a.Currency);
            }
        }

        if (AvailableCurrencies.Count == 0)
        {
            // Fallback to user settings if no currencies are found in accounts
            var userCurrency = _userSettingsService.GetSettingAsync("BaseCurrency").Result;
            if (!string.IsNullOrWhiteSpace(userCurrency))
            {
                AvailableCurrencies.Add(userCurrency);
            }
        }
    }

    // =========================================================================
    // REACTIVE UI HOOKS
    // =========================================================================

    [RelayCommand]
    private void ClearEntityField()
    {
        EntityName = string.Empty;
    }
    [RelayCommand]
    private void ClearAccountNumField()
    {
        AccountNumber = string.Empty;
    }
    [RelayCommand]
    private void ClearCardNumField()
    {
        CardNumber = string.Empty;
    }
    [RelayCommand]
    private void ClearAccountTypeField()
    {
        AccountType = string.Empty;
    }
    [RelayCommand]
    private void ClearCurrencyField()
    {
        Currency = string.Empty;
    }

    // partial void OnAccountTypeChanged(string value)
    // {
    //     IsCreditLimitVisible = value?.Equals("Credit Card", StringComparison.OrdinalIgnoreCase) == true;
    //     if (!IsCreditLimitVisible && !_isAutoFilling)
    //     {
    //         CreditLimit = string.Empty; // Clear hidden field logic
    //     }
    // }

    partial void OnAccountNumberChanged(string value)
    {
        if (_isAutoFilling) return;

        IsCardNumberEnabled = string.IsNullOrWhiteSpace(value);

        // If an account number is provided, it's not a credit card -> hide limit
        if (!string.IsNullOrWhiteSpace(value))
        {
            IsCreditLimitVisible = false;
            if (!_isAutoFilling)
            {
                CreditLimit = string.Empty;
            }
        }

        if (!_isAutoFilling && !string.IsNullOrWhiteSpace(value))
        {
            var match = _allAccounts.FirstOrDefault(a => a.AccountNumber == value);
            if (match != null) ApplyAutoFill(match);
        }
    }

    partial void OnCardNumberChanged(string value)
    {
        if (_isAutoFilling) return;

        IsAccountNumberEnabled = string.IsNullOrWhiteSpace(value);

        // If a card number is provided, show the limit. If cleared, hide it.
        IsCreditLimitVisible = !string.IsNullOrWhiteSpace(value);
        if (!IsCreditLimitVisible && !_isAutoFilling)
        {
            CreditLimit = string.Empty;
        }

        if (!_isAutoFilling && !string.IsNullOrWhiteSpace(value))
        {
            var match = _allAccounts.FirstOrDefault(a => a.CardNumber == value);
            if (match != null) ApplyAutoFill(match);
        }
    }

    private void ApplyAutoFill(Account account)
    {
        _isAutoFilling = true;

        // Auto-fill while preserving manual entries if they exist
        if (string.IsNullOrWhiteSpace(EntityName)) EntityName = account.EntityName;
        if (string.IsNullOrWhiteSpace(AccountType)) AccountType = account.AccountType;
        if (string.IsNullOrWhiteSpace(Currency)) Currency = account.Currency;
        if (string.IsNullOrWhiteSpace(CreditLimit)) CreditLimit = account.CreditLimit;

        UpdateUIStates();
        _isAutoFilling = false;
    }

    // =========================================================================
    // DICTIONARY MAPPING (Orchestrator Integration)
    // =========================================================================

    public void LoadFromPreview(Dictionary<string, DetectedField> fields)
    {
        _isAutoFilling = true;

        EntityName = ExtractValue(fields, "EntityName", "Meta:ENTITY_NAME");
        AccountNumber = ExtractValue(fields, "AccountNumber", "Meta:ACCOUNT_NUMBER");
        CardNumber = ExtractValue(fields, "CardNumber", "Meta:CARD_NUMBER");
        AccountType = ExtractValue(fields, "AccountType", "Meta:ACCOUNT_TYPE");
        Currency = ExtractValue(fields, "Currency", "Meta:CURRENCY");
        CreditLimit = ExtractValue(fields, "CreditLimit", "Meta:CREDIT_LIMIT");

        UpdateUIStates();
        _isAutoFilling = false;
    }

    public void ApplyToMappings(Dictionary<string, DetectedField> fields)
    {
        SyncField(fields, "Meta:ENTITY_NAME", EntityName);
        SyncField(fields, "Meta:ACCOUNT_NUMBER", AccountNumber);
        SyncField(fields, "Meta:CARD_NUMBER", CardNumber);
        SyncField(fields, "Meta:ACCOUNT_TYPE", AccountType);
        SyncField(fields, "Meta:CURRENCY", Currency);
        SyncField(fields, "Meta:CREDIT_LIMIT", CreditLimit);
    }

    private void UpdateUIStates()
    {
        IsCreditLimitVisible = !string.IsNullOrWhiteSpace(CardNumber);
        IsCardNumberEnabled = string.IsNullOrWhiteSpace(AccountNumber);
        IsAccountNumberEnabled = string.IsNullOrWhiteSpace(CardNumber);
    }

    private string ExtractValue(Dictionary<string, DetectedField> fields, string key1, string key2)
    {
        if (fields.TryGetValue(key1, out var field1)) return field1.ExtractedValue ?? string.Empty;
        if (fields.TryGetValue(key2, out var field2)) return field2.ExtractedValue ?? string.Empty;
        return string.Empty;
    }

    private void SyncField(Dictionary<string, DetectedField> fields, string key, string value)
    {
        // If the user clears a field, we shouldn't necessarily penalize the confidence score
        // for optional fields, but we definitely want to boost it if they provided data.
        bool hasData = !string.IsNullOrWhiteSpace(value);

        if (!fields.TryGetValue(key, out var field))
        {
            field = new DetectedField { SuggestedField = key.Replace("Meta:", "") };
            fields[key] = field;
        }

        field.ExtractedValue = value ?? string.Empty;
        field.IsUserVerified = true;

        // CRITICAL FIX: Tell the backend IConfidenceService that this field is fully resolved
        // This stops the backend from dragging down the overall score due to missing/low-confidence metadata.
        if (hasData)
        {
            field.ConfidenceScore = 100;
        }
        else
        {
            // Optional: If you want empty fields to be ignored by the confidence calculator,
            // you can either remove them from the dictionary or leave their score as 0.
            field.ConfidenceScore = 0;
        }
    }
}