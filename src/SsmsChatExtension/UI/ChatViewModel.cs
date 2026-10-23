using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using SsmsChatExtension.Database;
using SsmsChatExtension.Diagrams;
using SsmsChatExtension.Editor;
using SsmsChatExtension.Services;
using SsmsChatExtension.Services.Claude;

namespace SsmsChatExtension.UI
{
    public sealed class ChatViewModel : INotifyPropertyChanged
    {
        private static readonly Regex CodeFence = new Regex(@"```[a-zA-Z]*\r?\n(?<code>.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

        private readonly IChatService _service;
        private readonly Dispatcher _dispatcher;
        private CancellationTokenSource _cts;

        private string _inputText = string.Empty;
        private bool _isBusy;
        private bool _includeEditorContext = true;
        private string _statusText = string.Empty;
        private bool _isHistoryOpen;
        private ChatConversation _conversation = new ChatConversation();

        public ChatViewModel(IChatService service, ClaudeAccountViewModel account)
        {
            _service = service;
            Account = account;
            _dispatcher = Dispatcher.CurrentDispatcher;

            Messages = new ObservableCollection<ChatMessage>();

            SendCommand = new RelayCommand(async _ => await SendAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(InputText));
            StopCommand = new RelayCommand(_ => { var cts = _cts; if (cts != null) cts.Cancel(); }, _ => IsBusy);
            ClearCommand = new RelayCommand(_ => NewChat(), _ => !IsBusy && Messages.Count > 0);
            ToggleHistoryCommand = new RelayCommand(_ => { IsHistoryOpen = !IsHistoryOpen; if (IsHistoryOpen) LoadHistory(); });
            OpenConversationCommand = new RelayCommand(p => OpenConversation(p as ChatConversation), p => !IsBusy && p is ChatConversation);
            DeleteConversationCommand = new RelayCommand(p => DeleteConversation(p as ChatConversation), p => p is ChatConversation);
            DatabaseDiagramCommand = new RelayCommand(async _ => await ShowDatabaseDiagramAsync(), _ => !IsCreatingDiagram);
            History = new ObservableCollection<ChatConversation>();
            InsertCommand = new RelayCommand(async p => await InsertAsync(p as ChatMessage), p => p is ChatMessage m && !string.IsNullOrWhiteSpace(m.Text));
            CopyCommand = new RelayCommand(p => Copy(p as ChatMessage), p => p is ChatMessage m && !string.IsNullOrWhiteSpace(m.Text));
        }

        public ObservableCollection<ChatMessage> Messages { get; }

        public string ServiceName { get { return _service.Name; } }

        /// <summary>Claude sign-in and settings (header of the window).</summary>
        public ClaudeAccountViewModel Account { get; }

        /// <summary>Re-reads <see cref="ServiceName"/>, for example after the model setting changed.</summary>
        public void RefreshServiceName()
        {
            OnPropertyChanged(nameof(ServiceName));
        }

        public ICommand SendCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand ToggleHistoryCommand { get; }
        public ICommand OpenConversationCommand { get; }
        public ICommand DeleteConversationCommand { get; }
        public ICommand DatabaseDiagramCommand { get; }
        public ICommand InsertCommand { get; }

        /// <summary>Saved conversations, newest first (shown when <see cref="IsHistoryOpen"/>).</summary>
        public ObservableCollection<ChatConversation> History { get; }

        public bool IsHistoryOpen
        {
            get { return _isHistoryOpen; }
            set { if (_isHistoryOpen != value) { _isHistoryOpen = value; OnPropertyChanged(); } }
        }

        private bool _isCreatingDiagram;

        public bool IsCreatingDiagram
        {
            get { return _isCreatingDiagram; }
            private set { if (_isCreatingDiagram != value) { _isCreatingDiagram = value; OnPropertyChanged(); } }
        }
        public ICommand CopyCommand { get; }

        /// <summary>Raised whenever the message list grew or a reply got longer, so the view can scroll down.</summary>
        public event EventHandler ScrollRequested;

        public string InputText
        {
            get { return _inputText; }
            set { if (_inputText != value) { _inputText = value; OnPropertyChanged(); } }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set { if (_isBusy != value) { _isBusy = value; OnPropertyChanged(); } }
        }

        public bool IncludeEditorContext
        {
            get { return _includeEditorContext; }
            set { if (_includeEditorContext != value) { _includeEditorContext = value; OnPropertyChanged(); } }
        }

        public string StatusText
        {
            get { return _statusText; }
            private set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private async Task SendAsync()
        {
            var text = (InputText ?? string.Empty).Trim();
            if (text.Length == 0 || IsBusy) return;

            InputText = string.Empty;
            StatusText = string.Empty;
            IsBusy = true;

            var reply = new ChatMessage(ChatRole.Assistant, string.Empty);
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            try
            {
                string editorContext = null;
                if (IncludeEditorContext)
                {
                    try { editorContext = await SsmsEditor.GetSelectedOrAllTextAsync(); }
                    catch { /* no active query window - just chat without context */ }
                }

                Messages.Add(new ChatMessage(ChatRole.User, text));
                Messages.Add(reply);
                RequestScroll();

                var turns = Messages
                    .Where(m => !ReferenceEquals(m, reply) && m.Role != ChatRole.System)
                    .Select(m => new ChatTurn(m.Role, m.Text))
                    .ToList();

                var request = new ChatRequest(turns, editorContext) { SessionId = _conversation.SessionId };
                var conversation = _conversation;
                try
                {
                    await _service.GetReplyAsync(request, chunk => AppendToReply(reply, chunk), token);
                }
                finally
                {
                    conversation.SessionId = request.SessionId;
                }
            }
            catch (OperationCanceledException)
            {
                reply.Text = reply.Text.Length == 0 ? "(stopped)" : reply.Text + "\n\n(stopped)";
            }
            catch (Exception ex)
            {
                reply.Text = (reply.Text + "\n\n[error] " + ex.Message).Trim();
            }
            finally
            {
                IsBusy = false;
                var cts = _cts;
                _cts = null;
                if (cts != null) cts.Dispose();
                RequestScroll();
                CommandManager.InvalidateRequerySuggested();

                // Background priority: runs after the reply chunks still queued on the dispatcher.
                var conversation = _conversation;
                _ = _dispatcher.BeginInvoke(new Action(() => SaveConversation(conversation)), DispatcherPriority.Background);
            }
        }

        private void NewChat()
        {
            Messages.Clear();
            StatusText = string.Empty;
            _conversation = new ChatConversation();
        }

        private void SaveConversation(ChatConversation conversation)
        {
            if (!ReferenceEquals(conversation, _conversation)) return; // the user switched chats meanwhile
            conversation.Messages = Messages
                .Where(m => m.Text.Length > 0)
                .Select(m => new SavedMessage { Role = m.Role, Text = m.Text })
                .ToList();
            try
            {
                ChatHistoryStore.Save(conversation);
                if (IsHistoryOpen) LoadHistory();
            }
            catch (Exception ex)
            {
                StatusText = "Could not save chat history: " + ex.Message;
            }
        }

        private void LoadHistory()
        {
            History.Clear();
            try
            {
                foreach (var conversation in ChatHistoryStore.List()) History.Add(conversation);
            }
            catch (Exception ex)
            {
                StatusText = "Could not read chat history: " + ex.Message;
            }
        }

        private void OpenConversation(ChatConversation conversation)
        {
            if (conversation == null || IsBusy) return;

            Messages.Clear();
            foreach (var saved in conversation.Messages) Messages.Add(new ChatMessage(saved.Role, saved.Text));
            _conversation = conversation;
            IsHistoryOpen = false;
            StatusText = "Opened \"" + conversation.Title + "\" - new messages continue that Claude session.";
            RequestScroll();
        }

        private void DeleteConversation(ChatConversation conversation)
        {
            if (conversation == null) return;
            try
            {
                ChatHistoryStore.Delete(conversation.Id);
                History.Remove(conversation);
                if (ReferenceEquals(conversation, _conversation)) _conversation = new ChatConversation();
            }
            catch (Exception ex)
            {
                StatusText = "Could not delete chat: " + ex.Message;
            }
        }

        /// <summary>One click: ER diagram of the active query window's database, built from the catalog (no AI call).</summary>
        private async Task ShowDatabaseDiagramAsync()
        {
            IsCreatingDiagram = true;
            StatusText = "Building database diagram...";
            try
            {
                var connection = await SsmsConnection.GetAsync(ClaudeSettings.Load().ConnectionStringOverride);
                if (connection == null)
                {
                    StatusText = "Click into a connected query window first (or set a connection string in Settings).";
                    return;
                }

                await TaskScheduler.Default;
                string mermaid, summary, database;
                using (var db = connection.Factory())
                {
                    if (db.State != System.Data.ConnectionState.Open) db.Open();
                    var built = SchemaDiagram.Build(db, null);
                    mermaid = built.Mermaid;
                    summary = built.Summary;
                    database = db.Database;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DiagramViewer.Show(database + " ER diagram", mermaid, summary);
                StatusText = "Opened the diagram in your browser: " + summary;
            }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText = "Diagram failed: " + ex.Message;
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                IsCreatingDiagram = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void AppendToReply(ChatMessage reply, string chunk)
        {
            // The chat service may call us from any thread.
            _dispatcher.BeginInvoke(new Action(() =>
            {
                reply.Text += chunk;
                RequestScroll();
            }));
        }

        private async Task InsertAsync(ChatMessage message)
        {
            if (message == null) return;
            try
            {
                var ok = await SsmsEditor.InsertTextAsync(ExtractCode(message.Text));
                StatusText = ok ? "Inserted into the active query window." : "No active query window to insert into.";
            }
            catch (Exception ex)
            {
                StatusText = "Insert failed: " + ex.Message;
            }
        }

        private void Copy(ChatMessage message)
        {
            if (message == null) return;
            try
            {
                Clipboard.SetText(message.Text);
                StatusText = "Copied to clipboard.";
            }
            catch (Exception ex)
            {
                StatusText = "Copy failed: " + ex.Message;
            }
        }

        /// <summary>Returns the first ``` code block of a reply, or the whole reply when it has none.</summary>
        private static string ExtractCode(string text)
        {
            var match = CodeFence.Match(text ?? string.Empty);
            return match.Success ? match.Groups["code"].Value.TrimEnd() : text;
        }

        private void RequestScroll()
        {
            ScrollRequested?.Invoke(this, EventArgs.Empty);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
