using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using SsmsChatExtension.Services.Claude;

namespace SsmsChatExtension.UI
{
    /// <summary>Sign-in state and settings for the Claude integration, shown at the top of the chat window.</summary>
    public sealed class ClaudeAccountViewModel : INotifyPropertyChanged
    {
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

        private bool _isSignedIn;
        private bool _isCliFound = true;
        private bool _isWorking;
        private bool _isSettingsOpen;
        private string _accountText = "Checking Claude sign-in...";

        private string _claudePath;
        private string _repositoryFolder;
        private string _permissionMode;
        private string _effort;
        private DatabaseChangeMode _databaseChanges;
        private string _model;
        private string _connectionString;
        private string _settingsStatus = string.Empty;

        public ClaudeAccountViewModel()
        {
            LoadSettings();

            SignInCommand = new RelayCommand(async _ => await SignInAsync(), _ => !IsWorking && IsCliFound);
            SignOutCommand = new RelayCommand(async _ => await SignOutAsync(), _ => !IsWorking && IsSignedIn);
            RefreshCommand = new RelayCommand(async _ => await RefreshAsync(), _ => !IsWorking);
            ToggleSettingsCommand = new RelayCommand(_ => { IsSettingsOpen = !IsSettingsOpen; if (IsSettingsOpen) LoadSettings(); });
            SaveSettingsCommand = new RelayCommand(async _ => await SaveSettingsAsync());
            BrowseRepositoryCommand = new RelayCommand(_ => BrowseRepository());
            OpenRepositoryCommand = new RelayCommand(_ => OpenRepository());
        }

        /// <summary>Raised after settings were saved (the model name in the header may have changed).</summary>
        public event EventHandler SettingsSaved;

        public ICommand SignInCommand { get; }
        public ICommand SignOutCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand ToggleSettingsCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand BrowseRepositoryCommand { get; }
        public ICommand OpenRepositoryCommand { get; }

        public bool IsSignedIn { get { return _isSignedIn; } private set { Set(ref _isSignedIn, value); } }
        public bool IsCliFound { get { return _isCliFound; } private set { Set(ref _isCliFound, value); } }
        public bool IsWorking { get { return _isWorking; } private set { Set(ref _isWorking, value); } }
        public bool IsSettingsOpen { get { return _isSettingsOpen; } set { Set(ref _isSettingsOpen, value); } }
        public string AccountText { get { return _accountText; } private set { Set(ref _accountText, value); } }

        public string ClaudePath { get { return _claudePath; } set { Set(ref _claudePath, value); } }
        public string RepositoryFolder { get { return _repositoryFolder; } set { Set(ref _repositoryFolder, value); } }
        /// <summary>Model aliases offered in the model box; any other model id can be typed.</summary>
        public IReadOnlyList<string> ModelOptions { get; } = new[] { string.Empty, "opus", "sonnet", "haiku", "fable" };

        /// <summary>Effort levels for --effort; empty = the model's default.</summary>
        public IReadOnlyList<string> EffortOptions { get; } = new[] { string.Empty, "low", "medium", "high", "xhigh", "max" };

        public string Effort { get { return _effort; } set { Set(ref _effort, value ?? string.Empty); } }

        public string PermissionMode
        {
            get { return _permissionMode; }
            set
            {
                if (_permissionMode == value) return;
                _permissionMode = value;
                OnPropertyChanged(nameof(PermissionMode));
                OnPropertyChanged(nameof(PermissionAcceptEdits));
                OnPropertyChanged(nameof(PermissionAuto));
                OnPropertyChanged(nameof(PermissionManual));
            }
        }

        // One bool per radio button.
        public bool PermissionAcceptEdits { get { return PermissionMode == "acceptEdits"; } set { if (value) PermissionMode = "acceptEdits"; } }
        public bool PermissionAuto { get { return PermissionMode == "auto"; } set { if (value) PermissionMode = "auto"; } }
        public bool PermissionManual { get { return PermissionMode == "default"; } set { if (value) PermissionMode = "default"; } }

        public DatabaseChangeMode DatabaseChanges
        {
            get { return _databaseChanges; }
            set
            {
                if (_databaseChanges == value) return;
                _databaseChanges = value;
                OnPropertyChanged(nameof(DatabaseChanges));
                OnPropertyChanged(nameof(DatabaseChangesOff));
                OnPropertyChanged(nameof(DatabaseChangesAsk));
                OnPropertyChanged(nameof(DatabaseChangesAllow));
            }
        }

        // One bool per radio button.
        public bool DatabaseChangesOff { get { return DatabaseChanges == DatabaseChangeMode.Off; } set { if (value) DatabaseChanges = DatabaseChangeMode.Off; } }
        public bool DatabaseChangesAsk { get { return DatabaseChanges == DatabaseChangeMode.Ask; } set { if (value) DatabaseChanges = DatabaseChangeMode.Ask; } }
        public bool DatabaseChangesAllow { get { return DatabaseChanges == DatabaseChangeMode.Allow; } set { if (value) DatabaseChanges = DatabaseChangeMode.Allow; } }
        public string Model { get { return _model; } set { Set(ref _model, value); } }
        public string ConnectionString { get { return _connectionString; } set { Set(ref _connectionString, value); } }
        public string SettingsStatus { get { return _settingsStatus; } private set { Set(ref _settingsStatus, value); } }

