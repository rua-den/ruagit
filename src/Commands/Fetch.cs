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
            var configuredKey = await new Config(WorkingDirectory).GetAsync($"remote.{_remote}.sshkey").ConfigureAwait(false);
            if (!string.IsNullOrEmpty(configuredKey))
                SSHKey = configuredKey;
            else if (string.IsNullOrEmpty(SSHKey))
                ApplyGitHubCredential(await Services.GitHubCredential.DetectForRepositoryAsync(WorkingDirectory).ConfigureAwait(false));
            return await ExecAsync().ConfigureAwait(false);
        }

        private void ResolveBoundCredential()
        {
            // SSH key from bound account only applies when no explicit key configured yet;
            // RunAsync may still override via git config afterwards.
            var account = FindBoundGitHubAccount();
            if (account?.AuthType == Models.GitHubAuthType.PersonalAccessToken)
                ApplyGitHubCredential(account);
        }

        private readonly string _remote;
    }
}
