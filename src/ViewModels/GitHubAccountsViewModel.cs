using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

using Avalonia.Collections;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using SourceGit.Models;

namespace SourceGit.ViewModels
{
    public partial class GitHubAccountsViewModel : ObservableObject
    {
        private readonly Preferences _preferences;

        public GitHubAccountsViewModel(Preferences preferences)
        {
            _preferences = preferences;
            Accounts = new AvaloniaList<GitHubAccount>(_preferences.GitHubAccounts);
        }

        public AvaloniaList<GitHubAccount> Accounts { get; }

        [ObservableProperty]
        private GitHubAccount _selectedAccount;

        [ObservableProperty]
        private bool _isEditing = false;

        [ObservableProperty]
        private GitHubAccount _editingAccount;

        [ObservableProperty]
        private string _newToken = string.Empty;

        [ObservableProperty]
        private string _testResult = string.Empty;

        [ObservableProperty]
        private bool _isTesting = false;

        [ObservableProperty]
        private bool _isDetecting = false;

        [ObservableProperty]
        private string _detectResult = string.Empty;

        public bool ShowEmptyState => Accounts.Count == 0 && !IsEditing;

        public string EditingTitle
        {
            get
            {
                var isNew = EditingAccount == null ||
                    _preferences.GetGitHubAccount(EditingAccount.Id) == null;
                return isNew ? "Add Account" : "Edit Account";
            }
        }

        [RelayCommand]
        public void BeginAdd()
        {
            EditingAccount = new GitHubAccount
            {
                AuthType = GitHubAuthType.PersonalAccessToken,
                IsDefault = Accounts.Count == 0,
            };
            NewToken = string.Empty;
            IsEditing = true;
            OnPropertyChanged(nameof(EditingTitle));
        }

        [RelayCommand]
        public void BeginEdit(GitHubAccount account)
        {
            EditingAccount = new GitHubAccount
            {
                Id = account.Id,
                Name = account.Name,
                Username = account.Username,
                Email = account.Email,
                AuthType = account.AuthType,
                SSHKeyPath = account.SSHKeyPath,
                IsDefault = account.IsDefault,
                CreatedAt = account.CreatedAt,
                UpdatedAt = DateTime.Now,
            };
            NewToken = string.Empty;
            IsEditing = true;
            OnPropertyChanged(nameof(EditingTitle));
        }

        [RelayCommand]
        public void CancelEdit()
        {
            EditingAccount = null;
            IsEditing = false;
            NewToken = string.Empty;
            TestResult = string.Empty;
        }

        [RelayCommand]
        public void SaveEdit()
        {
            if (EditingAccount == null)
                return;

            if (string.IsNullOrWhiteSpace(EditingAccount.Name))
            {
                TestResult = "Name is required";
                return;
            }

            if (string.IsNullOrWhiteSpace(EditingAccount.Username))
            {
                TestResult = "Username is required";
                return;
            }

            if (EditingAccount.AuthType == GitHubAuthType.PersonalAccessToken)
            {
                if (string.IsNullOrWhiteSpace(NewToken))
                {
                    var existing = _preferences.GetGitHubAccount(EditingAccount.Id);
                    if (existing != null && string.IsNullOrWhiteSpace(existing.Token))
                    {
                        TestResult = "Token is required";
                        return;
                    }
                }
                else
                {
                    EditingAccount.Token = NewToken;
                }
            }
            else if (EditingAccount.AuthType == GitHubAuthType.SSHKey)
            {
                if (string.IsNullOrWhiteSpace(EditingAccount.SSHKeyPath))
                {
                    TestResult = "SSH key path is required";
                    return;
                }
            }

            EditingAccount.UpdatedAt = DateTime.Now;

            var existingAccount = _preferences.GetGitHubAccount(EditingAccount.Id);
            if (existingAccount != null)
            {
                existingAccount.Name = EditingAccount.Name;
                existingAccount.Username = EditingAccount.Username;
                existingAccount.Email = EditingAccount.Email;
                existingAccount.AuthType = EditingAccount.AuthType;
                existingAccount.SSHKeyPath = EditingAccount.SSHKeyPath;
                existingAccount.IsDefault = EditingAccount.IsDefault;
                existingAccount.UpdatedAt = EditingAccount.UpdatedAt;

                if (!string.IsNullOrWhiteSpace(NewToken))
                    existingAccount.Token = NewToken;
            }
            else
            {
                _preferences.AddGitHubAccount(EditingAccount);
                Accounts.Add(EditingAccount);
            }

            if (EditingAccount.IsDefault)
                _preferences.SetDefaultGitHubAccount(EditingAccount);

            _preferences.Save();
            CancelEdit();
        }

