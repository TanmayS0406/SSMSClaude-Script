using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SsmsChatExtension.Services.Claude;

namespace SsmsChatExtension.UI
{
    /// <summary>Theme and accent picker (palette button in the header). Changes apply and save immediately.</summary>
    public sealed class AppearanceViewModel : INotifyPropertyChanged
    {
        private ChatTheme _theme;
        private ChatAccent _accent;
        private bool _isOpen;

        public AppearanceViewModel()
        {
            var settings = ClaudeSettings.Load();
            _theme = ChatTheme.Find(settings.Theme);
            _accent = ChatAccent.Find(settings.Accent);

            ToggleCommand = new RelayCommand(_ => IsOpen = !IsOpen);
            OpenLinkCommand = new RelayCommand(p => OpenLink(p as string), p => p is string);
        }

        /// <summary>Raised when the theme or accent changed; the view re-applies its brushes.</summary>
        public event EventHandler Changed;

        public IReadOnlyList<ChatTheme> Themes { get { return ChatTheme.All; } }

        public IReadOnlyList<ChatAccent> Accents { get { return ChatAccent.All; } }

        public ICommand ToggleCommand { get; }

        /// <summary>Opens a URL (documentation, website, sponsor page) in the default browser.</summary>
        public ICommand OpenLinkCommand { get; }

        public bool IsOpen { get { return _isOpen; } set { Set(ref _isOpen, value); } }

        public ChatTheme Theme
        {
            get { return _theme; }
            set
            {
                if (value == null || value == _theme) return;
                _theme = value;
                OnPropertyChanged();
                SaveAndNotify();
            }
        }

        public ChatAccent Accent
        {
            get { return _accent; }
            set
            {
                if (value == null || value == _accent) return;
                _accent = value;
                OnPropertyChanged();
                SaveAndNotify();
            }
        }

        private void SaveAndNotify()
        {
            Changed?.Invoke(this, EventArgs.Empty);
            try
            {
                var settings = ClaudeSettings.Load();
                settings.Theme = _theme.Name;
                settings.Accent = _accent.Name;
                settings.Save();
            }
            catch
            {
                // Not saved - the theme still applies for this session.
            }
        }

        private static void OpenLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch
            {
                // No default browser - nothing useful to do.
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            OnPropertyChanged(name);
        }

        private void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
