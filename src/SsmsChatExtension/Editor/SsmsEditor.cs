using System;
using System.Linq;
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

        /// <summary>Name, path, full text and selection of the active query window, or null when none is open.</summary>
        public static async Task<EditorSnapshot> GetActiveDocumentAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetDteAsync();
            var document = dte?.ActiveDocument;
            var textDocument = document?.Object("TextDocument") as TextDocument;
            if (textDocument == null) return null;

            var text = textDocument.StartPoint.CreateEditPoint().GetText(textDocument.EndPoint);
            var selection = document.Selection as TextSelection;
            return new EditorSnapshot(document.Name, document.FullName, text, selection?.Text ?? string.Empty);
        }

        /// <summary>Replaces the whole text of the active query window as one undo step (Ctrl+Z reverts it).</summary>
        public static async Task<bool> ReplaceAllTextAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetDteAsync();
            var textDocument = dte?.ActiveDocument?.Object("TextDocument") as TextDocument;
            if (textDocument == null) return false;

            var openedUndo = false;
            try
            {
                if (!dte.UndoContext.IsOpen)
                {
                    dte.UndoContext.Open("SSMS Claude Chat edit");
                    openedUndo = true;
                }
                var start = textDocument.StartPoint.CreateEditPoint();
                start.Delete(textDocument.EndPoint);
                start.Insert(text ?? string.Empty);
            }
            finally
            {
                if (openedUndo) dte.UndoContext.Close();
            }
            return true;
        }

        /// <summary>
        /// Opens a new query window, connected like the active one when possible, and fills it with <paramref name="text"/>.
        /// </summary>
        public static async Task<bool> OpenNewQueryAsync(string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var opened = false;
            try
            {
                var factory = Database.SsmsConnection.GetScriptFactory();
                var connection = Database.SsmsConnection.GetActiveUIConnectionInfo();
                var method = factory?.GetType().GetInterfaces()
                    .SelectMany(i => i.GetMethods())
                    .FirstOrDefault(m => m.Name == "CreateNewBlankScript"
                                         && m.GetParameters().Length == (connection != null ? 3 : 1)
                                         && (connection == null || m.GetParameters()[1].ParameterType.IsInstanceOfType(connection)));
                if (method != null)
                {
                    var scriptType = Enum.Parse(method.GetParameters()[0].ParameterType, "Sql");
                    method.Invoke(factory, connection != null ? new[] { scriptType, connection, null } : new[] { scriptType });
                    opened = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SsmsChat: CreateNewBlankScript failed: " + ex);
            }

            if (!opened)
            {
                var dte = await GetDteAsync();
                if (dte == null) return false;
                dte.ExecuteCommand("File.NewQuery");
            }

            return await ReplaceAllTextAsync(text);
        }

        /// <summary>
        /// Executes the active query window like F5 (only the selection, if there is one); results appear in SSMS.
        /// </summary>
        public static async Task<bool> ExecuteActiveQueryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dte = await GetDteAsync();
            var document = dte?.ActiveDocument;
            if (document == null) return false;

            document.Activate();
            dte.ExecuteCommand("Query.Execute");
            return true;
        }

        private static async Task<DTE2> GetDteAsync()
        {
            var service = await AsyncServiceProvider.GlobalProvider.GetServiceAsync(typeof(DTE));
            return service as DTE2;
        }
    }

    /// <summary>State of the active query window at one moment.</summary>
    internal sealed class EditorSnapshot
    {
        public EditorSnapshot(string name, string path, string text, string selectedText)
        {
            Name = name;
            Path = path;
            Text = text ?? string.Empty;
            SelectedText = selectedText ?? string.Empty;
        }

        public string Name { get; }
        public string Path { get; }
        public string Text { get; }
        public string SelectedText { get; }
    }
}
