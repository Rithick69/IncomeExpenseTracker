using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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

    // Dropdown Data Sources (Used by AutoCompleteBox FilterMode="Contains")
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

    private bool _isAutoFilling;

    // Debounce Tokens
    private CancellationTokenSource? _entitySearchCts;
    private CancellationTokenSource? _accountSearchCts;
    private CancellationTokenSource? _cardSearchCts;

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

    public async Task InitializeAsync()
    {
        // Fetch only small distinct lists on load
        var types = await _accountService.GetDistinctAccountTypesAsync();
        foreach (var t in types) AvailableAccountTypes.Add(t);

        var currencies = await _accountService.GetDistinctCurrenciesAsync();
        foreach (var c in currencies) AvailableCurrencies.Add(c);

        if (AvailableCurrencies.Count == 0)
        {
            var userCurrency = await _userSettingsService.GetSettingAsync("BaseCurrency");
            if (!string.IsNullOrWhiteSpace(userCurrency)) AvailableCurrencies.Add(userCurrency);
        }
    }

    // =========================================================================
    // REACTIVE UI HOOKS (Triggered as user types in AutoCompleteBox)
    // =========================================================================

    // =========================================================================
    // DEBOUNCED SEARCH HOOKS
    // =========================================================================

    partial void OnEntityNameChanged(string value)
    {
        if (_isAutoFilling || string.IsNullOrWhiteSpace(value)) return;
        DebounceEntitySearch(value);
    }

    partial void OnAccountNumberChanged(string value)
    {
        if (_isAutoFilling) return;
        IsCardNumberEnabled = string.IsNullOrWhiteSpace(value);

        if (!string.IsNullOrWhiteSpace(value))
        {
            IsCreditLimitVisible = false;
            CreditLimit = string.Empty;
            DebounceAccountSearch(value);

            // Fire-and-forget the autofill check
            _ = CheckAndApplyAutoFillAsync(value, isCardNumber: false);
        }
    }

    partial void OnCardNumberChanged(string value)
    {
        if (_isAutoFilling) return;
        IsAccountNumberEnabled = string.IsNullOrWhiteSpace(value);
        IsCreditLimitVisible = !string.IsNullOrWhiteSpace(value);

        if (!IsCreditLimitVisible) CreditLimit = string.Empty;
        if (!string.IsNullOrWhiteSpace(value))
        {
            DebounceCardSearch(value); // Assuming card search uses the same debounce logic

            // Fire-and-forget the autofill check
            _ = CheckAndApplyAutoFillAsync(value, isCardNumber: true);
        }
    }

    private async void DebounceEntitySearch(string query)
    {
        _entitySearchCts?.Cancel();
        _entitySearchCts = new CancellationTokenSource();
        var token = _entitySearchCts.Token;

        try
        {
            await Task.Delay(300, token); // 300ms wait
            var results = await _entityService.SearchEntityNamesAsync(query, token);

            AvailableEntityNames.Clear();
            foreach (var r in results) AvailableEntityNames.Add(r);
        }
        catch (TaskCanceledException) { /* Ignored, user kept typing */ }
    }

    private async void DebounceAccountSearch(string query)
    {
        _accountSearchCts?.Cancel();
        _accountSearchCts = new CancellationTokenSource();
        var token = _accountSearchCts.Token;

        try
        {
            await Task.Delay(300, token);
            var results = await _accountService.SearchAccountNumbersAsync(query, token);

            AvailableAccountNumbers.Clear();
            foreach (var r in results) AvailableAccountNumbers.Add(r);
        }
        catch (TaskCanceledException) { }
    }

    private async void DebounceCardSearch(string query)
    {
        _cardSearchCts?.Cancel();
        _cardSearchCts = new CancellationTokenSource();
        var token = _cardSearchCts.Token;

        try
        {
            await Task.Delay(300, token);
            var results = await _accountService.SearchCardNumbersAsync(query, token);

            AvailableCardNumbers.Clear();
            foreach (var r in results) AvailableCardNumbers.Add(r);
        }
        catch (TaskCanceledException) { }
    }

    // Safely fetches the account from the DB without blocking the UI thread
    private async Task CheckAndApplyAutoFillAsync(string identifier, bool isCardNumber)
    {
        try
        {
            Account? match = isCardNumber
                ? await _accountService.GetAccountByCardNumberAsync(identifier)
                : await _accountService.GetAccountByNumberAsync(identifier);

            if (match != null)
            {
                ApplyAutoFill(match);
            }
        }
        catch
        {
            // Ignore DB lookup errors while typing
        }
    }

    private void ApplyAutoFill(Account account)
    {
        _isAutoFilling = true;
        if (string.IsNullOrWhiteSpace(EntityName)) EntityName = account.EntityName;
        if (string.IsNullOrWhiteSpace(AccountType)) AccountType = account.AccountType;
        if (string.IsNullOrWhiteSpace(Currency)) Currency = account.Currency;
        if (string.IsNullOrWhiteSpace(CreditLimit)) CreditLimit = account.CreditLimit;
        UpdateUIStates();
        _isAutoFilling = false;
    }

    // =========================================================================
    // DICTIONARY MAPPING
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
        bool hasData = !string.IsNullOrWhiteSpace(value);

        if (!fields.TryGetValue(key, out var field))
        {
            field = new DetectedField { SuggestedField = key.Replace("Meta:", "") };
            fields[key] = field;
        }

        field.ExtractedValue = value ?? string.Empty;
        field.IsUserVerified = true;
        field.ConfidenceScore = hasData ? 100 : 0;
    }
}