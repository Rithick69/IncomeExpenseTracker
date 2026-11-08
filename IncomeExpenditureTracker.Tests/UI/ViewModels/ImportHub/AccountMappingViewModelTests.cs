using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Moq;
using IncomeExpenditureTracker.UI.ImportHub;
using IncomeExpenditureTracker.Services.Entities;
using IncomeExpenditureTracker.Services.Settings;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class AccountMappingViewModelTests
{
    private readonly Mock<IAccountService> _mockAccountService;
    private readonly Mock<IEntityService> _mockEntityService;
    private readonly Mock<IUserSettingsService> _mockUserSettings;
    private readonly Mock<IApplicationBroker> _mockBroker;

    public AccountMappingViewModelTests()
    {
        // Enforce the Conveyor Belt Bypass for headless testing
        ViewModelBase.IsTestEnvironment = true;

        _mockAccountService = new Mock<IAccountService>();
        _mockEntityService = new Mock<IEntityService>();
        _mockUserSettings = new Mock<IUserSettingsService>();
        _mockBroker = new Mock<IApplicationBroker>();

        // Fix for the NullReferenceExceptions:
        // When observable properties change in tests, the ViewModel triggers Debounce searches.
        // We must mock these to return empty lists instead of null Tasks to prevent await crashes.
        _mockEntityService.Setup(s => s.SearchEntityNamesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        _mockAccountService.Setup(s => s.SearchAccountNumbersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        _mockAccountService.Setup(s => s.SearchCardNumbersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
    }

    private AccountMappingViewModel CreateViewModel()
    {
        return new AccountMappingViewModel(
            _mockAccountService.Object,
            _mockEntityService.Object,
            _mockUserSettings.Object,
            _mockBroker.Object);
    }

    [Fact]
    public async Task InitializeAsync_PopulatesDropdowns_FromDatabase()
    {
        // Arrange
        var viewModel = CreateViewModel();

        _mockAccountService.Setup(s => s.GetDistinctAccountTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Checking", "Savings" });

        _mockAccountService.Setup(s => s.GetDistinctCurrenciesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "USD", "EUR" });

        // Act
        await viewModel.InitializeAsync();

        // Assert
        Assert.Equal(2, viewModel.AvailableAccountTypes.Count);
        Assert.Contains("Checking", viewModel.AvailableAccountTypes);

        Assert.Equal(2, viewModel.AvailableCurrencies.Count);
        Assert.Contains("EUR", viewModel.AvailableCurrencies);

        // Settings should not be queried if the database already has currencies
        _mockUserSettings.Verify(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InitializeAsync_WhenCurrenciesEmpty_LoadsBaseCurrencyFromSettings()
    {
        // Arrange
        var viewModel = CreateViewModel();

        _mockAccountService.Setup(s => s.GetDistinctAccountTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        _mockAccountService.Setup(s => s.GetDistinctCurrenciesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>()); // Empty DB

        // The architectural rule: Lock to the Base Currency if DB is empty
        _mockUserSettings.Setup(s => s.GetSettingAsync("BaseCurrency", It.IsAny<CancellationToken>()))
            .ReturnsAsync("INR");

        // Act
        await viewModel.InitializeAsync();

        // Assert
        Assert.Single(viewModel.AvailableCurrencies);
        Assert.Contains("INR", viewModel.AvailableCurrencies);
    }

    [Fact]
    public void LoadFromPreview_PopulatesProperties_AndUpdatesUIState()
    {
        // Arrange
        var viewModel = CreateViewModel();
        var fields = new Dictionary<string, DetectedField>
        {
            { "Meta:ENTITY_NAME", new DetectedField { ExtractedValue = "Chase Bank" } },
            { "Meta:CARD_NUMBER", new DetectedField { ExtractedValue = "4444" } }
            // Account Number intentionally omitted
        };

        // Act
        viewModel.LoadFromPreview(fields);

        // Assert
        Assert.Equal("Chase Bank", viewModel.EntityName);
        Assert.Equal("4444", viewModel.CardNumber);
        Assert.Empty(viewModel.AccountNumber);

        // Verify UI states correctly toggle based on the populated card data
        Assert.True(viewModel.IsCreditLimitVisible);
        Assert.False(viewModel.IsAccountNumberEnabled); // Disabled because card number exists
    }

    [Fact]
    public void LoadFromPreview_EvaluatesKeysInPriorityOrder()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // The ViewModel explicitly extracts using ExtractValue(fields, "EntityName", "Meta:ENTITY_NAME")
        // which gives "EntityName" priority if both exist.
        var fields = new Dictionary<string, DetectedField>
        {
            { "EntityName", new DetectedField { ExtractedValue = "Primary Match" } },
            { "Meta:ENTITY_NAME", new DetectedField { ExtractedValue = "Secondary Match" } }
        };

        // Act
        viewModel.LoadFromPreview(fields);

        // Assert: It should resolve the first key passed into the ExtractValue method
        Assert.Equal("Primary Match", viewModel.EntityName);
    }

    [Fact]
    public void ApplyToMappings_CreatesOrUpdatesDetectedFields()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Simulate user typing into the UI forms
        viewModel.EntityName = "My New Bank";
        viewModel.AccountNumber = "99998888";

        var fields = new Dictionary<string, DetectedField>
        {
            // Pre-existing field to be updated
            { "Meta:ENTITY_NAME", new DetectedField { ExtractedValue = "Old Bank" } }
        };

        // Act
        viewModel.ApplyToMappings(fields);

        // Assert
        Assert.Equal(6, fields.Count); // Should populate all 6 metadata keys even if empty

        // Check updated existing field
        Assert.Equal("My New Bank", fields["Meta:ENTITY_NAME"].ExtractedValue);
        Assert.True(fields["Meta:ENTITY_NAME"].IsUserVerified);
        Assert.Equal(100, fields["Meta:ENTITY_NAME"].ConfidenceScore);

        // Check newly created field
        Assert.Equal("99998888", fields["Meta:ACCOUNT_NUMBER"].ExtractedValue);
        Assert.Equal("ACCOUNT_NUMBER", fields["Meta:ACCOUNT_NUMBER"].SuggestedField); // Ensures "Meta:" prefix was stripped for the internal label

        // Check empty field (Should have 0 confidence score)
        Assert.Empty(fields["Meta:CREDIT_LIMIT"].ExtractedValue);
        Assert.Equal(0, fields["Meta:CREDIT_LIMIT"].ConfidenceScore);
    }

    [Fact]
    public void ReactiveHooks_ChangingCardNumber_TogglesAccountNumberAndCreditLimit()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Initial state check
        Assert.True(viewModel.IsAccountNumberEnabled);
        Assert.False(viewModel.IsCreditLimitVisible);

        // Act: Simulate user typing a card number (triggers OnCardNumberChanged)
        viewModel.CardNumber = "1234";

        // Assert
        Assert.False(viewModel.IsAccountNumberEnabled);
        Assert.True(viewModel.IsCreditLimitVisible);

        // Act: User clears the card number
        viewModel.CardNumber = "";

        // Assert: Reverts back
        Assert.True(viewModel.IsAccountNumberEnabled);
        Assert.False(viewModel.IsCreditLimitVisible);
    }
}