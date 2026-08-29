using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using SourceGit.Models;

namespace SourceGit.ViewModels
{
    public partial class Clone : Popup
    {
        [Required(ErrorMessage = "Remote URL is required")]
        [CustomValidation(typeof(Clone), nameof(ValidateRemote))]
        public string Remote
        {
            get => _remote;
            set
            {
                if (SetProperty(ref _remote, value, true))
                {
                    UseSSH = Models.Remote.IsSSH(value);
                    OnPropertyChanged(nameof(IsGitHubRemote));

                    // Pre-select the default account so private repos clone with proper credentials.
                    if (IsGitHubRemote && _selectedGitHubAccount == null)
                        SelectedGitHubAccount = Preferences.Instance.GetDefaultGitHubAccount();
                }
            }
        }

        public bool UseSSH
        {
            get => _useSSH;
            set => SetProperty(ref _useSSH, value);
        }

        public string SSHKey
        {
            get => _sshKey;
            set => SetProperty(ref _sshKey, value);
        }

        [Required(ErrorMessage = "Parent folder is required")]
        [CustomValidation(typeof(Clone), nameof(ValidateParentFolder))]
        public string ParentFolder
        {
            get => _parentFolder;
            set => SetProperty(ref _parentFolder, value, true);
        }

        public string Local
        {
            get => _local;
            set => SetProperty(ref _local, value);
        }

        public List<RepositoryNode> Groups
        {
            get;
        }

        public RepositoryNode SelectedGroup
        {
            get => _selectedGroup;
            set => SetProperty(ref _selectedGroup, value);
        }

        public int Bookmark
        {
            get => _bookmark;
            set => SetProperty(ref _bookmark, value);
        }

        public string ExtraArgs
        {
            get => _extraArgs;
            set => SetProperty(ref _extraArgs, value);
        }

        public Avalonia.Collections.AvaloniaList<Models.GitHubAccount> AvailableGitHubAccounts
        {
            get;
        } = Preferences.Instance.GitHubAccounts;

        public Models.GitHubAccount SelectedGitHubAccount
        {
            get => _selectedGitHubAccount;
            set
            {
                if (SetProperty(ref _selectedGitHubAccount, value))
                    AlignRemoteWithAccount();
            }
        }

