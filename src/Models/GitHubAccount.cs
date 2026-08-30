using System;
using System.Text.Json.Serialization;

using CommunityToolkit.Mvvm.ComponentModel;

using SourceGit.Services;

namespace SourceGit.Models
{
    public enum GitHubAuthType
    {
        PersonalAccessToken = 0,
        SSHKey = 1,
        OAuth = 2,
    }

    public partial class GitHubAccount : ObservableObject
    {
        public GitHubAccount()
        {
            Id = Guid.NewGuid();
            CreatedAt = DateTime.Now;
            UpdatedAt = DateTime.Now;
        }

        public Guid Id { get; set; }

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        private string _username = string.Empty;
        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value))
                    OnPropertyChanged(nameof(DisplayName));
            }
        }

        private string _email = string.Empty;
        public string Email
        {
            get => _email;
            set => SetProperty(ref _email, value);
        }

        private GitHubAuthType _authType = GitHubAuthType.PersonalAccessToken;
        public GitHubAuthType AuthType
        {
            get => _authType;
            set
            {
                if (SetProperty(ref _authType, value))
                    OnPropertyChanged(nameof(HasValidCredentials));
            }
        }

        [JsonIgnore]
        public string Token
        {
            get => CredentialManager.GetToken(Id);
            set
            {
                CredentialManager.StoreToken(Id, value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasValidCredentials));
            }
        }

        [JsonPropertyName("Token")]
        public string EncryptedToken { get; set; } = string.Empty;

        private string _sshKeyPath = string.Empty;
        public string SSHKeyPath
        {
            get => _sshKeyPath;
            set
            {
                if (SetProperty(ref _sshKeyPath, value))
                    OnPropertyChanged(nameof(HasValidCredentials));
            }
        }

        private string _matchRules = string.Empty;
        public string MatchRules
        {
            get => _matchRules;
            set
            {
                if (SetProperty(ref _matchRules, value ?? string.Empty))
                    OnPropertyChanged(nameof(HasMatchRules));
            }
        }

        [JsonIgnore]
        public bool HasMatchRules => !string.IsNullOrWhiteSpace(MatchRules);

        private bool _isDefault = false;
        public bool IsDefault
        {
            get => _isDefault;
            set => SetProperty(ref _isDefault, value);
        }

        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        private string _avatarUrl = string.Empty;
        public string AvatarUrl
        {
            get => _avatarUrl;
            set => SetProperty(ref _avatarUrl, value);
        }

        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Username : Name;

        public bool HasValidCredentials =>
            AuthType == GitHubAuthType.PersonalAccessToken ? !string.IsNullOrWhiteSpace(Token) :
            AuthType == GitHubAuthType.SSHKey ? !string.IsNullOrWhiteSpace(SSHKeyPath) :
            false;

        public void DeleteCredentials()
        {
            CredentialManager.DeleteToken(Id);
            OnPropertyChanged(nameof(HasValidCredentials));
        }
    }
}
