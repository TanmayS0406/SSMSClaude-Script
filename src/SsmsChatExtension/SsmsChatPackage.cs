using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace SsmsChatExtension
{
    /// <summary>
    /// Entry point of the extension. Registers the "SQL Chat" menu command (View menu)
    /// and the dockable chat tool window.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("SQL Chat for SSMS", "Dockable chat window for SQL Server Management Studio", "1.4.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(UI.ChatToolWindow), Style = VsDockStyle.Float, Width = 420, Height = 640)]
    [Guid(PackageGuids.SsmsChatString)]
    public sealed class SsmsChatPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null)
            {
                return;
            }

            var showChatId = new CommandID(PackageGuids.SsmsChatCmdSet, PackageIds.ShowChatWindow);
            var showChat = new MenuCommand(
                (sender, args) => JoinableTaskFactory.RunAsync(ShowChatWindowAsync).FileAndForget("SsmsChat/ShowChatWindow"),
                showChatId);
            commandService.AddCommand(showChat);
        }

        private async Task ShowChatWindowAsync()
        {
            await ShowToolWindowAsync(typeof(UI.ChatToolWindow), 0, true, DisposalToken);
        }

        // ---- async tool window plumbing (the window itself is created in ChatToolWindow's constructor) ----

        public override IVsAsyncToolWindowFactory GetAsyncToolWindowFactory(Guid toolWindowType)
        {
            return toolWindowType.Equals(PackageGuids.ChatToolWindow) ? this : null;
        }

        protected override string GetToolWindowTitle(Type toolWindowType, int id)
        {
            return toolWindowType == typeof(UI.ChatToolWindow) ? "SQL Chat" : base.GetToolWindowTitle(toolWindowType, id);
        }

        protected override System.Threading.Tasks.Task<object> InitializeToolWindowAsync(Type toolWindowType, int id, CancellationToken cancellationToken)
        {
            return Task.FromResult<object>(null);
        }
    }
}
