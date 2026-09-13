using System.Text;
using System.Threading.Tasks;

namespace SourceGit.Commands
{
    public class Push : Command
    {
        public Push(string repo, string local, string remote, string remoteBranch, bool withTags, bool checkSubmodules, bool track, bool force)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(1024);
            builder.Append("push --progress --verbose ");
            if (withTags)
                builder.Append("--tags ");
            if (checkSubmodules)
                builder.Append("--recurse-submodules=check ");
            if (track)
                builder.Append("-u ");
            if (force)
                builder.Append("--force-with-lease ");

            builder.Append(remote).Append(' ').Append(local).Append(':').Append(remoteBranch);
            Args = builder.ToString();
            ResolveBoundCredential();
        }

        public Push(string repo, string remote, string refname, bool isDelete)
        {
            _remote = remote;

            WorkingDirectory = repo;
            Context = repo;

            var builder = new StringBuilder(512);
            builder.Append("push ");
            if (isDelete)
                builder.Append("--delete ");
            builder.Append(remote).Append(' ').Append(refname);

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
