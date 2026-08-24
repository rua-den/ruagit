# Multi-Account GitHub Management Feature Plan

## Overview
Add support for managing multiple GitHub accounts in SourceGit, with UI to configure accounts and track which repositories are authenticated with which account.

## Current State Analysis
- Existing `User` model (Name, Email) - used for commit authorship
- Existing `Remote` model - handles Git URLs (HTTPS, SSH, Git protocol)
- Existing `RepositorySettings` - per-repo settings
- Preferences stores `RepositoryNodes` (list of repositories)
- GitHub avatar support exists via `AvatarManager`
- No existing multi-account management for GitHub authentication

## Requirements

### 1. Data Models
- **GitHubAccount** model:
  - `Id` (Guid)
  - `Name` (display name, e.g., "Personal", "Work")
  - `Username` (GitHub username)
  - `Email` (GitHub email)
  - `AuthType` (enum: PAT, SSH, OAuth)
  - `Token` (encrypted PAT) - for PAT auth
  - `SSHKeyPath` - for SSH auth
  - `IsDefault` (bool)
  - `CreatedAt`, `UpdatedAt` (DateTime)
  - `AvatarUrl` (cached)

- **RepositoryAccountBinding** model:
  - `RepositoryPath` (string)
  - `AccountId` (Guid)
  - `RemoteName` (string, default "origin")

### 2. Storage
- Store accounts in `preference.json` (or separate `github-accounts.json`)
- Encrypt sensitive data (PAT tokens) using DPAPI (Windows) / libsecret (Linux) / Keychain (macOS)
- Store repository-account bindings in repository settings or separate file

### 3. UI Components

#### A. GitHub Accounts Management Page (Preferences)
- List all configured GitHub accounts
- Add/Edit/Delete accounts
- Set default account
- Test connection button
- Show avatar, username, auth type

#### B. Repository Account Binding UI
- In Repository Settings / Configure: dropdown to select GitHub account
- Show current binding in repository list (launcher)
- Auto-detect account from remote URL when possible

#### C. Launcher/Repository View
- Show GitHub account badge/icon next to repository
- Tooltip with account details
- Filter repositories by account

### 4. Git Operations Integration
- When performing Git operations (push, pull, fetch, clone):
  - Use the bound account's credentials
  - For HTTPS: use PAT as password
  - For SSH: use configured SSH key
- Credential helper integration

### 5. Clone Dialog Enhancement
- When cloning from GitHub:
  - Show account selector
  - Auto-fill auth based on selected account
  - Create binding automatically

## Implementation Steps

### Phase 1: Core Models & Storage
1. Create `GitHubAccount` model in `Models/`
2. Create `RepositoryAccountBinding` model
3. Add account storage to `Preferences` or new service
4. Implement encryption for tokens

### Phase 2: UI - Account Management
1. Create `GitHubAccountsViewModel` 
2. Create `GitHubAccounts.axaml` + `.axaml.cs`
3. Add to Preferences tabs
4. Implement Add/Edit/Delete/Test flows

### Phase 3: UI - Repository Binding
1. Add account selector to `RepositoryConfigure`
2. Add binding display in `LauncherPage` / repository list
3. Add filtering by account in Launcher

### Phase 4: Git Operations Integration
1. Modify Git commands to use account credentials
2. Update `Remote` handling for auth
3. Integrate with credential helper

### Phase 5: Clone & New Repo
1. Enhance Clone dialog with account selector
2. Auto-create binding on clone
3. Support for GitHub OAuth (future)

## Technical Considerations

### Security
- Encrypt PAT tokens at rest
- Never log tokens
- Clear tokens from memory after use
- Support SSH keys as alternative

### Cross-platform
- Windows: DPAPI (ProtectedData)
- Linux: libsecret
- macOS: Keychain
- Fallback: encrypted file with machine-specific key

### Git Integration
- Use `git credential` helper protocol
- Or set `GIT_ASKPASS` for specific operations
- Or embed credentials in remote URL (less secure)

## Files to Create/Modify

### New Files:
- `Models/GitHubAccount.cs`
- `Models/RepositoryAccountBinding.cs`
- `Services/GitHubAccountService.cs`
- `Services/CredentialManager.cs`
- `ViewModels/GitHubAccountsViewModel.cs`
- `Views/GitHubAccounts.axaml`
- `Views/GitHubAccounts.axaml.cs`
- `Views/EditGitHubAccount.axaml`
- `Views/EditGitHubAccount.axaml.cs`

### Modified Files:
- `ViewModels/Preferences.cs` - add accounts collection, navigation
- `Views/Preferences.axaml` - add GitHub Accounts tab
- `ViewModels/RepositoryConfigure.cs` - add account binding
- `Views/RepositoryConfigure.axaml` - add account selector
- `ViewModels/LauncherPage.cs` - add account filtering/display
- `Views/LauncherPage.axaml` - add account badge
- `ViewModels/Clone.cs` - add account selector
- `Views/Clone.axaml` - add account selector
- `Commands/Remote.cs` - credential integration
- `Commands/Fetch.cs`, `Push.cs`, `Pull.cs`, `Clone.cs` - use account credentials

## Acceptance Criteria
- [ ] User can add multiple GitHub accounts (PAT, SSH)
- [ ] User can set default account
- [ ] User can bind repository to specific account
- [ ] Repository list shows which account is used
- [ ] Git operations use correct credentials
- [ ] Clone dialog allows selecting account
- [ ] Tokens are encrypted at rest
- [ ] Works on Windows, Linux, macOS

## Future Enhancements
- GitHub OAuth flow (device code or web)
- GitHub App installation support
- Organization-level account management
- Sync accounts across devices (encrypted)
- 2FA support for PAT