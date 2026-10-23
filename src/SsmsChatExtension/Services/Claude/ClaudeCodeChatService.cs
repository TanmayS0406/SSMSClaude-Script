using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SsmsChatExtension.Mcp;

namespace SsmsChatExtension.Services.Claude
{
    /// <summary>
    /// Chat service backed by the Claude Code CLI, signed in with the user's own Claude account.
    /// Each message runs <c>claude -p</c> in the repository folder (so Claude can read and edit files there) and
    /// connects it to an in-process MCP server that exposes the SSMS connection (read-only) and query windows.
    /// The conversation continues across messages with <c>--resume</c>.
    /// </summary>
    public sealed class ClaudeCodeChatService : IChatService
    {
        private const string McpServerName = "ssms";

        // Must not contain cmd.exe metacharacters (& | < > ^ %): it is passed on the command line, possibly through cmd.exe.
        private const string SystemPrompt =
            "You are running inside SQL Server Management Studio (SSMS) through the SQL Chat extension, talking to a database developer. " +
            "The working directory is the user's SQL repository: you may read and edit files there when asked. " +
            "Use the ssms MCP tools to understand the database the user is connected to (get_connection_info, list_databases, list_objects, describe_object, run_query). " +
            "Never guess table, column or procedure names - look them up first. Use run_query (read-only) to explore. " +
            "When the user asks you to run, apply or fix something in the database, use execute_sql; to run the script in their query window so they see the results grid, use execute_query_window. " +
            "These may need the user's approval or be turned off; if a call is declined or refused, do not retry it - explain and offer the script instead. Only say the database changed after execute_sql succeeded. " +
            "To change the editor text use get_query_window, replace_query_window or open_new_query_window. " +
            "Put T-SQL you suggest in sql code blocks. Be concise.";

        private static readonly object ServerLock = new object();
        private static McpHttpServer _mcpServer;
        private static string _mcpConfigPath;

        private string _sessionId;

        public string Name
        {
            get
            {
                var model = ClaudeSettings.Load().Model;
                return "Claude" + (string.IsNullOrWhiteSpace(model) ? string.Empty : " (" + model.Trim() + ")");
            }
        }

        public async Task GetReplyAsync(ChatRequest request, Action<string> onToken, CancellationToken cancellationToken)
        {
            var settings = ClaudeSettings.Load();
            var cliPath = ClaudeCli.Locate(settings.ClaudePath);
            if (cliPath == null)
            {
                throw new InvalidOperationException(
                    "Claude Code CLI not found. Install it (https://claude.com/claude-code), or set its path in SQL Chat settings, then click Sign in.");
            }

            // A cleared chat (only one user message) starts a new Claude session.
            if (request.Turns.Count(t => t.Role == ChatRole.User) <= 1) _sessionId = null;

            var prompt = BuildPrompt(request);
            var writer = new ReplyWriter(onToken);

            var outcome = await RunOnceAsync(cliPath, settings, prompt, _sessionId, writer, cancellationToken).ConfigureAwait(false);
            if (outcome == RunOutcome.SessionMissing)
            {
                _sessionId = null;
                await RunOnceAsync(cliPath, settings, prompt, null, writer, cancellationToken).ConfigureAwait(false);
            }
        }

        private enum RunOutcome
        {
            Completed,
            SessionMissing
        }

