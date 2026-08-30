using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

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
            TestResult = string.Empty;
            IsEditing = true;
            OnPropertyChanged(nameof(EditingTitle));
            OnPropertyChanged(nameof(ShowEmptyState));
        }

        [RelayCommand]
        public void BeginEdit(GitHubAccount account)
        {
            if (account == null)
                return;

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
            TestResult = string.Empty;
            IsEditing = true;
            OnPropertyChanged(nameof(EditingTitle));
            OnPropertyChanged(nameof(ShowEmptyState));
        }

        [RelayCommand]
        public void CancelEdit()
        {
            EditingAccount = null;
            IsEditing = false;
            NewToken = string.Empty;
            TestResult = string.Empty;
            OnPropertyChanged(nameof(ShowEmptyState));
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

            if (EditingAccount.AuthType == GitHubAuthType.PersonalAccessToken &&
                string.IsNullOrWhiteSpace(EditingAccount.Username))
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
                    else if (existing == null)
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
                var previousAuthType = existingAccount.AuthType;
                existingAccount.Name = EditingAccount.Name;
                existingAccount.Username = EditingAccount.Username;
                existingAccount.Email = EditingAccount.Email;
                existingAccount.AuthType = EditingAccount.AuthType;
                existingAccount.SSHKeyPath = EditingAccount.SSHKeyPath;
                existingAccount.IsDefault = EditingAccount.IsDefault;
                existingAccount.UpdatedAt = EditingAccount.UpdatedAt;

                if (EditingAccount.AuthType == GitHubAuthType.PersonalAccessToken &&
                    !string.IsNullOrWhiteSpace(NewToken))
                {
                    existingAccount.Token = NewToken;
                }
                else if (previousAuthType == GitHubAuthType.PersonalAccessToken &&
                         EditingAccount.AuthType != GitHubAuthType.PersonalAccessToken)
                {
                    existingAccount.DeleteCredentials();
                }
            }
            else
            {
                _preferences.AddGitHubAccount(EditingAccount);
                Accounts.Add(EditingAccount);
            }

            if (EditingAccount.IsDefault)
                _preferences.SetDefaultGitHubAccount(existingAccount ?? EditingAccount);

            _preferences.Save();
            OnPropertyChanged(nameof(ShowEmptyState));
            CancelEdit();
        }

        [RelayCommand]
        public void DeleteAccount(GitHubAccount account)
        {
            if (account == null)
                return;

            _preferences.RemoveGitHubAccount(account);
            Accounts.Remove(account);
            if (ReferenceEquals(SelectedAccount, account))
                SelectedAccount = null;
            OnPropertyChanged(nameof(ShowEmptyState));
        }

        [RelayCommand]
        public async Task TestConnectionAsync()
        {
            if (EditingAccount == null || IsTesting)
                return;

            IsTesting = true;
            TestResult = "Testing...";

            try
            {
                if (EditingAccount.AuthType == GitHubAuthType.SSHKey)
                    await TestSshConnectionAsync();
                else
                    await TestTokenConnectionAsync();
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
            if (account == null)
                return;

            _preferences.SetDefaultGitHubAccount(account);
        }

        public void SetEditingSshKeyPath(string path)
        {
            if (EditingAccount == null)
                return;

            EditingAccount.SSHKeyPath = path ?? string.Empty;
            TestResult = string.Empty;
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

                    OnPropertyChanged(nameof(ShowEmptyState));
                    IsDetecting = false;
                });
            });
        }

        private async Task TestTokenConnectionAsync()
        {
            var token = string.IsNullOrWhiteSpace(NewToken) ? EditingAccount.Token : NewToken;
            if (string.IsNullOrWhiteSpace(token))
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

        private async Task TestSshConnectionAsync()
        {
            var keyPath = EditingAccount.SSHKeyPath;
            if (string.IsNullOrWhiteSpace(keyPath) || !File.Exists(keyPath))
            {
                TestResult = "SSH key file does not exist";
                return;
            }

            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "ssh",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            proc.StartInfo.ArgumentList.Add("-T");
            proc.StartInfo.ArgumentList.Add("-o");
            proc.StartInfo.ArgumentList.Add("BatchMode=yes");
            proc.StartInfo.ArgumentList.Add("-o");
            proc.StartInfo.ArgumentList.Add("StrictHostKeyChecking=accept-new");
            proc.StartInfo.ArgumentList.Add("-o");
            proc.StartInfo.ArgumentList.Add("IdentitiesOnly=yes");
            proc.StartInfo.ArgumentList.Add("-i");
            proc.StartInfo.ArgumentList.Add(keyPath);
            proc.StartInfo.ArgumentList.Add("git@github.com");

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            var exitTask = proc.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(10))) != exitTask)
            {
                try
                {
                    proc.Kill(true);
                }
                catch
                {
                }

                TestResult = "✗ SSH test timed out";
                return;
            }

            await exitTask;
            var output = ((await stdoutTask) + "\n" + (await stderrTask)).Trim();

            if (output.Contains("successfully authenticated", StringComparison.OrdinalIgnoreCase))
            {
                var hi = output.IndexOf("Hi ", StringComparison.OrdinalIgnoreCase);
                var bang = hi >= 0 ? output.IndexOf('!', hi) : -1;
                if (hi >= 0 && bang > hi + 3)
                    EditingAccount.Username = output.Substring(hi + 3, bang - hi - 3).Trim();

                TestResult = string.IsNullOrWhiteSpace(EditingAccount.Username)
                    ? "✓ SSH authentication succeeded"
                    : $"✓ Connected as @{EditingAccount.Username}";
            }
            else
            {
                TestResult = string.IsNullOrWhiteSpace(output)
                    ? $"✗ SSH authentication failed (exit {proc.ExitCode})"
                    : $"✗ {output.Replace('\n', ' ').Replace('\r', ' ')}";
            }
        }
    }
}