        public async Task RefreshAsync()
        {
            IsWorking = true;
            try
            {
                await UpdateStatusAsync();
            }
            finally
            {
                IsWorking = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task<ClaudeAuthStatus> UpdateStatusAsync()
        {
            var cliPath = ClaudeCli.Locate(ClaudeSettings.Load().ClaudePath);
            ClaudeAuthStatus status;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    status = await ClaudeCli.GetAuthStatusAsync(cliPath, timeout.Token);
                }
            }
            catch (OperationCanceledException)
            {
                status = new ClaudeAuthStatus(cliPath != null, false, "Timed out checking sign-in.");
            }

            IsCliFound = status.CliFound;
            IsSignedIn = status.LoggedIn;
            if (!status.CliFound)
            {
                AccountText = "Claude Code CLI not found - install it, or set its path in Settings.";
            }
            else if (status.LoggedIn)
            {
                AccountText = "Signed in to Claude" + (status.Detail.Length > 0 ? " as " + status.Detail : string.Empty);
            }
            else
            {
                AccountText = "Not signed in to Claude.";
            }
            return status;
        }

        private async Task SignInAsync()
        {
            var cliPath = ClaudeCli.Locate(ClaudeSettings.Load().ClaudePath);
            if (cliPath == null)
            {
                await RefreshAsync();
                return;
            }

            IsWorking = true;
            try
            {
                AccountText = "Finish signing in in the browser / console window that just opened...";
                using (var console = ClaudeCli.StartLogin(cliPath))
                {
                    // Poll until signed in, the console is closed, or we give up.
                    var started = DateTime.UtcNow;
                    while (DateTime.UtcNow - started < LoginTimeout)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(3));
                        var status = await UpdateStatusAsync();
                        if (status.LoggedIn || console == null || console.HasExited) break;
                        AccountText = "Waiting for Claude sign-in to finish...";
                    }
                }
                await UpdateStatusAsync();
            }
            catch (Exception ex)
            {
                AccountText = "Sign-in failed: " + ex.Message;
            }
            finally
            {
                IsWorking = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private async Task SignOutAsync()
        {
            var cliPath = ClaudeCli.Locate(ClaudeSettings.Load().ClaudePath);
            if (cliPath == null) return;

            IsWorking = true;
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await ClaudeCli.LogoutAsync(cliPath, timeout.Token);
                }
                await UpdateStatusAsync();
            }
            catch (Exception ex)
            {
                AccountText = "Sign-out failed: " + ex.Message;
            }
            finally
            {
                IsWorking = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void LoadSettings()
        {
            var settings = ClaudeSettings.Load();
            ClaudePath = settings.ClaudePath;
            RepositoryFolder = settings.RepositoryFolder;
            PermissionMode = settings.EffectivePermissionMode;
            Effort = settings.Effort;
            DatabaseChanges = settings.DatabaseChanges;
            Model = settings.Model;
            ConnectionString = settings.ConnectionStringOverride;
            SettingsStatus = string.Empty;
        }

        private async Task SaveSettingsAsync()
        {
            var path = (ClaudePath ?? string.Empty).Trim().Trim('"');
            var folder = (RepositoryFolder ?? string.Empty).Trim().Trim('"');

            if (path.Length > 0 && !File.Exists(path))
            {
                SettingsStatus = "Claude CLI path does not exist.";
                return;
            }
            if (folder.Length > 0 && !Directory.Exists(folder))
            {
                SettingsStatus = "Repository folder does not exist.";
                return;
            }

            var connectionString = (ConnectionString ?? string.Empty).Trim();
            if (connectionString.Length > 0)
            {
                try
                {
                    new System.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                }
                catch (Exception ex)
                {
                    SettingsStatus = "Invalid connection string: " + ex.Message;
                    return;
                }
            }

            var settings = ClaudeSettings.Load();
            settings.ClaudePath = path;
            settings.RepositoryFolder = folder;
            settings.ClaudePermissionMode = PermissionMode;
            settings.AllowFileEdits = PermissionMode != "default";
            settings.Effort = (Effort ?? string.Empty).Trim();
            settings.DatabaseChanges = DatabaseChanges;
            settings.Model = (Model ?? string.Empty).Trim();
            settings.ConnectionStringOverride = connectionString;

            try
            {
                settings.Save();
            }
            catch (Exception ex)
            {
                SettingsStatus = "Could not save settings: " + ex.Message;
                return;
            }

            SettingsStatus = "Saved.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            await RefreshAsync();
        }

        private void BrowseRepository()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Folder Claude may read and edit (your SQL scripts repository)",
                SelectedPath = Directory.Exists(RepositoryFolder ?? string.Empty) ? RepositoryFolder : string.Empty
            })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    RepositoryFolder = dialog.SelectedPath;
                }
            }
        }

        private void OpenRepository()
        {
            var folder = Directory.Exists(RepositoryFolder ?? string.Empty) ? RepositoryFolder : ClaudeSettings.ScratchFolder;
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", "\"" + folder + "\"");
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
