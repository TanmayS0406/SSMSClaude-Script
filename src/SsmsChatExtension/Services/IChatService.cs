using System;
using System.Threading;
using System.Threading.Tasks;

namespace SsmsChatExtension.Services
{
    /// <summary>
    /// The seam for plugging in an AI provider. Implement this interface (OpenAI, Azure OpenAI,
    /// Anthropic, a local Ollama model, ...) and assign it to <see cref="ChatServiceProvider.Current"/>.
    /// </summary>
    public interface IChatService
    {
        /// <summary>Short name shown in the chat window header.</summary>
        string Name { get; }

        /// <summary>
        /// Produce the assistant reply for <paramref name="request"/>.
        /// Call <paramref name="onToken"/> for every chunk of text as it arrives (any thread is fine;
        /// the window marshals to the UI thread). Honour <paramref name="cancellationToken"/> so the
        /// Stop button works. Throw to show an error in the chat.
        /// </summary>
        Task GetReplyAsync(ChatRequest request, Action<string> onToken, CancellationToken cancellationToken);
    }

    /// <summary>Single place that decides which <see cref="IChatService"/> the window uses.</summary>
    public static class ChatServiceProvider
    {
        private static IChatService _current = new EchoChatService();

        public static IChatService Current
        {
            get { return _current; }
            set { _current = value ?? new EchoChatService(); }
        }
    }
}
