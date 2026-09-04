using System.Security;
using System.Threading.Tasks;
using Avalonia.Controls;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Gatekeeper;
using IncomeExpenditureTracker.Services.Settings;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class LoginViewModelTests
{
    // MERN Equivalent: A setup helper to reset mocks before each test
    private readonly Mock<IApplicationBroker> _mockBroker;
    private readonly Mock<IProfileLoginService> _mockLoginService;
    private readonly Mock<IProfileRegistry> _mockProfileRegistry;
    private readonly Mock<IPasswordHasher> _mockPasswordHasher;

    public LoginViewModelTests()
    {
        _mockBroker = new Mock<IApplicationBroker>();
        _mockLoginService = new Mock<IProfileLoginService>();
        _mockProfileRegistry = new Mock<IProfileRegistry>();
        _mockPasswordHasher = new Mock<IPasswordHasher>();
    }

    private LoginViewModel CreateViewModel()
    {
        return new LoginViewModel(
            _mockBroker.Object,
            _mockLoginService.Object,
            _mockProfileRegistry.Object,
            _mockPasswordHasher.Object);
    }

    // =========================================================================
    // INITIALIZATION & STATE TESTS
    // =========================================================================

    [Fact]
    public async Task Constructor_FiresLoadProfiles_PopulatesGrid()
    {
        // Arrange
        var mockProfiles = new List<ProfileDto>
        {
            new ProfileDto { ProfileId = Guid.NewGuid().ToString(), ProfileName = "User1" },
            new ProfileDto { ProfileId = Guid.NewGuid().ToString(), ProfileName = "User2" }
        };
        _mockProfileRegistry.Setup(r => r.GetAllProfilesAsync()).ReturnsAsync(mockProfiles);

        // Act
        var viewModel = CreateViewModel();

        // Await slightly to allow the fire-and-forget LoadProfilesAsync() to complete
        await Task.Delay(50);

        // Assert
        Assert.Equal(2, viewModel.Profiles.Count);
        Assert.Equal(string.Empty, viewModel.ErrorMessage);
    }

    [Fact]
    public void SelectProfile_UpdatesState_ShowsPasswordPrompt()
    {
        var viewModel = CreateViewModel();
        var profile = new ProfileDto { ProfileName = "TestUser", Nickname = "Test" };

        viewModel.SelectProfileCommand.Execute(profile);

        Assert.Equal("TestUser", viewModel.Username);
        Assert.Equal(profile, viewModel.SelectedProfile);
        Assert.True(viewModel.IsPasswordPromptVisible);
    }

    [Fact]
    public void CancelSelection_WipesState_ReturnsToGrid()
    {
        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(new ProfileDto { ProfileName = "TestUser" });
        viewModel.InitiateDeleteCommand.Execute(null); // Dirty the delete state too

        viewModel.CancelSelectionCommand.Execute(null);

        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal(string.Empty, viewModel.Username);
        Assert.False(viewModel.IsPasswordPromptVisible);
        Assert.False(viewModel.IsDeletePromptVisible);
    }

    [Fact]
    public void NavigateToRegister_WipesState_RoutesToRegister()
    {
        var viewModel = CreateViewModel();
        viewModel.Username = "LingeringUser";
        viewModel.IsPasswordPromptVisible = true;

        viewModel.NavigateToRegisterCommand.Execute(null);

        // State wiped
        Assert.Equal(string.Empty, viewModel.Username);
        Assert.False(viewModel.IsPasswordPromptVisible);

        // Route dispatched
        _mockBroker.Verify(b => b.Send(It.Is<NavigationMessage>(m => m.Destination == "Register")), Times.Once);
    }

    // =========================================================================
    // AUTHENTICATION TESTS
    // =========================================================================

    [Fact]
    public async Task LoginAsync_MissingInputs_UpdatesErrorAndAborts()
    {
        var viewModel = CreateViewModel();
        viewModel.Username = ""; // Missing username

        var passwordBox = new TextBox { Text = string.Empty };

        await viewModel.LoginCommand.ExecuteAsync(passwordBox);

        Assert.Contains("enter both username and password", viewModel.ErrorMessage);
        _mockLoginService.Verify(s => s.AuthenticateAndLoadProfileAsync(It.IsAny<string>(), It.IsAny<SecureString>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_WipesMemoryAndNavigates()
    {
        _mockLoginService.Setup(s => s.AuthenticateAndLoadProfileAsync("admin", It.IsAny<SecureString>()))
                         .ReturnsAsync(true);

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(new ProfileDto { ProfileName = "admin" });
        var passwordBox = new TextBox { Text = "supersecret123" };

        await viewModel.LoginCommand.ExecuteAsync(passwordBox);

        // 1. Mathematically prove RAM wiping for Zero-Leak Architecture
        Assert.Equal(string.Empty, passwordBox.Text);

        // 2. Prove React Router style navigation fires
        _mockBroker.Verify(b => b.Send(It.Is<NavigationMessage>(m => m.Destination == "Dashboard")), Times.Once);
    }

    [Fact]
    public async Task LoginAsync_InvalidCredentials_SetsErrorMessage()
    {
        _mockLoginService.Setup(s => s.AuthenticateAndLoadProfileAsync(It.IsAny<string>(), It.IsAny<SecureString>()))
                         .ReturnsAsync(false); // Simulate failed auth

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(new ProfileDto { ProfileName = "admin" });
        var passwordBox = new TextBox { Text = "wrongpass" };

        await viewModel.LoginCommand.ExecuteAsync(passwordBox);

        Assert.Contains("Invalid credentials", viewModel.ErrorMessage);
        Assert.False(viewModel.IsLoading);
    }

    // MERN Equivalent: Asserting mid-flight React state (isLoading === true) during pending Promises
    [Fact]
    public async Task LoginAsync_MidFlightState_CorrectlyTogglesIsLoading()
    {
        var tcs = new TaskCompletionSource<bool>();
        _mockLoginService.Setup(s => s.AuthenticateAndLoadProfileAsync(It.IsAny<string>(), It.IsAny<SecureString>()))
                         .Returns(tcs.Task); // Freezes the service mid-execution

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(new ProfileDto { ProfileName = "admin" });
        var passwordBox = new TextBox { Text = "secret" };

        // Act: Start the task without awaiting it yet
        var loginTask = viewModel.LoginCommand.ExecuteAsync(passwordBox);

        // Assert 1: State is actively loading
        Assert.True(viewModel.IsLoading);
        Assert.Equal("Authenticating...", viewModel.LoginButtonText);

        // Act: Resolve the frozen promise
        tcs.SetResult(true);
        await loginTask;

        // Assert 2: Loading state has gracefully wound down
        Assert.False(viewModel.IsLoading);
        Assert.Equal("Login", viewModel.LoginButtonText);
    }

    // =========================================================================
    // VAULT NUKE (DELETION) TESTS
    // =========================================================================

    [Fact]
    public async Task ConfirmDeleteAsync_Unauthorized_BlocksDeletion()
    {
        var profile = new ProfileDto
        {
            ProfileId = Guid.NewGuid().ToString(),
            PasswordHash = "pHash",
            PasswordSalt = "pSalt",
            MasterKeyHash = "mHash",
            MasterKeySalt = "mSalt"
        };

        // Simulate hasher returning false for BOTH checks
        _mockPasswordHasher.Setup(h => h.VerifyPassword(It.IsAny<SecureString>(), It.IsAny<string>(), It.IsAny<string>())).Returns(false);

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(profile);

        var authBox = new TextBox { Text = "wrong-code" };

        await viewModel.ConfirmDeleteCommand.ExecuteAsync(authBox);

        Assert.Contains("Invalid Password or Master Key", viewModel.ErrorMessage);
        _mockProfileRegistry.Verify(r => r.DeleteProfileAsync(It.IsAny<String>()), Times.Never);
    }

    [Fact]
    public async Task ConfirmDeleteAsync_AuthorizedViaMasterKey_ExecutesDeletion()
    {
        var profileId = Guid.NewGuid().ToString();
        var profile = new ProfileDto
        {
            ProfileId = profileId,
            PasswordHash = "pHash",
            PasswordSalt = "pSalt",
            MasterKeyHash = "mHash",
            MasterKeySalt = "mSalt"
        };

        // Simulate hasher failing the password check, but PASSING the Master Key check
        _mockPasswordHasher.Setup(h => h.VerifyPassword(It.IsAny<SecureString>(), "pHash", "pSalt")).Returns(false);
        _mockPasswordHasher.Setup(h => h.VerifyPassword(It.IsAny<SecureString>(), "mHash", "mSalt")).Returns(true);

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(profile);

        var authBox = new TextBox { Text = "correct-master-key" };

        await viewModel.ConfirmDeleteCommand.ExecuteAsync(authBox);

        // Verify memory wipe of the auth box
        Assert.Equal(string.Empty, authBox.Text);

        // Verify the profile was deleted from the registry
        _mockProfileRegistry.Verify(r => r.DeleteProfileAsync(profileId), Times.Once);

        // Verify success toast was sent
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m => m.Type == NotificationType.Success)), Times.Once);

        // Verify UI returned to grid
        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsDeletePromptVisible);
    }

    // =========================================================================
    // FACTORY RESET TESTS
    // =========================================================================

    [Fact]
    public async Task FactoryResetAsync_UserCancels_DoesNothing()
    {
        // Intercept the confirmation dialog and resolve it as FALSE (User clicked Cancel)
        _mockBroker.Setup(b => b.Send(It.IsAny<ShowConfirmationMessage>()))
            .Callback<ShowConfirmationMessage>(msg =>
            {
                if (msg is ShowConfirmationMessage confMsg && confMsg.CompletionSource != null)
                {
                    confMsg.CompletionSource.TrySetResult(false);
                }
            });

        var viewModel = CreateViewModel();
        viewModel.Username = "ShouldNotBeWiped";

        await viewModel.FactoryResetCommand.ExecuteAsync(null);

        // Assert nothing happened
        Assert.Equal("ShouldNotBeWiped", viewModel.Username);
        _mockBroker.Verify(b => b.Send(It.IsAny<ToastNotificationMessage>()), Times.Never);
    }

    [Fact]
    public async Task FactoryResetAsync_UserConfirms_ExecutesWipe()
    {
        // Intercept the confirmation dialog and resolve it as TRUE (User clicked Confirm)
        _mockBroker.Setup(b => b.Send(It.IsAny<ShowConfirmationMessage>()))
            .Callback<ShowConfirmationMessage>(msg =>
            {
                if (msg is ShowConfirmationMessage confMsg && confMsg.CompletionSource != null)
                {
                    confMsg.CompletionSource.TrySetResult(true);
                }
            });

        var viewModel = CreateViewModel();
        viewModel.SelectProfileCommand.Execute(new ProfileDto { ProfileName = "Dummy" });

        await viewModel.FactoryResetCommand.ExecuteAsync(null);

        // Assert state was fully wiped after confirmation
        Assert.Null(viewModel.SelectedProfile);
        Assert.False(viewModel.IsPasswordPromptVisible);

        // Assert success toast was fired
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m => m.Message.Contains("All data wiped"))), Times.Once);
    }
}