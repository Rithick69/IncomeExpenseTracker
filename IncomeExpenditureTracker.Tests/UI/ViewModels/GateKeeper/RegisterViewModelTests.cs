using System;
using System.Security;
using System.Threading.Tasks;
using Avalonia.Controls;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Gatekeeper;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Settings;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class RegisterViewModelTests
{
    private readonly Mock<IApplicationBroker> _mockBroker;
    private readonly Mock<IProfileRegistry> _mockRegistry;
    private readonly Mock<IPasswordHasher> _mockHasher;
    private readonly Mock<IProfileLoginService> _mockLoginService;
    private readonly Mock<IUserSettingsService> _mockUserSettingsService;

    public RegisterViewModelTests()
    {
        _mockBroker = new Mock<IApplicationBroker>();
        _mockRegistry = new Mock<IProfileRegistry>();
        _mockHasher = new Mock<IPasswordHasher>();
        _mockLoginService = new Mock<IProfileLoginService>();
        _mockUserSettingsService = new Mock<IUserSettingsService>();

        // Set the static flag globally to bypass the Avalonia UI thread during headless testing
        ViewModelBase.IsTestEnvironment = true;
    }

    // Factory method to easily create the ViewModel with all dependencies injected correctly
    private RegisterViewModel CreateViewModel()
    {
        return new RegisterViewModel(
            _mockRegistry.Object,
            _mockHasher.Object,
            _mockLoginService.Object,
            _mockUserSettingsService.Object,
            _mockBroker.Object
        );
    }

    // MERN Equivalent: Testing Express-Validator regex rejections
    [Theory]
    [InlineData("admin!")]
    [InlineData("../../Admin")]
    [InlineData("user<script>")]
    public async Task RegisterAsync_InvalidRegexName_UpdatesErrorAndAborts(string invalidName)
    {
        var viewModel = CreateViewModel();
        viewModel.ProfileName = invalidName;
        viewModel.Nickname = "ValidNick";

        var passwordBox = new TextBox { Text = "securepassword" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        Assert.Contains("can only contain letters", viewModel.ErrorMessage);
        _mockRegistry.Verify(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_MissingPassword_UpdatesErrorAndAborts()
    {
        var viewModel = CreateViewModel();
        viewModel.ProfileName = "ValidName";
        viewModel.Nickname = "ValidNick";
        var passwordBox = new TextBox { Text = string.Empty };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        Assert.Equal("Password cannot be empty.", viewModel.ErrorMessage);
        _mockRegistry.Verify(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>()), Times.Never);
    }

    // MERN Equivalent: Testing MongoDB E11000 duplicate key error handling
    [Fact]
    public async Task RegisterAsync_DuplicateName_TrapsSqliteExceptionAndBroadcastsToast()
    {
        _mockHasher.Setup(h => h.HashPassword(It.IsAny<SecureString>()))
               .Returns(("hash", "salt"));

        // Simulate SQLite UNIQUE constraint failure
        _mockRegistry.Setup(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>()))
                     .ThrowsAsync(new Exception("SQLite Error 19: 'UNIQUE constraint failed: Profiles.ProfileName'"));

        var viewModel = CreateViewModel();
        viewModel.ProfileName = "ExistingUser";
        viewModel.Nickname = "User";
        var passwordBox = new TextBox { Text = "securepassword" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);
        Assert.Contains("already taken", viewModel.ErrorMessage);
    }

    [Fact]
    public async Task RegisterAsync_Success_ShowsMasterKeyModal_ThenRoutesToDashboard()
    {
        // 1. Setup Mocks
        _mockHasher.Setup(h => h.HashPassword(It.IsAny<SecureString>())).Returns(("mock-hash", "mock-salt"));
        _mockRegistry.Setup(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>())).Returns(Task.CompletedTask);
        _mockLoginService.Setup(l => l.AuthenticateAndLoadProfileAsync(It.IsAny<String>(), It.IsAny<SecureString>())).ReturnsAsync(true);
        _mockUserSettingsService.Setup(u => u.SetSettingAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

        // 2. CRITICAL FIX: Intercept the Master Key Dialog request and instantly resolve the TaskCompletionSource
        _mockBroker.Setup(b => b.Send(It.IsAny<ShowHelperMessage>()))
            .Callback<ShowHelperMessage>(msg =>
            {
                if (msg is ShowHelperMessage helperMsg && helperMsg.CompletionSource != null)
                {
                    helperMsg.CompletionSource.TrySetResult(true);
                }
            });

        var viewModel = CreateViewModel();
        viewModel.ProfileName = "NewUser";
        viewModel.Nickname = "Newbie";
        viewModel.SelectedCurrency = "$";

        var passwordBox = new TextBox { Text = "supersecret123" };

        // 3. Execute
        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        // 4. Assertions
        Assert.Equal(string.Empty, passwordBox.Text); // Verify UI memory wipe

        // Verify Settings were saved
        _mockUserSettingsService.Verify(u => u.SetSettingAsync("BaseCurrency", "$"), Times.Once);

        // Verify the Modal was shown
        _mockBroker.Verify(b => b.Send(It.Is<ShowHelperMessage>(m => m.Title == "SAVE YOUR MASTER KEY")), Times.Once);

        // THE FIX: Changed "Dashboard" to "MainDashboard" to match your ViewModel's actual routing string
        _mockBroker.Verify(b => b.Send(It.Is<NavigationMessage>(m => m.Destination == "MainDashboard")), Times.Once);
    }

    // =========================================================================
    // STATE MANAGEMENT & EDGE CASE TESTS
    // =========================================================================

    [Fact]
    public void NavigateToLogin_ExecutesResetState_WipesFormData()
    {
        // Arrange
        var viewModel = CreateViewModel();

        // Dirty the state
        viewModel.ProfileName = "LingeringData";
        viewModel.Nickname = "LingeringNick";
        viewModel.SelectedCurrency = "$";
        viewModel.ErrorMessage = "Some old error";

        // Act
        viewModel.NavigateToLoginCommand.Execute(null);

        // Assert - verify the internal ResetState() method did its job
        Assert.Equal(string.Empty, viewModel.ProfileName);
        Assert.Equal(string.Empty, viewModel.Nickname);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
        Assert.Equal("₹", viewModel.SelectedCurrency); // Verifies it reset to default currency

        // Verify routing broadcasted correctly
        _mockBroker.Verify(b => b.Send(It.Is<NavigationMessage>(m => m.Destination == "Login")), Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_DatabaseExceptionDuringSettings_TrapsAndShowsError()
    {
        // Tests edge case where profile registers, but encrypted DB fails to initialize/save settings
        _mockHasher.Setup(h => h.HashPassword(It.IsAny<SecureString>())).Returns(("hash", "salt"));
        _mockRegistry.Setup(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>())).Returns(Task.CompletedTask);
        _mockLoginService.Setup(l => l.AuthenticateAndLoadProfileAsync(It.IsAny<string>(), It.IsAny<SecureString>())).ReturnsAsync(true);

        // Force failure on the user settings upsert
        _mockUserSettingsService.Setup(u => u.SetSettingAsync(It.IsAny<string>(), It.IsAny<string>()))
                                .ThrowsAsync(new Exception("Database locked"));

        var viewModel = CreateViewModel();
        viewModel.ProfileName = "ValidUser";
        viewModel.Nickname = "ValidNick";
        viewModel.SelectedCurrency = "₹";

        var passwordBox = new TextBox { Text = "password123" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        // Assert execution stopped and error was shown to user
        Assert.Contains("Failed to create profile", viewModel.ErrorMessage);

        // Verify it did NOT route to the Dashboard due to the crash
        _mockBroker.Verify(b => b.Send(It.IsAny<NavigationMessage>()), Times.Never);
    }
}