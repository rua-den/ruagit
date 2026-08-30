using System;
using System.IO;
using System.Text;

using SourceGit.Native;

namespace SourceGit.Services
{
    public static class CredentialManager
    {
        private const string Prefix = "github_account_";

        public static bool StoreToken(Guid accountId, string token)
        {
            if (string.IsNullOrEmpty(token))
                return DeleteToken(accountId);

            var data = Encoding.UTF8.GetBytes(token);
            var key = Prefix + accountId;
            return OS.ProtectData(data, out var protectedData) &&
                   SaveProtectedData(key, protectedData);
        }

        public static string GetToken(Guid accountId)
        {
            var key = Prefix + accountId;
            if (LoadProtectedData(key, out var protectedData) &&
                OS.UnprotectData(protectedData, out var data))
            {
                return Encoding.UTF8.GetString(data);
            }
            return string.Empty;
        }

        public static bool DeleteToken(Guid accountId)
        {
            var key = Prefix + accountId;

            // Always attempt both cleanup paths. The platform credential entry may not
            // exist (tokens are currently stored in our protected data file), and using
            // && here would short-circuit before deleting that file.
            var platformDeleted = OS.DeleteCredential(key);
            var fileDeleted = DeleteProtectedData(key);
            return platformDeleted || fileDeleted;
        }

        private static bool SaveProtectedData(string key, byte[] data)
        {
            try
            {
                var file = GetCredentialFilePath(key);
                var dir = Path.GetDirectoryName(file);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);
                File.WriteAllBytes(file, data);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool LoadProtectedData(string key, out byte[] data)
        {
            data = null;
            try
            {
                var file = GetCredentialFilePath(key);
                if (File.Exists(file))
                {
                    data = File.ReadAllBytes(file);
                    return data.Length > 0;
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool DeleteProtectedData(string key)
        {
            try
            {
                var file = GetCredentialFilePath(key);
                if (File.Exists(file))
                    File.Delete(file);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetCredentialFilePath(string key)
        {
            var safeKey = key.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
            var dir = Path.Combine(OS.DataDir, "credentials");
            return Path.Combine(dir, $"{safeKey}.dat");
        }
    }
}