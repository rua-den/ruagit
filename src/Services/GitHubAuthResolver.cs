using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace SourceGit.Services
{
    /// <summary>
    /// Resolves repository-to-account bindings while keeping the reason for the
    /// decision inspectable. Explicit user bindings always win. Automatic rules
    /// and the effective SSH identity are evaluated before the legacy GitHub
    /// remote/account heuristic.
    /// </summary>
    public static class GitHubAuthResolver
    {
        public sealed class Resolution
        {
            public Models.GitHubAccount Account { get; init; }
            public string Source { get; init; } = "Unresolved";
            public string Reason { get; init; } = string.Empty;
            public string Remote { get; init; } = string.Empty;
            public string Rule { get; init; } = string.Empty;

            public bool IsResolved => Account != null;

            public string Display
            {
                get
                {
                    if (Account == null)
                        return string.IsNullOrWhiteSpace(Reason) ? "Unresolved" : Reason;

                    var auth = Account.AuthType == Models.GitHubAuthType.SSHKey ? "SSH" : "PAT";
                    var parts = new List<string> { Account.DisplayName, auth, Source };
                    if (!string.IsNullOrWhiteSpace(Rule))
                        parts.Add(Rule);
                    else if (!string.IsNullOrWhiteSpace(Reason) && !Reason.Equals(Source, StringComparison.OrdinalIgnoreCase))
                        parts.Add(Reason);
                    if (!string.IsNullOrWhiteSpace(Remote))
                        parts.Add(Remote);
                    return string.Join(" · ", parts);
                }
            }
        }

        public static Resolution InspectRepository(string repoPath)
        {
            if (!TryGetSettings(repoPath, out var settings))
                return new Resolution { Reason = "Repository settings are unavailable" };

            if (settings.GitHubAccountId == Guid.Empty)
            {
                return new Resolution
                {
                    Source = "Unresolved",
                    Reason = string.IsNullOrWhiteSpace(settings.GitHubAccountBindingReason)
                        ? "Not bound yet — auto-detect or choose an account"
                        : settings.GitHubAccountBindingReason,
                    Remote = settings.GitHubAccountBindingRemote,
                };
            }

            var account = ViewModels.Preferences.Instance.GetGitHubAccount(settings.GitHubAccountId);
            if (account == null)
            {
                return new Resolution
                {
                    Source = "Stale binding",
                    Reason = "Stored account no longer exists",
                    Remote = settings.GitHubAccountBindingRemote,
                };
            }

            var source = settings.GitHubAccountIsExplicit switch
            {
                true => "Manual",
                false when settings.GitHubAccountBindingReason.StartsWith("Rule ", StringComparison.OrdinalIgnoreCase) => "Rule",
                false when settings.GitHubAccountBindingReason.StartsWith("SSH config ", StringComparison.OrdinalIgnoreCase) => "SSH config",
                false => "Auto-detected",
                _ => "Legacy binding",
            };

            return new Resolution
            {
                Account = account,
                Source = source,
                Reason = settings.GitHubAccountBindingReason,
                Remote = settings.GitHubAccountBindingRemote,
                Rule = source == "Rule" ? settings.GitHubAccountBindingReason.Substring(5) : string.Empty,
            };
        }

        public static bool BindManual(string repoPath, Models.GitHubAccount account)
        {
            if (!TryGetSettings(repoPath, out var settings))
                return false;

            settings.GitHubAccountId = account?.Id ?? Guid.Empty;
            settings.GitHubAccountIsExplicit = account != null;
            settings.GitHubAccountBindingReason = account == null ? "Binding cleared manually" : "Manual binding";
            settings.GitHubAccountBindingRemote = string.Empty;
            settings.Save();
            return true;
        }

        public static async Task<Resolution> ResolveForRepositoryAsync(string repoPath)
        {
            if (!TryGetSettings(repoPath, out var settings))
                return new Resolution { Reason = "Repository settings are unavailable" };

            var pref = ViewModels.Preferences.Instance;
            if (pref.GitHubAccounts.Count == 0)
            {
                SaveUnresolved(settings, "No GitHub accounts configured", string.Empty);
                return InspectRepository(repoPath);
            }

            var current = settings.GitHubAccountId == Guid.Empty
                ? null
                : pref.GetGitHubAccount(settings.GitHubAccountId);

            // Only a binding explicitly selected in the provenance-aware UI is
            // immutable. Legacy bindings are allowed to migrate when a stronger
            // rule/SSH signal proves which identity the remote actually uses.
            if (current != null && settings.GitHubAccountIsExplicit == true)
                return InspectRepository(repoPath);

            List<Models.Remote> remotes;
            try
            {
                remotes = await new Commands.QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);
            }
            catch
            {
                remotes = [];
            }

            var preferredRemote = SelectPreferredRemote(settings, remotes);

            // 1. Explicit account rules are the strongest automatic signal.
            var matches = FindRuleMatches(repoPath, pref.GitHubAccounts, remotes, preferredRemote);
            var accounts = matches
                .GroupBy(x => x.Account.Id)
                .Select(g => g.First())
                .ToList();

            if (accounts.Count == 1)
            {
                var match = accounts[0];
                SaveAutomaticBinding(settings, match.Account, $"Rule {match.Rule}", FormatRemote(match.Remote));
                return InspectRepository(repoPath);
            }

            if (accounts.Count > 1)
            {
                var names = string.Join(", ", accounts.Select(x => x.Account.DisplayName));
                SaveUnresolved(settings, $"Ambiguous rules matched multiple accounts: {names}", FormatRemote(preferredRemote));
                return InspectRepository(repoPath);
            }

            // 2. For SSH remotes, ~/.ssh/config is authoritative about the key that
            // OpenSSH would use. This fixes cases where a global/legacy GitHub account
            // was selected even though the remote host alias points at another key.
            var sshMatches = FindSshConfigMatches(pref.GitHubAccounts, remotes);
            var sshAccounts = sshMatches
                .GroupBy(x => x.Account.Id)
                .Select(g => g.First())
                .ToList();

            if (sshAccounts.Count == 1)
            {
                var match = sshAccounts[0];
                SaveAutomaticBinding(settings, match.Account, $"SSH config IdentityFile {match.IdentityFile}", FormatRemote(match.Remote));
                return InspectRepository(repoPath);
            }

            if (sshAccounts.Count > 1)
            {
                var names = string.Join(", ", sshAccounts.Select(x => x.Account.DisplayName));
                SaveUnresolved(settings, $"SSH config matched multiple accounts: {names}", FormatRemote(preferredRemote));
                return InspectRepository(repoPath);
            }

            // A legacy binding may have been a deliberate manual selection before we
            // tracked provenance. Preserve it only when it is compatible with the
            // repository's current remote protocol. An HTTPS account must not block an
            // SSH-key account (and vice versa) just because it was stored first.
            if (current != null && settings.GitHubAccountIsExplicit == null &&
                (preferredRemote == null || IsCompatible(current, preferredRemote)))
            {
                return InspectRepository(repoPath);
            }

            // Clear an old automatic/incompatible legacy binding before invoking the
            // existing heuristic; otherwise GitHubCredential would immediately return
            // the stored account without evaluating the remote again.
            if (settings.GitHubAccountIsExplicit != true)
            {
                settings.GitHubAccountId = Guid.Empty;
                settings.Save();
            }

            // 3. Fall back to the original deterministic owner/protocol heuristic.
            var detected = await GitHubCredential.DetectForRepositoryAsync(repoPath).ConfigureAwait(false);
            if (detected != null)
            {
                SaveAutomaticBinding(settings, detected, "GitHub remote/account heuristic", FormatRemote(preferredRemote));
                return InspectRepository(repoPath);
            }

            SaveUnresolved(settings, "No matching auth rule, SSH identity, or deterministic GitHub account", FormatRemote(preferredRemote));
            return InspectRepository(repoPath);
        }

        public static async Task WarmupRepositoryBindingsAsync(IEnumerable<ViewModels.RepositoryNode> nodes)
        {
            if (nodes == null || ViewModels.Preferences.Instance.GitHubAccounts.Count == 0)
                return;

            foreach (var node in nodes)
            {
                if (node == null)
                    continue;

                if (node.IsRepository)
                {
                    if (Directory.Exists(node.Id))
                    {
                        var resolution = await ResolveForRepositoryAsync(node.Id).ConfigureAwait(false);
                        if (!ReferenceEquals(node.BoundGitHubAccount, resolution.Account))
                        {
                            if (Dispatcher.UIThread.CheckAccess())
                                node.BoundGitHubAccount = resolution.Account;
                            else
                                Dispatcher.UIThread.Post(() => node.BoundGitHubAccount = resolution.Account);
                        }
                    }
                }
                else if (node.SubNodes.Count > 0)
                {
                    await WarmupRepositoryBindingsAsync(node.SubNodes).ConfigureAwait(false);
                }
            }
        }

        private sealed class RuleMatch
        {
            public Models.GitHubAccount Account { get; init; }
            public string Rule { get; init; } = string.Empty;
            public Models.Remote Remote { get; init; }
        }

        private sealed class SshIdentityMatch
        {
            public Models.GitHubAccount Account { get; init; }
            public Models.Remote Remote { get; init; }
            public string IdentityFile { get; init; } = string.Empty;
        }

        private sealed class SshConfigBlock
        {
            public List<string> HostPatterns { get; } = [];
            public string HostName { get; set; } = string.Empty;
            public List<string> IdentityFiles { get; } = [];
        }

        private static List<RuleMatch> FindRuleMatches(
            string repoPath,
            IEnumerable<Models.GitHubAccount> accounts,
            List<Models.Remote> remotes,
            Models.Remote preferredRemote)
        {
            var matches = new List<RuleMatch>();
            var normalizedPath = NormalizePath(repoPath);

            foreach (var account in accounts)
            {
                if (!account.HasValidCredentials || string.IsNullOrWhiteSpace(account.MatchRules))
                    continue;

                var rules = account.MatchRules.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var rawRule in rules)
                {
                    if (string.IsNullOrWhiteSpace(rawRule) || rawRule.StartsWith('#'))
                        continue;

                    var separator = rawRule.IndexOf(':');
                    var kind = separator > 0 ? rawRule[..separator].Trim().ToLowerInvariant() : "remote";
                    var pattern = separator > 0 ? rawRule[(separator + 1)..].Trim() : rawRule.Trim();
                    if (string.IsNullOrWhiteSpace(pattern))
                        continue;

                    if (kind == "path")
                    {
                        var expanded = NormalizePath(ExpandHome(pattern));
                        if (WildcardMatch(normalizedPath, expanded) && IsCompatible(account, preferredRemote))
                        {
                            matches.Add(new RuleMatch { Account = account, Rule = rawRule, Remote = preferredRemote });
                            break;
                        }
                    }
                    else if (kind == "owner")
                    {
                        foreach (var remote in remotes)
                        {
                            var owner = GitHubCredential.ExtractGitHubOwner(remote.URL);
                            if (!string.IsNullOrWhiteSpace(owner) && WildcardMatch(owner, pattern) && IsCompatible(account, remote))
                            {
                                matches.Add(new RuleMatch { Account = account, Rule = rawRule, Remote = remote });
                                break;
                            }
                        }

                        if (matches.Count > 0 && matches[^1].Account.Id == account.Id)
                            break;
                    }
                    else if (kind == "remote")
                    {
                        foreach (var remote in remotes)
                        {
                            if (WildcardMatch(remote.URL, pattern) && IsCompatible(account, remote))
                            {
                                matches.Add(new RuleMatch { Account = account, Rule = rawRule, Remote = remote });
                                break;
                            }
                        }

                        if (matches.Count > 0 && matches[^1].Account.Id == account.Id)
                            break;
                    }
                }
            }

            return matches;
        }

        private static List<SshIdentityMatch> FindSshConfigMatches(
            IEnumerable<Models.GitHubAccount> accounts,
            List<Models.Remote> remotes)
        {
            var matches = new List<SshIdentityMatch>();
            var sshAccounts = accounts
                .Where(x => x.AuthType == Models.GitHubAuthType.SSHKey && x.HasValidCredentials)
                .ToList();
            if (sshAccounts.Count == 0 || remotes == null || remotes.Count == 0)
                return matches;

            var blocks = ReadSshConfigBlocks();
            if (blocks.Count == 0)
                return matches;

            foreach (var remote in remotes)
            {
                if (!IsSshRemote(remote.URL))
                    continue;

                var host = ExtractSshHost(remote.URL);
                if (string.IsNullOrWhiteSpace(host))
                    continue;

                var identityFiles = ResolveSshIdentityFiles(host, blocks);
                foreach (var identityFile in identityFiles)
                {
                    foreach (var account in sshAccounts)
                    {
                        if (!PathEquals(account.SSHKeyPath, identityFile))
                            continue;

                        matches.Add(new SshIdentityMatch
                        {
                            Account = account,
                            Remote = remote,
                            IdentityFile = identityFile,
                        });
                    }
                }
            }

            return matches;
        }

        private static List<SshConfigBlock> ReadSshConfigBlocks()
        {
            var blocks = new List<SshConfigBlock>();
            var config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh", "config");
            if (!File.Exists(config))
                return blocks;

            try
            {
                // Directives before the first Host block apply globally.
                var current = new SshConfigBlock();
                current.HostPatterns.Add("*");
                blocks.Add(current);

                foreach (var raw in File.ReadLines(config))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#'))
                        continue;

                    var separator = line.IndexOfAny([' ', '\t']);
                    if (separator <= 0)
                        continue;

                    var key = line[..separator].Trim().ToLowerInvariant();
                    var value = line[(separator + 1)..].Trim();
                    if (key == "host")
                    {
                        current = new SshConfigBlock();
                        current.HostPatterns.AddRange(value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                        blocks.Add(current);
                    }
                    else if (key == "hostname")
                    {
                        current.HostName = value.Trim('"');
                    }
                    else if (key == "identityfile")
                    {
                        current.IdentityFiles.Add(value.Trim('"'));
                    }
                }
            }
            catch
            {
                blocks.Clear();
            }

            return blocks;
        }

        private static List<string> ResolveSshIdentityFiles(string host, List<SshConfigBlock> blocks)
        {
            var identities = new List<string>();
            var remoteIsGitHub = host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

            foreach (var block in blocks)
            {
                if (!MatchesSshHostBlock(host, block.HostPatterns))
                    continue;

                var targetHost = string.IsNullOrWhiteSpace(block.HostName) ? host : block.HostName;
                if (!remoteIsGitHub && !targetHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var rawIdentity in block.IdentityFiles)
                {
                    var identity = ExpandSshIdentityPath(rawIdentity, host);
                    if (string.IsNullOrWhiteSpace(identity) || identities.Any(x => PathEquals(x, identity)))
                        continue;
                    identities.Add(identity);
                }
            }

            return identities;
        }

        private static bool MatchesSshHostBlock(string host, List<string> patterns)
        {
            var positive = false;
            foreach (var rawPattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(rawPattern))
                    continue;

                var negated = rawPattern[0] == '!';
                var pattern = negated ? rawPattern[1..] : rawPattern;
                if (!WildcardMatch(host, pattern))
                    continue;

                if (negated)
                    return false;
                positive = true;
            }

            return positive;
        }

        private static string ExtractSshHost(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            var value = url.Trim();
            if (value.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
                    return uri.Host;
                return string.Empty;
            }

            var at = value.IndexOf('@');
            var start = at >= 0 ? at + 1 : 0;
            var colon = value.IndexOf(':', start);
            if (colon > start)
                return value[start..colon];

            var slash = value.IndexOf('/', start);
            return slash > start ? value[start..slash] : value[start..];
        }

        private static string ExpandSshIdentityPath(string value, string host)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var expanded = value
                .Replace("%d", home, StringComparison.Ordinal)
                .Replace("%h", host, StringComparison.Ordinal);
            return NormalizePath(ExpandHome(expanded));
        }

        private static Models.Remote SelectPreferredRemote(Models.RepositorySettings settings, List<Models.Remote> remotes)
        {
            if (remotes == null || remotes.Count == 0)
                return null;

            if (!string.IsNullOrWhiteSpace(settings.DefaultRemote))
            {
                var configured = remotes.Find(x => x.Name.Equals(settings.DefaultRemote, StringComparison.Ordinal));
                if (configured != null)
                    return configured;
            }

            return remotes.Find(x => x.Name.Equals("origin", StringComparison.Ordinal)) ?? remotes[0];
        }

        private static bool IsCompatible(Models.GitHubAccount account, Models.Remote remote)
        {
            if (remote == null)
                return true;

            var ssh = IsSshRemote(remote.URL);
            return ssh
                ? account.AuthType == Models.GitHubAuthType.SSHKey
                : account.AuthType == Models.GitHubAuthType.PersonalAccessToken;
        }

        private static bool IsSshRemote(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
                return true;

            // Git also accepts SCP-like SSH URLs without an explicit user.
            var colon = url.IndexOf(':');
            var slash = url.IndexOf('/');
            return colon > 0 && (slash < 0 || colon < slash) && !url.Contains("://", StringComparison.Ordinal);
        }

        private static bool WildcardMatch(string value, string pattern)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(pattern))
                return false;

            var expression = "^" + Regex.Escape(pattern)
                .Replace("\\*\\*", ".*")
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string ExpandHome(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            if (value.Equals("~", StringComparison.Ordinal))
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);

            return value;
        }

        private static string NormalizePath(string value)
        {
            try
            {
                return Path.GetFullPath(value).Replace('\\', '/').TrimEnd('/');
            }
            catch
            {
                return (value ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            }
        }

        private static bool PathEquals(string left, string right)
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return NormalizePath(left).Equals(NormalizePath(right), comparison);
        }

        private static string FormatRemote(Models.Remote remote)
        {
            if (remote == null)
                return string.Empty;
            return string.IsNullOrWhiteSpace(remote.Name) ? remote.URL : remote.Name;
        }

        private static void SaveAutomaticBinding(Models.RepositorySettings settings, Models.GitHubAccount account, string reason, string remote)
        {
            settings.GitHubAccountId = account?.Id ?? Guid.Empty;
            settings.GitHubAccountIsExplicit = false;
            settings.GitHubAccountBindingReason = reason ?? string.Empty;
            settings.GitHubAccountBindingRemote = remote ?? string.Empty;
            settings.Save();
        }

        private static void SaveUnresolved(Models.RepositorySettings settings, string reason, string remote)
        {
            settings.GitHubAccountId = Guid.Empty;
            settings.GitHubAccountIsExplicit = false;
            settings.GitHubAccountBindingReason = reason ?? string.Empty;
            settings.GitHubAccountBindingRemote = remote ?? string.Empty;
            settings.Save();
        }

        private static bool TryGetSettings(string repoPath, out Models.RepositorySettings settings)
        {
            settings = null;
            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
                return false;

            try
            {
                var gitDir = new Commands.QueryGitDir(repoPath).GetResult();
                if (string.IsNullOrWhiteSpace(gitDir))
                    return false;

                settings = Models.RepositorySettings.Get(gitDir);
                return settings != null;
            }
            catch
            {
                return false;
            }
        }
    }
}