        private async Task<RunOutcome> RunOnceAsync(string cliPath, ClaudeSettings settings, string prompt, string resumeSessionId,
                                                    ReplyWriter writer, CancellationToken cancellationToken)
        {
            var workingDirectory = ResolveWorkingDirectory(settings);
            var info = ClaudeCli.CreateStartInfo(cliPath, BuildArguments(settings, resumeSessionId), workingDirectory);

            using (var process = new Process { StartInfo = info })
            {
                process.Start();

                // The prompt goes through stdin: no command-line length or quoting limits.
                using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
                {
                    await stdin.WriteAsync(prompt).ConfigureAwait(false);
                }

                var stderrTask = process.StandardError.ReadToEndAsync();
                string resultError = null;
                var gotResult = false;

                using (cancellationToken.Register(() => ClaudeCli.KillTree(process)))
                {
                    string line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        JObject message;
                        try
                        {
                            message = JObject.Parse(line);
                        }
                        catch (JsonException)
                        {
                            continue; // not a stream-json line
                        }

                        if (HandleMessage(message, writer, ref resultError))
                        {
                            gotResult = true;
                        }
                    }

                    await Task.Run(() => process.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();

                var stderr = (await stderrTask.ConfigureAwait(false)).Trim();

                if (resumeSessionId != null && !writer.HasOutput
                    && ((resultError ?? string.Empty) + stderr).IndexOf("No conversation found", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return RunOutcome.SessionMissing;
                }
                if (resultError != null)
                {
                    throw new InvalidOperationException(DescribeError(resultError));
                }
                if (!gotResult && process.ExitCode != 0)
                {
                    throw new InvalidOperationException(DescribeError(stderr.Length > 0 ? stderr : "Claude Code exited with code " + process.ExitCode + "."));
                }
                return RunOutcome.Completed;
            }
        }

        /// <summary>Processes one stream-json message. Returns true for the final "result" message.</summary>
        private bool HandleMessage(JObject message, ReplyWriter writer, ref string resultError)
        {
            switch ((string)message["type"])
            {
                case "system":
                    if ((string)message["subtype"] == "init")
                    {
                        _sessionId = (string)message["session_id"] ?? _sessionId;
                        var ssms = (message["mcp_servers"] as JArray)?.OfType<JObject>()
                            .FirstOrDefault(s => (string)s["name"] == McpServerName);
                        var status = (string)ssms?["status"];
                        if (status != null && status != "connected")
                        {
                            writer.Note("SSMS database tools unavailable (MCP status: " + status + ").");
                        }
                    }
                    return false;

                case "stream_event":
                    var streamEvent = message["event"] as JObject;
                    if ((string)streamEvent?["type"] == "content_block_delta"
                        && (string)streamEvent["delta"]?["type"] == "text_delta")
                    {
                        writer.StreamText((string)streamEvent["delta"]["text"]);
                    }
                    return false;

                case "assistant":
                    var content = message["message"]?["content"] as JArray;
                    if (content != null)
                    {
                        foreach (var block in content.OfType<JObject>())
                        {
                            switch ((string)block["type"])
                            {
                                case "text":
                                    writer.CompleteText((string)block["text"]);
                                    break;
                                case "tool_use":
                                    writer.Note(DescribeToolUse((string)block["name"], block["input"] as JObject));
                                    break;
                            }
                        }
                    }
                    writer.EndMessage();
                    return false;

                case "result":
                    _sessionId = (string)message["session_id"] ?? _sessionId;

                    var denials = (message["permission_denials"] as JArray)?.OfType<JObject>()
                        .Select(d => DescribeToolUse((string)d["tool_name"], d["tool_input"] as JObject))
                        .Distinct()
                        .ToList();
                    if (denials != null && denials.Count > 0)
                    {
                        writer.Note("Not permitted in SQL Chat: " + string.Join("; ", denials));
                    }

                    if ((bool?)message["is_error"] == true)
                    {
                        resultError = (string)message["result"]
                                      ?? string.Join("; ", (message["errors"] as JArray)?.Select(e => (string)e) ?? new[] { (string)message["subtype"] });
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static IEnumerable<string> BuildArguments(ClaudeSettings settings, string resumeSessionId)
        {
            var args = new List<string>
            {
                "-p",
                "--output-format", "stream-json",
                "--verbose",
                "--include-partial-messages",
                "--mcp-config", EnsureMcpServer(),
                "--append-system-prompt", SystemPrompt,
                // Read-only git commands are handy for "what changed in the repo"; everything else stays blocked.
                "--allowedTools", "mcp__" + McpServerName, "Bash(git status:*)", "Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)",
                "--permission-mode", settings.AllowFileEdits ? "acceptEdits" : "default"
            };

            if (!string.IsNullOrWhiteSpace(settings.Model))
            {
                args.Add("--model");
                args.Add(settings.Model.Trim());
            }
            if (resumeSessionId != null)
            {
                args.Add("--resume");
                args.Add(resumeSessionId);
            }
            return args;
        }

        private static string BuildPrompt(ChatRequest request)
        {
            var last = request.Turns.LastOrDefault(t => t.Role == ChatRole.User);
            var prompt = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(request.EditorContext))
            {
                prompt.AppendLine("Context - the selected text (or whole script) of my active SSMS query window:");
                prompt.AppendLine("```sql");
                prompt.AppendLine(request.EditorContext.Trim());
                prompt.AppendLine("```");
                prompt.AppendLine();
            }

            prompt.Append(last != null ? last.Text : string.Empty);
            return prompt.ToString();
        }

        private static string ResolveWorkingDirectory(ClaudeSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.RepositoryFolder) && Directory.Exists(settings.RepositoryFolder))
            {
                return settings.RepositoryFolder;
            }
            Directory.CreateDirectory(ClaudeSettings.ScratchFolder);
            return ClaudeSettings.ScratchFolder;
        }

        /// <summary>Starts the MCP server once per SSMS process and returns the path of the --mcp-config file.</summary>
        private static string EnsureMcpServer()
        {
            lock (ServerLock)
            {
                if (_mcpServer != null) return _mcpConfigPath;

                var server = new McpHttpServer(new SsmsMcpTools(ClaudeSettings.Load));
                var config = new JObject
                {
                    ["mcpServers"] = new JObject
                    {
                        [McpServerName] = new JObject
                        {
                            ["type"] = "http",
                            ["url"] = server.Url,
                            ["headers"] = new JObject { ["Authorization"] = "Bearer " + server.Token }
                        }
                    }
                };

                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SsmsChatExtension");
                Directory.CreateDirectory(folder);
                DeleteStaleMcpConfigs(folder);
                var path = Path.Combine(folder, "mcp-" + Process.GetCurrentProcess().Id + ".json");
                File.WriteAllText(path, config.ToString(Formatting.Indented));

                _mcpServer = server;
                _mcpConfigPath = path;
                return path;
            }
        }

        /// <summary>Removes config files (and their dead tokens) left behind by SSMS processes that have exited.</summary>
        private static void DeleteStaleMcpConfigs(string folder)
        {
            foreach (var file in Directory.GetFiles(folder, "mcp-*.json"))
            {
                try
                {
                    var pidText = Path.GetFileNameWithoutExtension(file).Substring("mcp-".Length);
                    if (!int.TryParse(pidText, out var pid)) continue;
                    try
                    {
                        using (Process.GetProcessById(pid)) continue;
                    }
                    catch (ArgumentException)
                    {
                        File.Delete(file); // no such process
                    }
                }
                catch (IOException)
                {
                    // In use or already gone.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private static string DescribeError(string error)
        {
            var lower = error.ToLowerInvariant();
            if (lower.Contains("login") || lower.Contains("api key") || lower.Contains("authenticat") || lower.Contains("oauth"))
            {
                return error + Environment.NewLine + "Click \"Sign in with Claude\" at the top of the SQL Chat window.";
            }
            return error;
        }

        private static string DescribeToolUse(string name, JObject input)
        {
            name = name ?? "tool";
            var label = name.StartsWith("mcp__" + McpServerName + "__", StringComparison.Ordinal)
                ? "SSMS " + name.Substring(("mcp__" + McpServerName + "__").Length)
                : name;

            var detail = (string)input?["file_path"] ?? (string)input?["path"] ?? (string)input?["object"]
                         ?? (string)input?["pattern"] ?? (string)input?["command"] ?? (string)input?["sql"] ?? (string)input?["url"];
            if (string.IsNullOrWhiteSpace(detail)) return label;

            detail = detail.Trim().Replace("\r", " ").Replace("\n", " ");
            if (detail.Length > 90) detail = detail.Substring(0, 90) + "...";
            return label + ": " + detail;
        }

        /// <summary>
        /// Turns stream-json events into readable chat text. Text arrives twice (as deltas and in the complete
        /// assistant message); the complete copy is only used when no deltas were seen for it.
        /// </summary>
        private sealed class ReplyWriter
        {
            private readonly Action<string> _onToken;
            private bool _streamedThisMessage;
            private bool _needsBreak;
            private char _last = '\n';

            public ReplyWriter(Action<string> onToken)
            {
                _onToken = onToken;
            }

            public bool HasOutput { get; private set; }

            public void StreamText(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                _streamedThisMessage = true;
                Write(text);
            }

            public void CompleteText(string text)
            {
                if (!_streamedThisMessage && !string.IsNullOrEmpty(text)) Write(text);
            }

            public void EndMessage()
            {
                if (_streamedThisMessage) _needsBreak = true;
                _streamedThisMessage = false;
            }

            /// <summary>Writes an activity line such as "› Edit: C:\repo\proc.sql" on its own line.</summary>
            public void Note(string text)
            {
                if (HasOutput && _last != '\n') Emit("\n");
                if (HasOutput && _needsBreak) Emit("\n");
                _needsBreak = false;
                Emit("\u203A " + text + "\n");
                _needsBreak = true;
            }

            private void Write(string text)
            {
                if (_needsBreak && HasOutput)
                {
                    Emit(_last == '\n' ? "\n" : "\n\n");
                }
                _needsBreak = false;
                Emit(text);
            }

            private void Emit(string text)
            {
                HasOutput = true;
                _last = text[text.Length - 1];
                _onToken(text);
            }
        }
    }
}
