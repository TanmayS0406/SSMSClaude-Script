using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
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

            PendingAttachments = new ObservableCollection<ChatAttachment>();
            SendCommand = new RelayCommand(async _ => await SendAsync(),
                                           _ => !IsBusy && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0));
            AttachFileCommand = new RelayCommand(_ => BrowseForFiles(), _ => PendingAttachments.Count < ChatAttachment.MaxPerMessage);
            ScreenshotCommand = new RelayCommand(_ => StartScreenshot(),
                                                 _ => _snipTimer == null && PendingAttachments.Count < ChatAttachment.MaxPerMessage);
            RemoveAttachmentCommand = new RelayCommand(p => PendingAttachments.Remove(p as ChatAttachment), p => p is ChatAttachment);
            ChatAttachment.DeleteOld();
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
        public ICommand AttachFileCommand { get; }
        public ICommand ScreenshotCommand { get; }
        public ICommand RemoveAttachmentCommand { get; }

        /// <summary>Files and screenshots that go with the next message (shown above the input box).</summary>
        public ObservableCollection<ChatAttachment> PendingAttachments { get; }

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
            var attachments = PendingAttachments.ToList();
            if ((text.Length == 0 && attachments.Count == 0) || IsBusy) return;

            InputText = string.Empty;
            PendingAttachments.Clear();
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

                Messages.Add(new ChatMessage(ChatRole.User, DescribeUserMessage(text, attachments)));
                Messages.Add(reply);
                RequestScroll();

                var turns = Messages
                    .Where(m => !ReferenceEquals(m, reply) && m.Role != ChatRole.System)
                    .Select(m => new ChatTurn(m.Role, m.Text))
                    .ToList();

                var request = new ChatRequest(turns, editorContext, attachments) { SessionId = _conversation.SessionId };
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

        /// <summary>Text of the user's bubble (and history): what they typed plus one line per attachment.</summary>
        private static string DescribeUserMessage(string text, IReadOnlyList<ChatAttachment> attachments)
        {
            var lines = attachments.Select(a => "\U0001F4CE " + a.Name);
            return string.Join("\n", new[] { text }.Where(t => t.Length > 0).Concat(lines));
        }

        // ===== Attachments =====

        private DispatcherTimer _snipTimer;
        private uint _snipClipboardSequence;
        private DateTime _snipStarted;

        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        /// <summary>Adds files (from the file picker, drag and drop or a pasted file list).</summary>
        public void AddFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (Directory.Exists(path))
                {
                    StatusText = "Folders cannot be attached: " + Path.GetFileName(path);
                    continue;
                }
                if (!TryReserveSlot()) break;
                try
                {
                    PendingAttachments.Add(ChatAttachment.FromFile(path));
                    StatusText = string.Empty;
                }
                catch (Exception ex)
                {
                    StatusText = "Could not attach " + Path.GetFileName(path) + ": " + ex.Message;
                }
            }
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>
        /// Attaches the image on the clipboard as a PNG screenshot. Returns false when the clipboard holds no image.
        /// </summary>
        public bool AddClipboardImage()
        {
            byte[] png;
            try
            {
                png = ReadClipboardImageAsPng();
            }
            catch (Exception ex)
            {
                StatusText = "Could not read the clipboard image: " + ex.Message;
                return true;
            }
            if (png == null) return false;

            if (TryReserveSlot())
            {
                try
                {
                    PendingAttachments.Add(ChatAttachment.FromBytes("screenshot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png", png));
                    StatusText = "Screenshot attached.";
                }
                catch (Exception ex)
                {
                    StatusText = "Could not attach the screenshot: " + ex.Message;
                }
            }
            CommandManager.InvalidateRequerySuggested();
            return true;
        }

        private bool TryReserveSlot()
        {
            if (PendingAttachments.Count < ChatAttachment.MaxPerMessage) return true;
            StatusText = "At most " + ChatAttachment.MaxPerMessage + " attachments per message.";
            return false;
        }

        private void BrowseForFiles()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Attach files",
                Multiselect = true,
                Filter = "All files (*.*)|*.*|SQL scripts (*.sql)|*.sql|Images (*.png;*.jpg;*.jpeg;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.gif;*.bmp|"
                         + "Text and data (*.txt;*.csv;*.json;*.xml;*.log)|*.txt;*.csv;*.json;*.xml;*.log|Execution plans (*.sqlplan)|*.sqlplan|PDF (*.pdf)|*.pdf"
            };
            if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
        }

        /// <summary>
        /// Opens the Windows snipping overlay (as Win+Shift+S does) and attaches the snip as soon as it lands on the clipboard.
        /// </summary>
        private void StartScreenshot()
        {
            _snipClipboardSequence = GetClipboardSequenceNumber();
            try
            {
                Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
            }
            catch (Exception)
            {
                StatusText = "Could not open the snipping tool. Press Win+Shift+S, then Ctrl+V in the message box.";
                return;
            }

            StatusText = "Select an area of the screen to attach (Esc cancels)...";
            _snipStarted = DateTime.UtcNow;
            _snipTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
            _snipTimer.Tick += OnSnipTimerTick;
            _snipTimer.Start();
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnSnipTimerTick(object sender, EventArgs e)
        {
            var changed = GetClipboardSequenceNumber() != _snipClipboardSequence;
            var timedOut = DateTime.UtcNow - _snipStarted > TimeSpan.FromMinutes(1);
            if (!changed && !timedOut) return;

            _snipTimer.Stop();
            _snipTimer.Tick -= OnSnipTimerTick;
            _snipTimer = null;

            if (!changed || !AddClipboardImage())
            {
                StatusText = changed ? "The clipboard did not get an image, so nothing was attached." : string.Empty;
            }
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>The clipboard image as PNG bytes, or null when there is none.</summary>
        private static byte[] ReadClipboardImageAsPng()
        {
            // Snipping Tool also puts a "PNG" format on the clipboard; it keeps colours and transparency exact,
            // unlike the DIB that Clipboard.GetImage reads.
            if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is MemoryStream pngStream)
            {
                return pngStream.ToArray();
            }
            if (!Clipboard.ContainsImage()) return null;

            var image = Clipboard.GetImage();
            if (image == null) return null;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        private void NewChat()
        {
            Messages.Clear();
            PendingAttachments.Clear();
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

        /// <summary>
        /// DB diagram button: pick databases on the active connection's server, then draw an ER diagram from the
        /// catalog (no AI call) with declared and inferred relationships, also across databases.
        /// </summary>
        private async Task ShowDatabaseDiagramAsync()
        {
            IsCreatingDiagram = true;
            StatusText = "Reading databases...";
            try
            {
                var connection = await SsmsConnection.GetAsync(ClaudeSettings.Load().ConnectionStringOverride);
                if (connection == null)
                {
                    StatusText = "Click into a connected query window first (or set a connection string in Settings).";
                    return;
                }

                await TaskScheduler.Default;
                IReadOnlyList<(string Name, bool IsCurrent)> databases;
                using (var db = connection.Factory())
                {
                    if (db.State != System.Data.ConnectionState.Open) db.Open();
                    databases = SchemaDiagram.ListDatabases(db);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dialog = new DiagramDialog(connection.Server, databases);
                if (dialog.ShowModal() != true)
                {
                    StatusText = string.Empty;
                    return;
                }
                var options = dialog.Options;
                StatusText = "Building diagram of " + string.Join(", ", options.Databases) + "...";

                await TaskScheduler.Default;
                DiagramResult result;
                using (var db = connection.Factory())
                {
                    if (db.State != System.Data.ConnectionState.Open) db.Open();
                    result = SchemaDiagram.Build(db, options);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                DiagramViewer.Show(result.Title, result.Mermaid, result.Summary, result.DetailsHtml);
                StatusText = "Opened the diagram in your browser: " + result.Summary;
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
