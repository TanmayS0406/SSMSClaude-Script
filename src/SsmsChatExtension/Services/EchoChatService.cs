using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsChatExtension.Services
{
    /// <summary>
    /// Placeholder "AI" that just echoes the question back, streamed word by word so the UI
    /// behaves like a real model. Replace with a real <see cref="IChatService"/> later.
    /// </summary>
    public sealed class EchoChatService : IChatService
    {
        public string Name { get { return "Echo (no AI connected)"; } }

        public async Task GetReplyAsync(ChatRequest request, Action<string> onToken, CancellationToken cancellationToken)
        {
            var last = request.Turns.LastOrDefault(t => t.Role == ChatRole.User);

            var reply = new StringBuilder();
            reply.AppendLine("No AI provider is connected yet - this is the echo stub.");
            reply.AppendLine();
            reply.AppendLine("You said: " + (last != null ? last.Text : string.Empty));

            if (!string.IsNullOrEmpty(request.EditorContext))
            {
                reply.AppendLine();
                reply.AppendLine("Query context received (" + request.EditorContext.Length + " characters):");
                reply.AppendLine("```sql");
                reply.AppendLine(request.EditorContext.Trim());
                reply.AppendLine("```");
            }

            // Stream it out in small chunks to exercise the streaming UI.
            foreach (var chunk in SplitKeepingWhitespace(reply.ToString()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                onToken(chunk);
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string[] SplitKeepingWhitespace(string text)
        {
            var parts = new System.Collections.Generic.List<string>();
            var current = new StringBuilder();
            foreach (var ch in text)
            {
                current.Append(ch);
                if (char.IsWhiteSpace(ch))
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            if (current.Length > 0) parts.Add(current.ToString());
            return parts.ToArray();
        }
    }
}
