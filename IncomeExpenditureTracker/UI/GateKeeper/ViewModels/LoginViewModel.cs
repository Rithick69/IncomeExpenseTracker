using System;
using System.Security;
using Avalonia.Controls;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Gatekeeper;

// @desc    Handles state management and authentication logic for the login screen
// @route   View-Model for /UI/Gatekeeper/LoginView
public partial class LoginViewModel : ViewModelBase
{
    private readonly IProfileLoginService _loginService;

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Add the attribute here so Avalonia knows to re-render ButtonText when IsLoading changes
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginButtonText))]
    private bool _isLoading;

    // The derived state (React equivalent: a calculated variable in your render function)
    public string LoginButtonText => IsLoading ? "Authenticating..." : "Login";

    // @desc    Constructor injects the global broker and the profile login service
    public LoginViewModel(IApplicationBroker broker, IProfileLoginService loginService) : base(broker)
    {
        _loginService = loginService;
    }

    // @desc    Executes the secure login flow using the Avalonia TextBox directly
    // @action  Accepts the UI control, extracts to SecureString, wipes the control
    [RelayCommand]
    public async Task LoginAsync(TextBox passwordBox)
    {
        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Username) || passwordBox == null || string.IsNullOrEmpty(passwordBox.Text))
        {
            ErrorMessage = "Please enter both username and password.";
            return;
        }

        IsLoading = true;
        var securePassword = new SecureString();

        try
        {
            // Transfer chars one-by-one to unmanaged secure memory
            foreach (char c in passwordBox.Text)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            // INSTANTLY wipe the Avalonia UI control's memory so it doesn't linger
            passwordBox.Text = string.Empty;

            // Send the SecureString down into the ProfileLoginService for PBKDF2 hashing
            // and SQLCipher PRAGMA key injection
            bool success = await _loginService.AuthenticateAndLoadProfileAsync(Username, securePassword);


            if (success)
            {
                RunOnUIThread(() => ErrorMessage = "Login Successful! Loading workspace...");

                // Dispatch the route change (React equivalent: navigate('/dashboard'))
                Broker.Send(new NavigationMessage("Dashboard"));
            }
            else
            {
                RunOnUIThread(() => ErrorMessage = "Invalid credentials or account locked. Please try again.");
            }
        }
        catch (Exception)
        {
            RunOnUIThread(() =>
            {

                ErrorMessage = "Failed to Login. Please try again.";
                Broker.Send(new ToastNotificationMessage("An unexpected error occurred during login.", NotificationType.Error));

            });
        }
        finally
        {
            RunOnUIThread(() => IsLoading = false);

            // In a strict Zero-Leak setup, ensure the SecureString is disposed
            // after the underlying service consumes it if it isn't passed by reference.
            securePassword.Dispose();
        }
    }

    // @desc    Navigation trigger to switch to the Registration screen
    [RelayCommand]
    private void NavigateToRegister()
    {
        // Emits a routing request to MainWindowViewModel
        Broker.Send(new NavigationMessage("Register"));
    }
}