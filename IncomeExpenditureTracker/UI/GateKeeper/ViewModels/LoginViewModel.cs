using System;
using System.Security;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using System.Threading.Tasks;
using System.IO;
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
    private readonly IProfileRegistry _profileRegistry;
    private readonly IPasswordHasher _passwordHasher;

    // --- STATE: THE PROFILE GRID ---
    public ObservableCollection<ProfileDto> Profiles { get; } = new();

    // --- STATE: THE SELECTED PROFILE ---
    [ObservableProperty]
    private ProfileDto? _selectedProfile;

    [ObservableProperty]
    private string _username = string.Empty;

    // Toggle to switch between the Netflix Grid and the Password Prompt
    [ObservableProperty]
    private bool _isPasswordPromptVisible;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Add the attribute here so Avalonia knows to re-render ButtonText when IsLoading changes
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoginButtonText))]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isDeletePromptVisible;

    // The derived state (React equivalent: a calculated variable in your render function)
    public string LoginButtonText => IsLoading ? "Authenticating..." : "Login";

    // @desc    Constructor injects the global broker and the profile login service
    public LoginViewModel(IApplicationBroker broker, IProfileLoginService loginService, IProfileRegistry profileRegistry, IPasswordHasher passwordHasher)
            : base(broker)
    {
        _loginService = loginService;
        _profileRegistry = profileRegistry;
        _passwordHasher = passwordHasher;

        // Fire and forget the profile loading when the ViewModel is instantiated
        _ = LoadProfilesAsync();
    }

    // @desc    Fetches the profiles from system.db to populate the Netflix grid
    private async Task LoadProfilesAsync()
    {
        try
        {
            var profiles = await _profileRegistry.GetAllProfilesAsync();

            RunOnUIThread(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles)
                {
                    Profiles.Add(p);
                }
            });
        }
        catch (Exception ex)
        {
            // RunOnUIThread(() => ErrorMessage = "Failed to load profiles.");
            RunOnUIThread(() => ErrorMessage = $"DB Error: {ex.Message}");
            Broker.Send(new ToastNotificationMessage($"Error loading profiles: {ex.Message}", NotificationType.Error));
        }
    }

    // @desc    Triggered when a user clicks a profile card in the UI
    [RelayCommand]
    public void SelectProfile(ProfileDto profile)
    {
        SelectedProfile = profile;
        Username = profile.ProfileName;
        ErrorMessage = string.Empty;
        IsPasswordPromptVisible = true; // Flips the UI state to show the password box
    }

    // @desc    Allows the user to hit "Back" and select a different profile
    [RelayCommand]
    public void CancelSelection()
    {
        SelectedProfile = null;
        // WIPE THE USERNAME
        Username = string.Empty;
        ErrorMessage = string.Empty;
        IsPasswordPromptVisible = false; // Flips the UI state back to the grid
        IsDeletePromptVisible = false;
    }
    private void ResetState()
    {
        SelectedProfile = null;
        Username = string.Empty;
        ErrorMessage = string.Empty;
        IsPasswordPromptVisible = false;
        IsDeletePromptVisible = false;
    }

    // @desc    Executes the secure login flow using the Avalonia TextBox directly
    // @action  Accepts the UI control, extracts to SecureString, wipes the control
    [RelayCommand]
    public async Task LoginAsync(TextBox passwordBox)
    {
        ErrorMessage = string.Empty;

        var selectedProfile = SelectedProfile;

        if (string.IsNullOrWhiteSpace(Username) || selectedProfile == null || passwordBox == null || string.IsNullOrEmpty(passwordBox.Text))
        {
            ErrorMessage = "Please enter both username and password.";
            return;
        }

        IsLoading = true;
        using var securePassword = new SecureString();

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
            bool success = await _loginService.AuthenticateAndLoadProfileAsync(selectedProfile.ProfileName, securePassword);


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
        }
    }

    // @desc    Navigation trigger to switch to the Registration screen
    [RelayCommand]
    private void NavigateToRegister()
    {
        ResetState();
        // Emits a routing request to MainWindowViewModel
        Broker.Send(new NavigationMessage("Register"));
    }

    [RelayCommand]
    public void InitiateDelete()
    {
        // Flips the UI from the standard Login prompt to the Delete prompt
        ErrorMessage = "WARNING: This will permanently delete the encrypted vault.";
        IsDeletePromptVisible = true;
    }

    [RelayCommand]
    public void CancelDelete()
    {
        IsDeletePromptVisible = false;
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    public async Task ConfirmDeleteAsync(TextBox authBox)
    {
        ErrorMessage = string.Empty;

        if (SelectedProfile == null || authBox == null || string.IsNullOrWhiteSpace(authBox.Text))
        {
            ErrorMessage = "Authorization code required to delete profile.";
            return;
        }

        IsLoading = true;
        using var secureInput = new SecureString();

        try
        {
            // 1. SECURE BINDING: Extract to unmanaged memory and wipe UI
            foreach (char c in authBox.Text)
            {
                secureInput.AppendChar(c);
            }
            secureInput.MakeReadOnly();
            authBox.Text = string.Empty;

            // 2. Hash the input to verify against the registry
            // Note: We use the existing salts to see if the hash matches either slot
            var passTestHash = _passwordHasher.VerifyPassword(secureInput, SelectedProfile.PasswordHash, SelectedProfile.PasswordSalt);
            var masterKeyTestHash = _passwordHasher.VerifyPassword(secureInput, SelectedProfile.MasterKeyHash, SelectedProfile.MasterKeySalt);

            bool isAuthorized = passTestHash || masterKeyTestHash;

            if (!isAuthorized)
            {
                RunOnUIThread(() => ErrorMessage = "Invalid Password or Master Key. Deletion blocked.");
                return;
            }

            // 3. VAULT NUKE EXECUTION
            // Delete the physical encrypted .db file from the OS
            if (File.Exists(SelectedProfile.DatabaseFilePath))
            {
                File.Delete(SelectedProfile.DatabaseFilePath);
            }

            // Delete the routing entry from system.db
            await _profileRegistry.DeleteProfileAsync(SelectedProfile.ProfileId);

            RunOnUIThread(() =>
            {
                Broker.Send(new ToastNotificationMessage("Profile and encrypted vault permanently deleted.", NotificationType.Success));

                // Reset state and reload the grid
                SelectedProfile = null;
                IsPasswordPromptVisible = false;
                IsDeletePromptVisible = false;
            });

            await LoadProfilesAsync();
        }
        catch (Exception ex)
        {
            RunOnUIThread(() => ErrorMessage = "Failed to delete profile.");
            Broker.Send(new ToastNotificationMessage($"Deletion error: {ex.Message}", NotificationType.Error));
        }
        finally
        {
            RunOnUIThread(() => IsLoading = false);
        }
    }

    // @desc    Emergency/Developer tool to wipe all local data and schemas.
    [RelayCommand]
    public async Task FactoryResetAsync()
    {
        // 1. Double-check with the user using your global dialog router
        var tcs = new TaskCompletionSource<bool>();
        Broker.Send(new ShowConfirmationMessage(
            "FACTORY RESET",
            "WARNING: This will permanently delete ALL profiles, ALL encrypted vaults, and ALL settings from this computer. Are you sure?",
            tcs,
            "NUKE EVERYTHING",
            "Cancel"
        ));

        bool confirmed = await tcs.Task;
        if (!confirmed) return;

        IsLoading = true;

        try
        {
            // 2. RELEASE OS FILE LOCKS
            // This forces SQLite to immediately let go of system.db and any cached profile .db files.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            // 3. TARGET THE LOCAL APP DATA FOLDER
            var appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var appFolder = Path.Combine(appDataFolder, "IncomeExpenditureTracker");

            if (Directory.Exists(appFolder))
            {
                // Delete every single SQLite file in the directory
                var dbFiles = Directory.GetFiles(appFolder, "*.db");
                var walFiles = Directory.GetFiles(appFolder, "*.db-wal");
                var shmFiles = Directory.GetFiles(appFolder, "*.db-shm");

                foreach (var file in dbFiles) File.Delete(file);
                foreach (var file in walFiles) File.Delete(file); // Clean up Write-Ahead Logs
                foreach (var file in shmFiles) File.Delete(file); // Clean up Shared Memory files
            }

            // 4. RESET UI STATE
            RunOnUIThread(() =>
            {
                Profiles.Clear();
                SelectedProfile = null;
                IsPasswordPromptVisible = false;
                IsDeletePromptVisible = false;
            });

            Broker.Send(new ToastNotificationMessage("All data wiped successfully.", NotificationType.Success));
        }
        catch (Exception ex)
        {
            RunOnUIThread(() => ErrorMessage = $"Failed to reset: {ex.Message}");
        }
        finally
        {
            RunOnUIThread(() => IsLoading = false);
        }
    }
}