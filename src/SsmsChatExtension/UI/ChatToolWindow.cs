using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

namespace SsmsChatExtension.UI
{
    /// <summary>The dockable tool window that hosts <see cref="ChatControl"/>.</summary>
    [Guid(PackageGuids.ChatToolWindowString)]
    public sealed class ChatToolWindow : ToolWindowPane
    {
        public ChatToolWindow() : base(null)
        {
            Caption = "SQL Chat";
            BitmapImageMoniker = KnownMonikers.StatusInformation;
            Content = new ChatControl();
        }
    }
}