        [RelayCommand]
        public void DeleteAccount(GitHubAccount account)
        {
            _preferences.RemoveGitHubAccount(account);
            Accounts.Remove(account);
        }

        [RelayCommand]
        public async Task TestConnectionAsync()
        {
            if (EditingAccount == null)
                return;

            IsTesting = true;
            TestResult = "Testing...";

            try
            {
                var token = EditingAccount.Token;
                if (string.IsNullOrEmpty(token))
                {
                    TestResult = "No token configured";
                    return;
                }

                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SourceGit");
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var response = await http.GetAsync("https://api.github.com/user");
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(json);
                    var login = doc.RootElement.GetProperty("login").GetString();
                    var avatar = doc.RootElement.GetProperty("avatar_url").GetString();
                    EditingAccount.Username = login ?? EditingAccount.Username;
                    EditingAccount.AvatarUrl = avatar ?? string.Empty;
                    TestResult = $"✓ Connected as @{login}";
                }
                else
                {
                    TestResult = $"✗ Failed: {(int)response.StatusCode} {response.ReasonPhrase}";
                }
            }
            catch (Exception ex)
            {
                TestResult = $"✗ Error: {ex.Message}";
            }
            finally
            {
                IsTesting = false;
            }
        }

        [RelayCommand]
        public void SetAsDefault(GitHubAccount account)
        {
            _preferences.SetDefaultGitHubAccount(account);
        }

        [RelayCommand]
        public void BrowseSSHKey()
        {
            // This would need a file dialog - for now just a placeholder
            // In a real implementation, you'd use IFileDialog or similar
        }

        [RelayCommand]
        public void AutoDetectFromGit()
        {
            if (IsDetecting)
                return;

            IsDetecting = true;
            DetectResult = "Scanning local git credential stores...";

            Task.Run(() =>
            {
                List<GitHubAccount> imported = [];
                var skipped = 0;

                try
                {
                    var found = Services.GitHubCredential.ScanLocalStores();
                    foreach (var entry in found)
                    {
                        var isSsh = entry.Source == "ssh";
                        if (!isSsh && string.IsNullOrWhiteSpace(entry.Username))
                        {
                            skipped++;
                            continue;
                        }

                        var exists = isSsh
                            ? Accounts.Any(a => a.AuthType == GitHubAuthType.SSHKey &&
                                                string.Equals(a.SSHKeyPath, entry.Secret, StringComparison.OrdinalIgnoreCase))
                            : Accounts.Any(a =>
                                string.Equals(a.Username, entry.Username, StringComparison.OrdinalIgnoreCase));
                        if (exists)
                        {
                            skipped++;
                            continue;
                        }

                        var account = new GitHubAccount
                        {
                            Name = isSsh
                                ? (string.IsNullOrEmpty(entry.Alias) ? "SSH Key" : entry.Alias)
                                : entry.Username,
                            Username = entry.Username,
                            AuthType = isSsh ? GitHubAuthType.SSHKey : GitHubAuthType.PersonalAccessToken,
                            IsDefault = _preferences.GitHubAccounts.Count == 0 && imported.Count == 0,
                        };

                        if (isSsh)
                            account.SSHKeyPath = entry.Secret;
                        else
                            account.Token = entry.Secret;

                        _preferences.AddGitHubAccount(account);
                        imported.Add(account);
                    }
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        DetectResult = $"✗ Scan failed: {ex.Message}";
                        IsDetecting = false;
                    });
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var acc in imported)
                        Accounts.Add(acc);

                    if (imported.Count > 0)
                    {
                        var bySource = imported.GroupBy(a =>
                            a.AuthType == GitHubAuthType.SSHKey ? "ssh" : "token")
                            .Select(g => $"{g.Key}: {g.Count()}");
                        DetectResult = $"✓ Imported {imported.Count} account(s) ({string.Join(", ", bySource)})" +
                            (skipped > 0 ? $" — {skipped} already exist" : "");
                    }
                    else
                    {
                        DetectResult = "No new accounts found (scanned: credential vault, ~/.git-credentials, gh CLI, ~/.ssh/config)";
                    }

                    IsDetecting = false;
                });
            });
        }
    }
}