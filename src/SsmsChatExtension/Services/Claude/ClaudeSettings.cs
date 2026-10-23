using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace SsmsChatExtension.Services.Claude
{
    /// <summary>Whether Claude may run SQL that changes the database.</summary>
    public enum DatabaseChangeMode
    {
        /// <summary>Read-only: only run_query (always rolled back).</summary>
        Off,

        /// <summary>execute_sql / execute_query_window show a confirmation dialog first.</summary>
        Ask,

        /// <summary>execute_sql / execute_query_window run without asking.</summary>
        Allow
    }

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

        /// <summary>Legacy (1.0 - 1.2) switch, used only when <see cref="ClaudePermissionMode"/> is not set.</summary>
        public bool AllowFileEdits { get; set; } = true;

        /// <summary>
        /// Claude Code --permission-mode for files and shell commands: "acceptEdits" (edit files in the repository),
        /// "auto" (Claude Code's auto mode: a safety classifier approves actions, including shell commands)
        /// or "default" (anything that needs permission is refused, because nobody can answer a prompt here).
        /// </summary>
        public string ClaudePermissionMode { get; set; }

        /// <summary>Optional --effort: low, medium, high, xhigh or max. Empty = the model's default.</summary>
        public string Effort { get; set; } = string.Empty;

        [JsonIgnore]
        public string EffectivePermissionMode
        {
            get
            {
                switch (ClaudePermissionMode)
                {
                    case "acceptEdits":
                    case "auto":
                    case "default":
                        return ClaudePermissionMode;
                    default:
                        return AllowFileEdits ? "acceptEdits" : "default";
                }
            }
        }

        /// <summary>Whether Claude may run SQL that changes the database. Default: ask every time.</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public DatabaseChangeMode DatabaseChanges { get; set; } = DatabaseChangeMode.Ask;

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
