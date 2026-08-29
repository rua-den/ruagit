using System;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.Models
{
    public partial class RepositoryAccountBinding : ObservableObject
    {
        private string _repositoryPath = string.Empty;
        public string RepositoryPath
        {
            get => _repositoryPath;
            set => SetProperty(ref _repositoryPath, value);
        }

        private Guid _accountId = Guid.Empty;
        public Guid AccountId
        {
            get => _accountId;
            set => SetProperty(ref _accountId, value);
        }

        private string _remoteName = "origin";
        public string RemoteName
        {
            get => _remoteName;
            set => SetProperty(ref _remoteName, value);
        }

        public DateTime BoundAt { get; set; } = DateTime.Now;

        [JsonIgnore]
        public string NormalizedPath => RepositoryPath.Replace('\\', '/').TrimEnd('/');
    }
}