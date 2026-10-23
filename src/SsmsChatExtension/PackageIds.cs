using System;

namespace SsmsChatExtension
{
    /// <summary>GUIDs. Must match VSCommandTable.vsct and source.extension.vsixmanifest.</summary>
    internal static class PackageGuids
    {
        public const string SsmsChatString = "CD45CFA1-0DA4-44D0-9819-74F42F0E10EF";
        public const string SsmsChatCmdSetString = "4DD2D69B-C79F-44AD-8AF6-64B0DDE24184";
        public const string ChatToolWindowString = "F1261746-AFBD-427E-B4D6-8D17036A16AD";

        public static readonly Guid SsmsChatCmdSet = new Guid(SsmsChatCmdSetString);
        public static readonly Guid ChatToolWindow = new Guid(ChatToolWindowString);
    }

    /// <summary>Command IDs. Must match VSCommandTable.vsct.</summary>
    internal static class PackageIds
    {
        public const int ChatMenuGroup = 0x1020;
        public const int ShowChatWindow = 0x0100;
    }
}
