using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using SsmsChatExtension.Services;

namespace SsmsChatExtension.UI
{
    public partial class ChatControl : UserControl
    {
        private readonly ChatViewModel _viewModel;

        public ChatControl()
        {
            InitializeComponent();

            var appearance = new AppearanceViewModel();
            ApplyTheme(appearance);
            appearance.Changed += (s, e) => ApplyTheme(appearance);

            // "Follow SSMS" re-reads the colours when the SSMS theme changes.
            VSColorTheme.ThemeChanged += _ => ApplyTheme(appearance);

            var account = new ClaudeAccountViewModel();
            _viewModel = new ChatViewModel(ChatServiceProvider.Current, account, appearance);
            _viewModel.ScrollRequested += OnScrollRequested;
            account.SettingsSaved += (s, e) => _viewModel.RefreshServiceName();
            DataContext = _viewModel;

            Loaded += OnFirstLoaded;
        }

        private void ApplyTheme(AppearanceViewModel appearance)
        {
            appearance.Theme.Apply(this, appearance.Accent);
        }

        private void OnFirstLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnFirstLoaded;
            ThreadHelper.JoinableTaskFactory.RunAsync(_viewModel.Account.RefreshAsync).FileAndForget("SsmsChat/RefreshClaudeStatus");
        }

        private void OnScrollRequested(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => MessageScroll.ScrollToEnd()), DispatcherPriority.Background);
        }

        // Enter sends, Shift+Enter inserts a new line. Ctrl+V / Shift+Insert attach a copied image or copied files.
        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
                        || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
            if (paste && TryPasteAttachment())
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_viewModel.SendCommand.CanExecute(null))
                {
                    _viewModel.SendCommand.Execute(null);
                }
            }
        }

        /// <summary>Pastes files or an image as attachments; plain text is left to the text box.</summary>
        private bool TryPasteAttachment()
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    _viewModel.AddFiles(Clipboard.GetFileDropList().Cast<string>());
                    return true;
                }
                return !Clipboard.ContainsText() && _viewModel.AddClipboardImage();
            }
            catch (ExternalException)
            {
                return false; // clipboard busy - fall back to a normal paste
            }
        }

        private void Input_PreviewDragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void Input_PreviewDrop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                _viewModel.AddFiles(files);
                e.Handled = true;
            }
        }
    }
}
