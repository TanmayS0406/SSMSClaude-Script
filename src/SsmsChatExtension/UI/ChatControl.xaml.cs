using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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

            var account = new ClaudeAccountViewModel();
            _viewModel = new ChatViewModel(ChatServiceProvider.Current, account);
            _viewModel.ScrollRequested += OnScrollRequested;
            account.SettingsSaved += (s, e) => _viewModel.RefreshServiceName();
            DataContext = _viewModel;

            Loaded += OnFirstLoaded;
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

        // Enter sends, Shift+Enter inserts a new line.
        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                if (_viewModel.SendCommand.CanExecute(null))
                {
                    _viewModel.SendCommand.Execute(null);
                }
            }
        }
    }
}
