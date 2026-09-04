using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IncomeExpenditureTracker.Models;
using IncomeExpenditureTracker.Services.Database;
using IncomeExpenditureTracker.Services.Messaging;
using IncomeExpenditureTracker.Services.Settings;
using IncomeExpenditureTracker.UI.Shared;

namespace IncomeExpenditureTracker.UI.Gatekeeper;

// @desc    Handles profile creation and secure password hashing
// @route   View-Model for /UI/Gatekeeper/RegisterView
public partial class RegisterViewModel : ViewModelBase
{
    private readonly IProfileRegistry _profileRegistry;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IProfileLoginService _loginService;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IApplicationBroker _broker;

    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _nickname = string.Empty;

    [ObservableProperty]
    private string _selectedCurrency = "₹";

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RegisterButtonText))]
    private bool _isLoading;

    public string RegisterButtonText => IsLoading ? "Generating Vault..." : "Generate Encrypted Vault";

    public List<string> AvailableCurrencies { get; } = new() { "₹", "$", "€", "£" };
    public RegisterViewModel(
            IProfileRegistry profileRegistry,
            IPasswordHasher passwordHasher,
            IProfileLoginService loginService,
            IUserSettingsService userSettingsService,
            IApplicationBroker broker) : base(broker)
    {
        _profileRegistry = profileRegistry;
        _passwordHasher = passwordHasher;
        _loginService = loginService;
        _userSettingsService = userSettingsService;
        _broker = broker;
    }

    // @desc    Executes profile creation securely wiping the UI control afterward
    [RelayCommand]
    private async Task RegisterAsync(object passwordBoxControl)
    {
        ErrorMessage = string.Empty;

        if (passwordBoxControl is not TextBox passwordBox || string.IsNullOrWhiteSpace(passwordBox.Text))
        {
            ErrorMessage = "Password cannot be empty.";
            return;
        }

        if (string.IsNullOrWhiteSpace(ProfileName) || string.IsNullOrWhiteSpace(Nickname))
        {
            ErrorMessage = "Profile Name and Nickname are required.";
            return;
        }

        // 1. UI-Side Sanitization
        var sanitizedName = ProfileName?.Trim();
        if (string.IsNullOrWhiteSpace(sanitizedName) || !Regex.IsMatch(sanitizedName, @"^[a-zA-Z0-9\-_ ]+$"))
        {
            ErrorMessage = "Profile name can only contain letters, numbers, hyphens, and underscores.";
            return;
        }

        var sanitizedNickname = Nickname?.Trim();
        if (string.IsNullOrWhiteSpace(sanitizedNickname) || !Regex.IsMatch(sanitizedNickname, @"^[a-zA-Z0-9\-_ ]+$"))
        {
            ErrorMessage = "Nickname can only contain letters, numbers, hyphens, and underscores.";
            return;
        }

        IsLoading = true;

        // CRITICAL FIX: The 'using' statement guarantees SecureString is disposed and
        // unmanaged memory is released immediately after this block finishes.
        using var securePassword = new SecureString();

        try
        {
            // 1.SECURE UI BINDING CONTRACT: Extract to SecureString, then ANNIHILATE the TextBox text
            foreach (char c in passwordBox.Text)
            {
                securePassword.AppendChar(c);
            }
            securePassword.MakeReadOnly();

            passwordBox.Text = string.Empty; // Wipe UI memory instantly

            // 2. Generate Cryptographic Data
            var (hash, salt) = _passwordHasher.HashPassword(securePassword);
            var profileId = Guid.NewGuid().ToString();

            // Prevent path traversal by tying the DB filename exclusively to the GUID
            var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appDataFolder, "IncomeExpenditureTracker");
            var dbFilePath = Path.Combine(appFolder, $"AppProfile_{profileId}.db");

            // 3. Generate Master Key
            string masterKey = GenerateMasterKey();

            // 4. Hash it securely for storage
            using var secureMasterKey = new SecureString();
            foreach (char c in masterKey) secureMasterKey.AppendChar(c);
            secureMasterKey.MakeReadOnly();


            var (masterKeyHash, masterKeySalt) = _passwordHasher.HashPassword(secureMasterKey);

            // 5. Construct the DTO
            var profileDto = new ProfileDto
            {
                ProfileId = profileId,
                ProfileName = sanitizedName,
                Nickname = sanitizedNickname,
                DatabaseFilePath = dbFilePath,
                PasswordHash = hash,
                PasswordSalt = salt,
                MasterKeyHash = masterKeyHash,
                MasterKeySalt = masterKeySalt
            };

            await _profileRegistry.RegisterProfileAsync(profileDto);

            // 6. Authenticate & Trigger the Airlock
            var success = await _loginService.AuthenticateAndLoadProfileAsync(sanitizedName, securePassword);
            if (!success)
            {
                throw new InvalidOperationException("Vault generation failed during authentication airlock.");
            }

            // 7. Inject Base Currency into the newly encrypted vault
            await _userSettingsService.SetSettingAsync("BaseCurrency", SelectedCurrency);

            RunOnUIThread(async () =>
            {
                // 8. Show Master Key Modal and pause execution until they click Confirm
                var modalTcs = new TaskCompletionSource<bool>();
                _broker.Send(new ShowHelperMessage(
                    Title: "SAVE YOUR MASTER KEY",
                    Body: $"Your vault is encrypted. If you lose your password, this is the only way to delete the vault. Please copy this safely:\n\n{masterKey}",
                    IsCritical: true,
                    CompletionSource: modalTcs,
                    ShowCopyButton: true
                ));

                // Execution safely pauses right here on the UI thread
                await modalTcs.Task;

                // 9. Route to Main Dashboard
                _broker.Send(new NavigationMessage("MainDashboard"));
            });
        }
        catch (Exception ex)
        {
            RunOnUIThread(() =>
            {
                if (ex.Message.Contains("UNIQUE constraint failed"))
                {
                    ErrorMessage = "Profile name is already taken. Please choose a different name.";
                }
                else
                {
                    ErrorMessage = $"Failed to create profile. {ex.Message}";
                }
                Broker.Send(new ToastNotificationMessage("An unexpected error occurred during registration.", NotificationType.Error));

            });
        }
        finally
        {
            RunOnUIThread(() => IsLoading = false);
        }
    }

    public void ResetState()
    {
        // Ensure any sensitive data is cleared when the ViewModel is disposed
        ProfileName = string.Empty;
        Nickname = string.Empty;
        SelectedCurrency = "₹"; // Reset to default
        ErrorMessage = string.Empty;
    }

    private string GenerateMasterKey()
    {
        var bytes = new byte[18];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_");
    }

    [RelayCommand]
    private void NavigateToLogin()
    {
        ResetState(); // Clear sensitive data before navigating away
        _broker.Send(new NavigationMessage("Login"));
    }
}