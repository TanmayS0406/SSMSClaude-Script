using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SsmsChatExtension.Services.Claude
{
    /// <summary>
    /// User settings for the Claude integration, stored in %LOCALAPPDATA%\SsmsChatExtension\settings.json.
    /// The optional connection string is DPAPI-protected (current user) because it may contain a password.
    /// </summary>
    public sealed class ClaudeSettings
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsmsChatExtension");

        private static readonly string FilePath = Path.Combine(Folder, "settings.json");

        /// <summary>Full path to claude.exe / claude.cmd. Empty = search PATH and the usual install folders.</summary>
        public string ClaudePath { get; set; } = string.Empty;

        /// <summary>Folder Claude works in and may edit (your SQL scripts repository). Empty = a private scratch folder.</summary>
        public string RepositoryFolder { get; set; } = string.Empty;

        /// <summary>Let Claude create and edit files in <see cref="RepositoryFolder"/> without asking.</summary>
        public bool AllowFileEdits { get; set; } = true;

        /// <summary>Optional model alias or id passed to --model (for example "sonnet" or "opus"). Empty = CLI default.</summary>
        public string Model { get; set; } = string.Empty;

        /// <summary>DPAPI-protected, base64. Use <see cref="ConnectionStringOverride"/> instead.</summary>
        public string ProtectedConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Connection string used for database tools instead of the active query window's connection
        /// (needed for Microsoft Entra logins, which cannot be reused from SSMS).
        /// </summary>
        [JsonIgnore]
        public string ConnectionStringOverride
        {
            get { return Unprotect(ProtectedConnectionString); }
            set { ProtectedConnectionString = Protect(value); }
        }

        /// <summary>Folder used when no repository folder is configured.</summary>
        [JsonIgnore]
        public static string ScratchFolder
        {
            get { return Path.Combine(Folder, "workspace"); }
        }

        public static ClaudeSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonConvert.DeserializeObject<ClaudeSettings>(File.ReadAllText(FilePath)) ?? new ClaudeSettings();
                }
            }
            catch
            {
                // Corrupt or unreadable file - start from defaults.
            }
            return new ClaudeSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }

        private static string Protect(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
