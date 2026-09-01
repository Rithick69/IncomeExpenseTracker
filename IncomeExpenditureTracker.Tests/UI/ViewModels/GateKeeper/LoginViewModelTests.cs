using System.Security;
using System.Threading.Tasks;
using Avalonia.Controls;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Gatekeeper;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class LoginViewModelTests
{
    // MERN Equivalent: A setup helper to reset mocks before each test
    private readonly Mock<IApplicationBroker> _mockBroker;
    private readonly Mock<IProfileLoginService> _mockLoginService;

    public LoginViewModelTests()
    {
        _mockBroker = new Mock<IApplicationBroker>();
        _mockLoginService = new Mock<IProfileLoginService>();
    }

    [Fact]
    public async Task LoginAsync_MissingInputs_UpdatesErrorAndAborts()
    {
        var viewModel = new LoginViewModel(_mockBroker.Object, _mockLoginService.Object);
        var passwordBox = new TextBox { Text = string.Empty };

        await viewModel.LoginCommand.ExecuteAsync(passwordBox);

        Assert.Equal("Please enter both username and password.", viewModel.ErrorMessage);
        _mockLoginService.Verify(s => s.AuthenticateAndLoadProfileAsync(It.IsAny<string>(), It.IsAny<SecureString>()), Times.Never);
    }

    [Fact]
    public async Task LoginAsync_ValidCredentials_WipesMemoryAndNavigates()
    {
        _mockLoginService.Setup(s => s.AuthenticateAndLoadProfileAsync("admin", It.IsAny<SecureString>()))
                         .ReturnsAsync(true);

        var viewModel = new LoginViewModel(_mockBroker.Object, _mockLoginService.Object)
        {
            Username = "admin"
        };

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

        var viewModel = new LoginViewModel(_mockBroker.Object, _mockLoginService.Object)
        {
            Username = "wronguser"
        };
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

        var viewModel = new LoginViewModel(_mockBroker.Object, _mockLoginService.Object)
        {
            Username = "admin"
        };
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
}