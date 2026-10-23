using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SsmsChatExtension.Services.Claude
{
    /// <summary>Result of <c>claude auth status</c>.</summary>
    public sealed class ClaudeAuthStatus
    {
        public ClaudeAuthStatus(bool cliFound, bool loggedIn, string detail)
        {
            CliFound = cliFound;
            LoggedIn = loggedIn;
            Detail = detail ?? string.Empty;
        }

        public bool CliFound { get; }
        public bool LoggedIn { get; }

        /// <summary>Account e-mail / auth method, or the reason it is not signed in.</summary>
        public string Detail { get; }
    }

    /// <summary>
    /// Thin wrapper around the Claude Code CLI. The extension never handles Claude credentials itself:
    /// sign-in, token storage and refresh all happen inside the official CLI.
    /// </summary>
    internal static class ClaudeCli
    {
        /// <summary>Resolves the CLI path from the setting, PATH, or the default install folders. Null when not found.</summary>
        public static string Locate(string configuredPath)
        {
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                return File.Exists(configuredPath) ? configuredPath : null;
            }

            var candidates = new List<string>();
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), "claude.exe"));
                candidates.Add(Path.Combine(dir.Trim(), "claude.cmd"));
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));   // native installer
            candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));         // npm -g install

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // Malformed PATH entry.
                }
            }
            return null;
        }

        /// <summary>Runs <c>claude auth status</c>.</summary>
        public static async Task<ClaudeAuthStatus> GetAuthStatusAsync(string cliPath, CancellationToken cancellationToken)
        {
            if (cliPath == null)
            {
                return new ClaudeAuthStatus(false, false, "Claude Code CLI not found.");
            }

            var output = new StringBuilder();
            int exitCode;
            try
            {
                exitCode = await RunAsync(cliPath, "auth status", output, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return new ClaudeAuthStatus(true, false, ex.Message);
            }

            var text = output.ToString().Trim();
            try
            {
                var json = JObject.Parse(text);
                var loggedIn = (bool?)json["loggedIn"] ?? exitCode == 0;
                var detail = (string)json["email"] ?? (string)json["authMethod"] ?? string.Empty;
                return new ClaudeAuthStatus(true, loggedIn, loggedIn ? detail : "Not signed in.");
            }
            catch
            {
                // Plain text output (older CLI): rely on the exit code.
                return new ClaudeAuthStatus(true, exitCode == 0, exitCode == 0 ? string.Empty : "Not signed in.");
            }
        }

        /// <summary>
        /// Opens a console window running <c>claude auth login</c>, which takes the user through the browser sign-in.
        /// Returns the console process so the caller can refresh the status when it closes.
        /// </summary>
        public static Process StartLogin(string cliPath)
        {
            var info = new ProcessStartInfo("cmd.exe",
                "/d /s /c \"" + QuoteArgument(cliPath) + " auth login & echo. & pause\"")
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            return Process.Start(info);
        }

        public static Task<int> LogoutAsync(string cliPath, CancellationToken cancellationToken)
        {
            return RunAsync(cliPath, "auth logout", new StringBuilder(), cancellationToken);
        }

        /// <summary>
        /// Builds a hidden, redirected process for the CLI. <c>.cmd</c> shims (npm installs) go through cmd.exe,
        /// so arguments must not contain cmd metacharacters such as &amp; | &lt; &gt; ^.
        /// </summary>
        public static ProcessStartInfo CreateStartInfo(string cliPath, IEnumerable<string> arguments, string workingDirectory)
        {
            var args = string.Join(" ", arguments.Select(QuoteArgument));
            var isShim = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                      || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

            var info = isShim
                ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " " + args + "\"")
                : new ProcessStartInfo(cliPath, args);

            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardInput = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = new UTF8Encoding(false);
            info.StandardErrorEncoding = new UTF8Encoding(false);
            info.WorkingDirectory = workingDirectory;
            return info;
        }

        /// <summary>Kills the process and its children (the CLI may spawn node / shell processes).</summary>
        public static void KillTree(Process process)
        {
            try
            {
                if (process.HasExited) return;
                using (var killer = Process.Start(new ProcessStartInfo("taskkill.exe", "/T /F /PID " + process.Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    killer?.WaitForExit(5000);
                }
            }
            catch
            {
                try { process.Kill(); } catch { /* already gone */ }
            }
        }

        /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
        public static string QuoteArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder("\"");
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes).Append(arg[i]);
                }
            }
            return sb.Append('"').ToString();
        }

        private static async Task<int> RunAsync(string cliPath, string arguments, StringBuilder output, CancellationToken cancellationToken)
        {
            var info = CreateStartInfo(cliPath, arguments.Split(' '), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            using (var process = new Process { StartInfo = info })
            {
                process.Start();
                process.StandardInput.Close();

                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();

                using (cancellationToken.Register(() => KillTree(process)))
                {
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                output.Append(await stdout.ConfigureAwait(false)).Append(await stderr.ConfigureAwait(false));
                return process.ExitCode;
            }
        }
    }
}
