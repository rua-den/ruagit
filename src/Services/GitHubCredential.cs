using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SourceGit.Services
{
    public static partial class GitHubCredential
    {
        public static Models.GitHubAccount FindForRepository(string repoPath)
        {
            if (string.IsNullOrEmpty(repoPath) || !Directory.Exists(repoPath))
                return null;

            try
            {
                var gitDir = new Commands.QueryGitDir(repoPath).GetResult();
                if (string.IsNullOrEmpty(gitDir))
                    return null;

                var settings = Models.RepositorySettings.Get(gitDir);
                if (settings.GitHubAccountId == Guid.Empty)
                    return null;

                return ViewModels.Preferences.Instance.GetGitHubAccount(settings.GitHubAccountId);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves which configured GitHub account is used by this repository.
        /// Priority: 1) explicit binding in sourcegit.settings, 2) remote URL owner
        /// matching a configured account's username, 3) for SSH remotes, the only
        /// configured SSH-key account when there is exactly one.
        /// </summary>
        public static async System.Threading.Tasks.Task<Models.GitHubAccount> DetectForRepositoryAsync(string repoPath)
        {
            var pref = ViewModels.Preferences.Instance;
            if (pref.GitHubAccounts.Count == 0)
                return null;

            // 1. Explicit binding wins.
            var bound = FindForRepository(repoPath);
            if (bound != null)
                return bound;

            try
            {
                var remotes = await new Commands.QueryRemotes(repoPath).GetResultAsync().ConfigureAwait(false);

                // 2. Match remote URL owner against account usernames.
                foreach (var remote in remotes)
                {
                    var owner = ExtractGitHubOwner(remote.URL);
                    if (string.IsNullOrEmpty(owner))
                        continue;

                    foreach (var acc in pref.GitHubAccounts)
                    {
                        if (string.Equals(acc.Username, owner, StringComparison.OrdinalIgnoreCase))
                            return acc;
                    }
                }

                // 3. SSH remotes: an SSH key cannot be matched by username, so when
                // exactly one SSH-key account is configured, assume it.
                var hasSshRemote = remotes.Exists(r =>
                    r.URL.StartsWith("git@", StringComparison.OrdinalIgnoreCase) ||
                    r.URL.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase));
                if (hasSshRemote)
                {
                    Models.GitHubAccount singleSsh = null;
                    foreach (var acc in pref.GitHubAccounts)
                    {
                        if (acc.AuthType == Models.GitHubAuthType.SSHKey &&
                            !string.IsNullOrWhiteSpace(acc.SSHKeyPath))
                        {
                            if (singleSsh != null)
                            {
                                singleSsh = null;
                                break;
                            }
                            singleSsh = acc;
                        }
                    }
                    if (singleSsh != null)
                        return singleSsh;
                }
            }
            catch
            {
            }

            return null;
        }

        public static string ExtractGitHubOwner(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            var match = REG_GITHUB_OWNER().Match(url.Trim());
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        /// <summary>
        /// Scans locally available git credential stores for github.com accounts:
        /// platform credential vault (GCM), ~/.git-credentials, GitHub CLI (gh)
        /// auth sessions, and ~/.ssh/config entries pointing at github.com.
        /// </summary>
        public static List<Models.GitHubCredentialEntry> ScanLocalStores()
        {
            var results = new List<Models.GitHubCredentialEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(Models.GitHubCredentialEntry entry)
            {
                var dedupeKey = entry.Source == "ssh"
                    ? "ssh:" + entry.Secret
                    : entry.Username;
                if (string.IsNullOrEmpty(dedupeKey) || !seen.Add(dedupeKey))
                    return;
                results.Add(entry);
            }

            // 1. Platform credential vault (GCM on Windows).
            foreach (var entry in Native.OS.FindStoredGitHubCredentials())
            {
                if (string.IsNullOrWhiteSpace(entry.Secret))
                    continue;
                entry.Source = "vault";
                Add(entry);
            }

            // 2. Plaintext ~/.git-credentials (helper=store).
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".git-credentials");
                if (File.Exists(path))
                {
                    foreach (var line in File.ReadAllLines(path))
                    {
                        var match = REG_GIT_CREDENTIALS().Match(line.Trim());
                        if (!match.Success)
                            continue;

                        var host = match.Groups[3].Value;
                        if (!host.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                            continue;

                        Add(new Models.GitHubCredentialEntry
                        {
                            Host = "github.com",
                            Username = Uri.UnescapeDataString(match.Groups[1].Value),
                            Secret = Uri.UnescapeDataString(match.Groups[2].Value),
                            Source = "file",
                        });
                    }
                }
            }
            catch
            {
            }

            // 3. GitHub CLI authenticated sessions (`gh auth status`).
            try
            {
                ScanGitHubCli(Add);
            }
            catch
            {
            }

            // 4. SSH config entries for github.com.
            try
            {
                ScanSshConfig(Add);
            }
            catch
            {
            }

            return results;
        }

        private static void ScanGitHubCli(Action<Models.GitHubCredentialEntry> add)
        {
            var gh = FindGhCli();
            if (string.IsNullOrEmpty(gh))
                return;

            var status = RunCapture(gh, "auth status");
            var combined = $"{status.Item1}\n{status.Item2}";
            var logins = new List<string>();
            foreach (Match m in REG_GH_LOGIN().Matches(combined))
            {
                var login = m.Groups[1].Value;
                if (!logins.Contains(login))
                    logins.Add(login);
            }

            foreach (var login in logins)
            {
                var token = RunCapture(gh, $"auth token --user {login}").Item1?.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    token = logins.Count == 1 ? RunCapture(gh, "auth token").Item1?.Trim() : null;
                if (string.IsNullOrWhiteSpace(token))
                    continue;

                add(new Models.GitHubCredentialEntry
                {
                    Host = "github.com",
                    Username = login,
                    Secret = token,
                    Source = "gh",
                });
            }
        }

        private static void ScanSshConfig(Action<Models.GitHubCredentialEntry> add)
        {
            var config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".ssh", "config");
            if (!File.Exists(config))
                return;

            var aliases = new List<string>();
            var matchesGithub = false;
            var identityFile = string.Empty;

            void Flush()
            {
                if (matchesGithub && !string.IsNullOrEmpty(identityFile))
                {
                    add(new Models.GitHubCredentialEntry
                    {
                        Host = "github.com",
                        Username = string.Empty,
                        Secret = identityFile,
                        Source = "ssh",
                        Alias = aliases.FirstOrDefault(a => !a.Contains('*') && !a.Contains('?')) ?? "github-ssh",
                    });
                }
                aliases.Clear();
                matchesGithub = false;
                identityFile = string.Empty;
            }

            foreach (var raw in File.ReadAllLines(config))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                    continue;

                var sep = line.IndexOf(' ');
                if (sep <= 0)
                    continue;

                var key = line.Substring(0, sep).ToLowerInvariant();
                var val = line.Substring(sep + 1).Trim().Trim('"');

                if (key == "host")
                {
                    Flush();
                    aliases.AddRange(val.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                }
                else if (key == "hostname" && val.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                {
                    matchesGithub = true;
                }
                else if (key == "identityfile" && string.IsNullOrEmpty(identityFile))
                {
                    var expanded = val.StartsWith("~/")
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), val[2..])
                        : val;
                    if (File.Exists(expanded))
                        identityFile = expanded;
                }
            }

            Flush();
        }

        private static string FindGhCli()
        {
            var test = RunCapture("gh", "--version");
            if (!string.IsNullOrEmpty(test.Item1) || !string.IsNullOrEmpty(test.Item2))
                return "gh";

            var pf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "GitHub CLI", "gh.exe");
            return File.Exists(pf) ? pf : string.Empty;
        }

        private static (string stdout, string stderr) RunCapture(string fileName, string args)
        {
            try
            {
                using var proc = new System.Diagnostics.Process();
                proc.StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                proc.Start();
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(true); } catch { }
                    return (string.Empty, string.Empty);
                }
                return (proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
            }
            catch
            {
                return (string.Empty, string.Empty);
            }
        }

        [GeneratedRegex(@"Logged in to github\.com account (\S+)", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GH_LOGIN();

        [GeneratedRegex(@"^https?://([^:/@]+):([^@]+)@([\w\.\-]+)/", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GIT_CREDENTIALS();

        [GeneratedRegex(@"^(?:https?://|ssh://)?(?:[^@/\s]+@)?github\.com[:/]([\w\.\-]+)/", RegexOptions.IgnoreCase)]
        private static partial Regex REG_GITHUB_OWNER();

        public static string GetRemoteHost(string url)
        {
            if (string.IsNullOrEmpty(url))
                return string.Empty;

            var host = url;

            var idx = host.IndexOf("://", StringComparison.Ordinal);
            if (idx > 0)
                host = host.Substring(idx + 3);

            var colonIdx = host.IndexOf(':', StringComparison.Ordinal);
            if (colonIdx > 0 && !host.Contains('/', StringComparison.Ordinal))
                host = host.Substring(0, colonIdx);

            var slashIdx = host.IndexOf('/', StringComparison.Ordinal);
            if (slashIdx > 0)
                host = host.Substring(0, slashIdx);

            var atIdx = host.IndexOf('@', StringComparison.Ordinal);
            if (atIdx >= 0)
                host = host.Substring(atIdx + 1);

            return host.TrimEnd('/');
        }
    }
}