        /// <summary>
        /// SSH keys can only authenticate the SSH protocol, tokens only HTTPS.
        /// Rewrites the github.com URL so its protocol matches the selected account.
        /// </summary>
        private void AlignRemoteWithAccount()
        {
            if (_selectedGitHubAccount == null || string.IsNullOrWhiteSpace(_remote))
                return;

            if (!_remote.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return;

            var toSsh = _selectedGitHubAccount.AuthType == GitHubAuthType.SSHKey;
            var converted = ConvertGitHubUrl(_remote, toSsh);
            if (!string.IsNullOrEmpty(converted) && converted != _remote)
                Remote = converted;
        }

        public static string ConvertGitHubUrl(string url, bool toSsh)
        {
            var trimmed = url.Trim().TrimEnd('/');
            if (toSsh)
            {
                var m = REG_GITHUB_HTTPS_URL().Match(trimmed);
                return m.Success ? $"git@github.com:{m.Groups[1].Value}.git" : null;
            }

            var s = REG_GITHUB_SSH_URL().Match(trimmed);
            return s.Success ? $"https://github.com/{s.Groups[1].Value}.git" : null;
        }

        [GeneratedRegex(@"^https?://github\.com/([\w\.\-]+/[\w\.\-]+?)(?:\.git)?$", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GITHUB_HTTPS_URL();

        [GeneratedRegex(@"^git@github\.com:([\w\.\-]+/[\w\.\-]+?)(?:\.git)?$", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GITHUB_SSH_URL();

        public bool IsGitHubRemote => !string.IsNullOrEmpty(_remote) && _remote.Contains("github.com", StringComparison.Ordinal);

        public bool InitAndUpdateSubmodules
        {
            get;
            set;
        } = true;

        public Clone(string pageId)
        {
            _pageId = pageId;

            Groups = new List<RepositoryNode>();
            Groups.Add(new RepositoryNode { Name = "No Group (Uncategorized)", Id = string.Empty });
            SelectedGroup = Groups[0];
            CollectGroups(Groups, Preferences.Instance.RepositoryNodes);

            var activeWorkspace = Preferences.Instance.GetActiveWorkspace();
            _parentFolder = activeWorkspace?.DefaultCloneDir;
            if (string.IsNullOrEmpty(ParentFolder))
                _parentFolder = Preferences.Instance.GitDefaultCloneDir;
        }

        public static ValidationResult ValidateRemote(string remote, ValidationContext _)
        {
            if (!Models.Remote.IsValidURL(remote))
                return new ValidationResult("Invalid remote repository URL format");
            return ValidationResult.Success;
        }

        public static ValidationResult ValidateParentFolder(string folder, ValidationContext _)
        {
            if (!Directory.Exists(folder))
                return new ValidationResult("Given path can NOT be found");
            return ValidationResult.Success;
        }

        public override async Task<bool> Sure()
        {
            ProgressDescription = "Clone ...";

            // Normalize parent folder: strip trailing slashes / whitespace so
            // Process.Start never receives a malformed working directory.
            try
            {
                _parentFolder = Path.GetFullPath(_parentFolder.Trim().TrimEnd('/', '\\'));
            }
            catch
            {
                // Keep original value; validation should have caught invalid paths.
            }

            // Final protocol alignment: SSH accounts must clone over SSH, token accounts over HTTPS.
            AlignRemoteWithAccount();

            var log = new CommandLog("Clone");
            Use(log);

            var succ = await new Commands.Clone(_pageId, _parentFolder, _remote, _local, _useSSH ? _sshKey : "", _extraArgs, _selectedGitHubAccount)
                .Use(log)
                .ExecAsync();
            if (!succ)
                return false;

            var path = _parentFolder;
            if (!string.IsNullOrEmpty(_local))
            {
                path = Path.GetFullPath(Path.Combine(path, _local));
            }
            else
            {
                var name = Path.GetFileName(_remote)!;
                if (name.EndsWith(".git", StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - 4);
                else if (name.EndsWith(".bundle", StringComparison.Ordinal))
                    name = name.Substring(0, name.Length - 7);

                path = Path.GetFullPath(Path.Combine(path, name));
            }

            if (!Directory.Exists(path))
            {
                Models.Notification.Send(_pageId, $"Folder '{path}' can NOT be found", true);
                return false;
            }

            if (_useSSH && !string.IsNullOrEmpty(_sshKey))
            {
                await new Commands.Config(path)
                    .Use(log)
                    .SetAsync("remote.origin.sshkey", _sshKey);
            }

            if (InitAndUpdateSubmodules)
            {
                var submodules = await new Commands.QueryUpdatableSubmodules(path, true).GetResultAsync();
                if (submodules.Count > 0)
                    await new Commands.Submodule(path)
                        .Use(log)
                        .UpdateAsync(submodules, true, true, false);
            }

            log.Complete();

            // Bind GitHub account if selected
            if (_selectedGitHubAccount != null)
            {
                var settings = Models.RepositorySettings.Get(Path.Combine(path, ".git"));
                settings.GitHubAccountId = _selectedGitHubAccount.Id;
                settings.Save();
            }

            var parent = _selectedGroup is { Id: not "" } ? _selectedGroup : null;
            var node = Preferences.Instance.FindOrAddNodeByRepositoryPath(path, parent, true);
            node.Bookmark = _bookmark;
            await node.UpdateStatusAsync(false, null);

            var launcher = App.GetLauncher();
            LauncherPage page = null;
            foreach (var one in launcher.Pages)
            {
                if (one.Node.Id == _pageId)
                {
                    page = one;
                    break;
                }
            }

            Welcome.Instance.Refresh();
            launcher.OpenRepositoryInTab(node, page);
            return true;
        }

        private void CollectGroups(List<RepositoryNode> outs, List<RepositoryNode> collections)
        {
            foreach (var node in collections)
            {
                if (!node.IsRepository)
                {
                    outs.Add(node);
                    CollectGroups(outs, node.SubNodes);
                }
            }
        }

        private string _pageId = string.Empty;
        private string _remote = string.Empty;
        private bool _useSSH = false;
        private string _sshKey = string.Empty;
        private string _parentFolder = string.Empty;
        private string _local = string.Empty;
        private string _extraArgs = string.Empty;
        private RepositoryNode _selectedGroup = null;
        private int _bookmark = 0;
        private Models.GitHubAccount _selectedGitHubAccount = null;
    }
}
