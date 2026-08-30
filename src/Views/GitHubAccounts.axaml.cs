using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SourceGit.Views
{
    public partial class GitHubAccounts : UserControl
    {
        public GitHubAccounts()
        {
            InitializeComponent();
        }

        private async void BrowseSSHKey(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
                return;

            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Select SSH private key",
                FileTypeFilter = [new FilePickerFileType("SSH private key") { Patterns = ["*"] }],
            });

            if (selected.Count == 1 && DataContext is ViewModels.GitHubAccountsViewModel vm)
                vm.SetEditingSshKeyPath(selected[0].Path.LocalPath);

            e.Handled = true;
        }
    }
}
