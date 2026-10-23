using System;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;

namespace SsmsChatExtension.Editor
{
    /// <summary>
    /// Best-effort bridge to the active SSMS query window through the DTE automation model.
    /// Every method returns null / false instead of throwing when no query window is active.
    /// </summary>
    internal static class SsmsEditor
    {
        /// <summary>Selected text of the active document, or the whole script if nothing is selected.</summary>
        public static async Task<string> GetSelectedOrAllTextAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetDteAsync();
            var document = dte?.ActiveDocument;
            if (document == null) return null;

            var selection = document.Selection as TextSelection;
            if (selection != null && !string.IsNullOrEmpty(selection.Text))
            {
                return selection.Text;
            }

            var textDocument = document.Object("TextDocument") as TextDocument;
            if (textDocument == null) return null;

            var text = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        /// <summary>Replace the selection (or insert at the caret) in the active query window.</summary>
        public static async Task<bool> InsertTextAsync(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetDteAsync();
            var document = dte?.ActiveDocument;
            var selection = document?.Selection as TextSelection;
            if (selection == null) return false;

            selection.Insert(text, (int)vsInsertFlags.vsInsertFlagsContainNewText);
            return true;
        }

        private static async Task<DTE2> GetDteAsync()
        {
            var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(DTE));
            return service as DTE2;
        }
    }
}
