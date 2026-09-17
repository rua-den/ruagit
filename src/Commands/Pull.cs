using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Pull : Command
    {
        public Pull(string repo, string remote, string branch, bool useRebase)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(512);
            builder
                .Append("pull --verbose --progress --rebase=")
                .Append(useRebase ? "true" : "false")
                .Append(' ')
                .Append(remote)
                .Append(' ')
                .Append(branch);

            Args = builder.ToString();
            ResolveBoundCredential();
        }

        public async Task<bool> RunAsync()
        {
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
            var account = FindBoundGitHubAccount();
            if (account?.AuthType == Models.GitHubAuthType.PersonalAccessToken)
                ApplyGitHubCredential(account);
        }

        private readonly string _remote;
    }
}
