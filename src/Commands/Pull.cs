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
            ApplyGitHubCredential(FindBoundGitHubAccount());
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

        private readonly string _remote;
    }
}
