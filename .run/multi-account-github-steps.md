# Multi-Account GitHub Management - Manual/Human Steps

## AI Implementation Status (2026-08-24)

| Phase | Scope | Status |
|-------|-------|--------|
| 1 | Models (GitHubAccount, RepositoryAccountBinding) + CredentialManager (DPAPI/libsecret/Keychain) | DONE |
| 2 | Preferences tab: account CRUD + Test Connection (GitHub API /user) | DONE |
| 3 | Repo binding UI (RepositoryConfigure tab), badge in Welcome list | DONE |
| 4 | Clone dialog account selector + auto-bind after clone | DONE |
| 5 | Git ops credential injection (Fetch/Push/Pull/Clone via headless GIT_ASKPASS) | DONE |

### Phase 5 technical notes for reviewers
- Credentials flow via env vars (`SOURCEGIT_GITHUB_ASKPASS_TOKEN`) — never in args/logs
- `App.TryLaunchAsAskpass` answers Username/Password prompts headlessly when token env is set
- `credential.helper` is bypassed (empty) for commands carrying a bound token
- SSH accounts set `SSHKeyPath` only when repo has no explicit `remote.<name>.sshkey`
- Remaining human work below is unchanged.

## Steps NOT to be coded by AI (Human Tasks)

### 1. Security Review & Approval
- [ ] Review encryption implementation for PAT tokens
- [ ] Approve credential storage approach (DPAPI/libsecret/Keychain)
- [ ] Verify no tokens in logs, memory dumps, or crash reports

### 2. Platform-Specific Credential Manager Setup
- [ ] Windows: Verify DPAPI integration works correctly
- [ ] Linux: Install/configure libsecret, test with common distros (Ubuntu, Fedora, Arch)
- [ ] macOS: Test Keychain integration, handle permission prompts

### 3. GitHub Token Creation Guide (User Documentation)
- [ ] Document how to create GitHub PAT with correct scopes
- [ ] Document SSH key generation and GitHub setup
- [ ] Create screenshots for user guide

### 4. Testing on Real GitHub Accounts
- [ ] Test with multiple personal accounts
- [ ] Test with organization accounts
- [ ] Test with GitHub Enterprise (if applicable)
- [ ] Test SSH + PAT mixed scenarios
- [ ] Test repo binding persistence across restarts

### 5. UI/UX Review
- [ ] Review account management UI with designer
- [ ] Test accessibility (screen readers, keyboard nav)
- [ ] Verify dark/light theme compatibility
- [ ] Test with long account names, many accounts (10+)

### 6. Migration Strategy
- [ ] Plan for existing users: how to migrate current Git config
- [ ] Detect existing GitHub remotes and suggest account creation
- [ ] Handle users who use GCM (Git Credential Manager) already

### 7. Release Checklist
- [ ] Update CHANGELOG
- [ ] Bump version
- [ ] Create release notes with screenshots
- [ ] Update documentation/wiki

---

## AI-Coded Tasks (Reference from .plans)
See `.plans/multi-account-github-management.md` for implementation plan.

## Notes for Human Implementer
- Start with Phase 1 (Models & Storage) - foundation for everything
- CredentialManager is critical - get security review early
- Test encryption/decryption thoroughly before adding UI
- Consider using existing `Native.OS` abstractions for cross-platform crypto
- GitHub API calls for avatar/validation should be optional (offline support)