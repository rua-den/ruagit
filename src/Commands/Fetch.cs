using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Fetch : Command
    {
        public Fetch(string repo, string remote, bool noTags, bool force)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(512);
            builder.Append("fetch --progress --verbose ");
            builder.Append(noTags ? "--no-tags " : "--tags ");
            if (force)
                builder.Append("--force ");
            builder.Append(remote);

            Args = builder.ToString();
            ResolveBoundCredential();
        }

        public Fetch(string repo, string remote)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;
            RaiseError = false;
            NonInteractiveAuthentication = true;

            Args = $"fetch --progress --verbose {remote}";
            ResolveBoundCredential();
        }

        public Fetch(string repo, Models.Branch local, Models.Branch remote)
        {
            _remote = remote.Remote;

            WorkingDirectory = repo;
            Context = repo;
            Args = $"fetch --progress --verbose {remote.Remote} {remote.Name}:{local.Name}";
            ResolveBoundCredential();
        }

        public async Task<bool> RunAsync()
        {
            // Resolve credentials as late as possible so auth rules/SSH config win over
            // a stale account cached when the command object was constructed.
            GitHubUsername = string.Empty;
            GitHubToken = string.Empty;

            var configuredKey = await new Config(WorkingDirectory).GetAsync($"remote.{_remote}.sshkey").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(configuredKey))
            {
                SSHKey = configuredKey;
            }
            else
            {
                SSHKey = string.Empty;
                var resolution = await Services.GitHubAuthResolver.ResolveForRepositoryAsync(WorkingDirectory).ConfigureAwait(false);
                ApplyGitHubCredential(resolution.Account);
            }

            return await ExecAsync().ConfigureAwait(false);
        }

        private void ResolveBoundCredential()
        {
            // PAT can be staged eagerly because RunAsync always refreshes the final
            // credential choice. SSH is intentionally resolved only after checking the
            // remote-specific sshkey setting.
            var account = FindBoundGitHubAccount();
            if (account?.AuthType == Models.GitHubAuthType.PersonalAccessToken)
                ApplyGitHubCredential(account);
        }

        private readonly string _remote;
    }
}
