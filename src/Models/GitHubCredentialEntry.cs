namespace SourceGit.Models
{
    public class GitHubCredentialEntry
    {
        public string Host { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string Source { get; set; } = "vault";
        public string Alias { get; set; } = string.Empty;
    }
}