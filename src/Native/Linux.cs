using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SourceGit.Native
{
    [SupportedOSPlatform("linux")]
    internal class Linux : OS.IBackend
    {
        public void SetupApp(AppBuilder builder)
        {
            builder.With(new X11PlatformOptions() { EnableIme = true });
        }

        public void SetupWindow(Window window)
        {
            window.BorderThickness = new Thickness(0);

            if (OS.UseSystemWindowFrame)
            {
                window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
                window.ExtendClientAreaToDecorationsHint = false;
            }
            else
            {
                window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
                window.ExtendClientAreaToDecorationsHint = true;
                window.Classes.Add("custom_window_frame");
            }
        }

        public string GetDataDir()
        {
            // AppImage supports portable mode
            var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
            if (!string.IsNullOrEmpty(appImage) && File.Exists(appImage))
            {
                var portableDir = Path.Combine(Path.GetDirectoryName(appImage)!, "data");
                if (Directory.Exists(portableDir))
                    return portableDir;
            }

            // Runtime data dir: ~/.sourcegit
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".sourcegit");
        }

        public string FindGitExecutable()
        {
            return FindExecutable("git");
        }

        public string FindTerminal(Models.ShellOrTerminal shell)
        {
            if (shell.Type.Equals("custom", StringComparison.Ordinal))
                return string.Empty;

            return FindExecutable(shell.Exec);
        }

        public List<Models.ExternalTool> FindExternalTools()
        {
            var localAppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var finder = new Models.ExternalToolsFinder();
            finder.VSCode(() => FindExecutable("code"));
            finder.VSCodeInsiders(() => FindExecutable("code-insiders"));
            finder.VSCodium(() => FindExecutable("codium"));
            finder.Cursor(() => FindExecutable("cursor"));
            finder.FindJetBrainsFromToolbox(() => Path.Combine(localAppDataDir, "JetBrains/Toolbox"));
            finder.SublimeText(() => FindExecutable("subl"));
            finder.Zed(() =>
            {
                var exec = FindExecutable("zeditor");
                return string.IsNullOrEmpty(exec) ? FindExecutable("zed") : exec;
            });
            return finder.Tools;
        }

        public void OpenBrowser(string url)
        {
            var browser = Environment.GetEnvironmentVariable("BROWSER");
            if (string.IsNullOrEmpty(browser))
                browser = "xdg-open";
            Process.Start(browser, url.Quoted());
        }

        public void OpenInFileManager(string path)
        {
            if (Directory.Exists(path))
            {
                Process.Start("xdg-open", path.Quoted());
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (Directory.Exists(dir))
                    Process.Start("xdg-open", dir.Quoted());
            }
        }

        public void OpenTerminal(string workdir, string args)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cwd = string.IsNullOrEmpty(workdir) ? home : workdir;

            var startInfo = new ProcessStartInfo();
            startInfo.WorkingDirectory = cwd;
            startInfo.FileName = OS.ShellOrTerminal;
            startInfo.Arguments = args;

            try
            {
                Process.Start(startInfo);
            }
            catch (Exception e)
            {
                Models.Notification.Send(workdir, $"Failed to start '{OS.ShellOrTerminal}'. Reason: {e.Message}", true);
            }
        }

        public void OpenWithDefaultEditor(string file)
        {
            var proc = Process.Start("xdg-open", file.Quoted());
            if (proc != null)
            {
                proc.WaitForExit();

                if (proc.ExitCode != 0)
                    Models.Notification.Send("", $"Failed to open: {file}", true);

                proc.Close();
            }
        }

        private string FindExecutable(string filename)
        {
            var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            var paths = pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var path in paths)
            {
                var test = Path.Combine(path, filename);
                if (File.Exists(test))
                    return test;
            }

            var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", filename);
            return File.Exists(local) ? local : string.Empty;
        }

        public bool ProtectData(byte[] data, out byte[] protectedData)
        {
            protectedData = null;
            try
            {
                var encoded = Convert.ToBase64String(data);
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "secret-tool",
                    Arguments = $"store --label='SourceGit Credential' application sourcegit key {Guid.NewGuid()}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                if (proc != null)
                {
                    proc.StandardInput.Write(encoded);
                    proc.StandardInput.Close();
                    proc.WaitForExit();
                    if (proc.ExitCode == 0)
                    {
                        protectedData = Encoding.UTF8.GetBytes("libsecret");
                        return true;
                    }
                }
            }
            catch
            {
            }

            protectedData = Encoding.UTF8.GetBytes(Convert.ToBase64String(data));
            return true;
        }

        public bool UnprotectData(byte[] protectedData, out byte[] data)
        {
            data = null;
            try
            {
                if (protectedData != null && protectedData.Length > 0)
                {
                    var marker = Encoding.UTF8.GetString(protectedData);
                    if (marker == "libsecret")
                    {
                        var proc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "secret-tool",
                            Arguments = "lookup application sourcegit",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                        });
                        if (proc != null)
                        {
                            var output = proc.StandardOutput.ReadToEnd().Trim();
                            proc.WaitForExit();
                            if (proc.ExitCode == 0 && !string.IsNullOrEmpty(output))
                            {
                                data = Convert.FromBase64String(output);
                                return true;
                            }
                        }
                    }
                    else
                    {
                        data = Convert.FromBase64String(marker);
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        public bool DeleteCredential(string key)
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "secret-tool",
                    Arguments = $"clear application sourcegit key {key}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                proc?.WaitForExit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<Models.GitHubCredentialEntry> FindStoredGitHubCredentials()
        {
            // libsecret schema used by GCM varies across distros; not supported yet.
            return [];
        }
    }
}
