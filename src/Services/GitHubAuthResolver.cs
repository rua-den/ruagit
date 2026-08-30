using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SourceGit.Services
{
    /// <summary>
    /// Resolves repository-to-account bindings while keeping the reason for the
    /// decision inspectable. Explicit user bindings always win. Automatic rules
    /// are evaluated before the legacy GitHub remote/account heuristic.
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

            // Preserve explicit bindings and legacy bindings whose provenance is unknown.
            // This avoids silently replacing a selection made before provenance tracking existed.
            if (current != null && settings.GitHubAccountIsExplicit != false)
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

            // Clear only an old automatic binding before invoking the existing heuristic;
            // otherwise GitHubCredential would immediately return that stored account.
            if (settings.GitHubAccountIsExplicit == false)
            {
                settings.GitHubAccountId = Guid.Empty;
                settings.Save();
            }

            var detected = await GitHubCredential.DetectForRepositoryAsync(repoPath).ConfigureAwait(false);
            if (detected != null)
            {
                SaveAutomaticBinding(settings, detected, "GitHub remote/account heuristic", FormatRemote(preferredRemote));
                return InspectRepository(repoPath);
            }

            SaveUnresolved(settings, "No matching auth rule or deterministic GitHub account", FormatRemote(preferredRemote));
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
                        await ResolveForRepositoryAsync(node.Id).ConfigureAwait(false);
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
            return !string.IsNullOrWhiteSpace(url) &&
                   (url.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                    url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));
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
