using System;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio.PlatformUI;

namespace SsmsChatExtension.UI
{
    /// <summary>
    /// Asks the user to approve SQL that Claude wants to run. Unlike a message box it can be resized and the script
    /// scrolls, so a long script can be read in full before answering. "No" is the default button (Enter and Esc).
    /// </summary>
    public partial class ApprovalDialog : DialogWindow
    {
        /// <summary>The longest script put in the box; a TextBox gets slow past a few hundred thousand characters.</summary>
        internal const int MaxShownLength = 200000;

        private readonly string _sql;

        internal ApprovalDialog(string title, string reason, string target, string sql, string question)
        {
            InitializeComponent();
            _sql = sql ?? string.Empty;

            Title = title;
            ReasonText.Text = reason ?? string.Empty;
            ReasonText.Visibility = string.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible;
            TargetText.Text = target;
            QuestionText.Text = question;

            var shown = _sql.Length > MaxShownLength ? _sql.Substring(0, MaxShownLength) : _sql;
            SqlBox.Text = shown;
            SqlHeader.Text = _sql.Length > MaxShownLength
                ? "SQL to run (first " + MaxShownLength.ToString("N0") + " of " + _sql.Length.ToString("N0") + " characters):"
                : "SQL to run (" + _sql.Length.ToString("N0") + " characters):";

            // Never taller than the screen, however big the owner window is.
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            MaxWidth = SystemParameters.WorkArea.Width * 0.9;
            Loaded += (s, e) => DeclineButton.Focus();
        }

        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Wrap_Changed(object sender, RoutedEventArgs e)
        {
            // Raised once while the XAML loads, before every element exists.
            if (SqlBox == null) return;
            SqlBox.TextWrapping = WrapBox.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_sql);
            }
            catch (COMException)
            {
                // The clipboard is held by another program; nothing worth interrupting an approval for.
            }
        }
    }
}
