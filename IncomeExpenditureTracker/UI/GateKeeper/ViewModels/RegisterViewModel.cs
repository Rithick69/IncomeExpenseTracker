using System;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.UI.Shared;
using IncomeExpenditureTracker.Services.Database;
using Microsoft.Extensions.Logging;

namespace IncomeExpenditureTracker.UI.Gatekeeper;

// @desc    Handles profile creation and secure password hashing
// @route   View-Model for /UI/Gatekeeper/RegisterView
public partial class RegisterViewModel : ViewModelBase
{
    private readonly IProfileRegistry _registry; // Inject your creation service
    private readonly IPasswordHasher _hasher;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegisterButtonText))]
    private bool _isLoading;

    public string RegisterButtonText => IsLoading ? "Creating..." : "Create Profile";

    public RegisterViewModel(IApplicationBroker broker, IProfileRegistry registry, IPasswordHasher hasher) : base(broker)
    {
        _registry = registry;
        _hasher = hasher;
    }

    // @desc    Executes profile creation securely wiping the UI control afterward
    [RelayCommand]
    private async Task RegisterAsync(TextBox passwordBox)
    {
        ErrorMessage = string.Empty;

        // 1. UI-Side Sanitization
        var sanitizedName = NewProfileName?.Trim();
        if (string.IsNullOrWhiteSpace(sanitizedName) || !Regex.IsMatch(sanitizedName, @"^[a-zA-Z0-9\-_ ]+$"))
        {
            ErrorMessage = "Profile name can only contain letters, numbers, hyphens, and underscores.";
            return;
        }

        if (passwordBox == null || string.IsNullOrEmpty(passwordBox.Text))
        {
            ErrorMessage = "Please provide a master password.";
            return;
        }

        IsLoading = true;
        var securePassword = new SecureString();

        try
        {
            foreach (char c in passwordBox.Text)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            passwordBox.Text = string.Empty; // Wipe UI memory instantly

            // 2. Generate Cryptographic Data
            var (hash, salt) = _hasher.HashPassword(securePassword);
            var profileId = Guid.NewGuid().ToString();

            // Prevent path traversal by tying the DB filename exclusively to the GUID
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"AppProfile_{profileId}.db");

            // 3. Construct the DTO
            var profileDto = new ProfileDto
            {
                ProfileId = profileId,
                ProfileName = sanitizedName,
                DatabaseFilePath = dbPath,
                PasswordHash = hash,
                PasswordSalt = salt
            };

            await _registry.RegisterProfileAsync(profileDto);

            RunOnUIThread(() =>
            {
                Broker.Send(new NavigationMessage("Login"));
                Broker.Send(new ToastNotificationMessage("Profile created successfully! Please log in.", NotificationType.Success));
            });
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                // Intercept the SQLite constraint violation
                if (ex.Message.Contains("UNIQUE constraint failed") ||
                   (ex.InnerException != null && ex.InnerException.Message.Contains("UNIQUE constraint failed")))
                {
                    ErrorMessage = "This profile name is already taken.";

                    // Broadcast the error to the global toast overlay
                    Broker.Send(new ToastNotificationMessage("Profile name already exists. Please choose another.", NotificationType.Error));
                }
                else
                {
                    ErrorMessage = "Failed to create profile. Please try again.";
                    Broker.Send(new ToastNotificationMessage("An unexpected error occurred during registration.", NotificationType.Error));
                }
            });
        }
        finally
        {
            RunOnUIThread(() => IsLoading = false);
            securePassword.Dispose();
        }
    }

    // @desc    Navigation trigger to return to the Login screen
    [RelayCommand]
    private void NavigateToLogin()
    {
        Broker.Send(new NavigationMessage("Login"));
    }
}