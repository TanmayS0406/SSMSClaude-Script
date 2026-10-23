using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SsmsChatExtension.Services
{
    public enum ChatRole
    {
        User,
        Assistant,
        System
    }

    /// <summary>A message shown in the chat list. Text is mutable so replies can stream in.</summary>
    public sealed class ChatMessage : INotifyPropertyChanged
    {
        private string _text;

        public ChatMessage(ChatRole role, string text)
        {
            Role = role;
            _text = text ?? string.Empty;
        }

        public ChatRole Role { get; }

        public string Text
        {
            get { return _text; }
            set
            {
                if (_text == value) return;
                _text = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public bool IsUser { get { return Role == ChatRole.User; } }
        public bool IsAssistant { get { return Role == ChatRole.Assistant; } }
        public bool IsSystem { get { return Role == ChatRole.System; } }

        public string DisplayName
        {
            get
            {
                switch (Role)
                {
                    case ChatRole.User: return "You";
                    case ChatRole.Assistant: return "Assistant";
                    default: return "Info";
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>Immutable snapshot of one message, handed to the chat service.</summary>
    public sealed class ChatTurn
    {
        public ChatTurn(ChatRole role, string text)
        {
            Role = role;
            Text = text ?? string.Empty;
        }

        public ChatRole Role { get; }
        public string Text { get; }
    }

    /// <summary>Everything a chat service needs to answer one user message.</summary>
    public sealed class ChatRequest
    {
        public ChatRequest(IReadOnlyList<ChatTurn> turns, string editorContext)
        {
            Turns = turns;
            EditorContext = editorContext;
        }

        /// <summary>The conversation so far; the last item is the new user message.</summary>
        public IReadOnlyList<ChatTurn> Turns { get; }

        /// <summary>
        /// Selected text (or the whole script) from the active SSMS query window,
        /// or null when the user switched "include query" off / nothing is open.
        /// </summary>
        public string EditorContext { get; }
    }
}
