using System;
using System.Security;
using System.Threading.Tasks;
using Avalonia.Controls;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Gatekeeper;
using IncomeExpenditureTracker.UI.Shared;
using Moq;
using Xunit;

namespace IncomeExpenditureTracker.Tests.UI.ViewModels;

public class RegisterViewModelTests
{
    private readonly Mock<IApplicationBroker> _mockBroker;
    private readonly Mock<IProfileRegistry> _mockRegistry;
    private readonly Mock<IPasswordHasher> _mockHasher;

    public RegisterViewModelTests()
    {
        _mockBroker = new Mock<IApplicationBroker>();
        _mockRegistry = new Mock<IProfileRegistry>();
        _mockHasher = new Mock<IPasswordHasher>();

        // Set the static flag globally to bypass the Avalonia UI thread during headless testing
        ViewModelBase.IsTestEnvironment = true;
    }

    // MERN Equivalent: Testing Express-Validator regex rejections
    [Theory]
    [InlineData("admin!")]
    [InlineData("../../Admin")]
    [InlineData("user<script>")]
    public async Task RegisterAsync_InvalidRegexName_UpdatesErrorAndAborts(string invalidName)
    {
        var viewModel = new RegisterViewModel(_mockBroker.Object, _mockRegistry.Object, _mockHasher.Object)
        {
            NewProfileName = invalidName
        };
        var passwordBox = new TextBox { Text = "securepassword" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        Assert.Contains("can only contain letters", viewModel.ErrorMessage);
        _mockRegistry.Verify(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>()), Times.Never);
    }

    [Fact]
    public async Task RegisterAsync_MissingPassword_UpdatesErrorAndAborts()
    {
        var viewModel = new RegisterViewModel(_mockBroker.Object, _mockRegistry.Object, _mockHasher.Object)
        {
            NewProfileName = "ValidName"
        };
        var passwordBox = new TextBox { Text = string.Empty };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        Assert.Equal("Please provide a master password.", viewModel.ErrorMessage);
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

        var viewModel = new RegisterViewModel(_mockBroker.Object, _mockRegistry.Object, _mockHasher.Object)
        {
            NewProfileName = "ExistingUser"
        };
        var passwordBox = new TextBox { Text = "securepassword" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        Assert.Equal("This profile name is already taken.", viewModel.ErrorMessage);

        // Verify a Toast Error was dispatched to the global overlay
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m =>
            m.Type == NotificationType.Error &&
            m.Message.Contains("already exists"))), Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_Success_WipesMemoryAndRoutesToLogin()
    {
        _mockHasher.Setup(h => h.HashPassword(It.IsAny<SecureString>()))
               .Returns(("mock-hash", "mock-salt"));
        _mockRegistry.Setup(r => r.RegisterProfileAsync(It.IsAny<ProfileDto>())).Returns(Task.CompletedTask);

        var viewModel = new RegisterViewModel(_mockBroker.Object, _mockRegistry.Object, _mockHasher.Object)
        {
            NewProfileName = "NewUser"
        };
        var passwordBox = new TextBox { Text = "supersecret123" };

        await viewModel.RegisterCommand.ExecuteAsync(passwordBox);

        // 1. Verify UI memory wipe
        Assert.Equal(string.Empty, passwordBox.Text);

        // 2. Verify Success Toast
        _mockBroker.Verify(b => b.Send(It.Is<ToastNotificationMessage>(m =>
            m.Type == NotificationType.Success &&
            m.Message.Contains("created successfully"))), Times.Once);

        // 3. Verify Routing back to Login
        _mockBroker.Verify(b => b.Send(It.Is<NavigationMessage>(m =>
            m.Destination == "Login")), Times.Once);
    }
}