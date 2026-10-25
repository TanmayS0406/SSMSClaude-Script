using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;

namespace SsmsChatExtension.UI
{
    /// <summary>
    /// A colour theme for the chat window. A theme is just a background, a text colour and a default accent;
    /// every other brush (surfaces, borders, hover states, bubbles) is blended from those, so all themes stay
    /// consistent. The "SSMS" theme reads its colours from the current SSMS theme instead.
    /// </summary>
    public sealed class ChatTheme
    {
        public const string FollowSsmsName = "SSMS";

        // Resource keys used with {DynamicResource ...} in the XAML.
        public const string BackgroundKey = "ChatBackground";
        public const string ForegroundKey = "ChatForeground";
        public const string MutedForegroundKey = "ChatMutedForeground";
        public const string BorderKey = "ChatBorder";
        public const string SurfaceKey = "ChatSurface";
        public const string SurfaceHoverKey = "ChatSurfaceHover";
        public const string SurfacePressedKey = "ChatSurfacePressed";
        public const string InputBackgroundKey = "ChatInputBackground";
        public const string AssistantBubbleKey = "ChatAssistantBubble";
        public const string UserBubbleKey = "ChatUserBubble";
        public const string AccentKey = "ChatAccent";
        public const string AccentHoverKey = "ChatAccentHover";
        public const string OnAccentKey = "ChatOnAccent";
        public const string DangerKey = "ChatDanger";
        public const string SponsorKey = "ChatSponsor";

        private static readonly Color ClaudeOrange = Rgb(0xD97757);

        public static IReadOnlyList<ChatTheme> All { get; } = new[]
        {
            new ChatTheme(FollowSsmsName, "Follow SSMS", null, null, ClaudeOrange),
            new ChatTheme("Light", "Light", Rgb(0xFFFFFF), Rgb(0x1F2328), Rgb(0x2563EB)),
            new ChatTheme("Dark", "Dark", Rgb(0x1E1E1E), Rgb(0xE6E6E6), Rgb(0x4F9DFF)),
            new ChatTheme("Claude", "Claude", Rgb(0xFAF9F5), Rgb(0x3D3929), ClaudeOrange),
            new ChatTheme("ClaudeDark", "Claude Dark", Rgb(0x262624), Rgb(0xF0EEE6), ClaudeOrange),
            new ChatTheme("Midnight", "Midnight", Rgb(0x0F172A), Rgb(0xE2E8F0), Rgb(0x818CF8)),
            new ChatTheme("Nord", "Nord", Rgb(0x2E3440), Rgb(0xECEFF4), Rgb(0x88C0D0)),
            new ChatTheme("Solarized", "Solarized", Rgb(0xFDF6E3), Rgb(0x586E75), Rgb(0x268BD2)),
        };

        private ChatTheme(string name, string displayName, Color? background, Color? foreground, Color accent)
        {
            Name = name;
            DisplayName = displayName;
            FixedBackground = background;
            FixedForeground = foreground;
            DefaultAccent = accent;
        }

        /// <summary>Stored in settings.json.</summary>
        public string Name { get; }

        public string DisplayName { get; }

        public Color DefaultAccent { get; }

        public bool FollowsSsms { get { return FixedBackground == null; } }

        private Color? FixedBackground { get; }

        private Color? FixedForeground { get; }

        // Swatch colours for the theme picker. For "Follow SSMS" they are read when the picker is built.
        public Brush PreviewBackground { get { return Frozen(FixedBackground ?? SsmsColor(EnvironmentColors.ToolWindowBackgroundBrushKey, Colors.White)); } }
        public Brush PreviewForeground { get { return Frozen(FixedForeground ?? SsmsColor(EnvironmentColors.ToolWindowTextBrushKey, Colors.Black)); } }
        public Brush PreviewAccent { get { return Frozen(DefaultAccent); } }

        public static ChatTheme Find(string name)
        {
            return All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        }

        /// <summary>Writes this theme's brushes into <paramref name="target"/>'s resources.</summary>
        public void Apply(FrameworkElement target, ChatAccent accent)
        {
            var background = FixedBackground ?? SsmsColor(EnvironmentColors.ToolWindowBackgroundBrushKey, Colors.White);
            var foreground = FixedForeground ?? SsmsColor(EnvironmentColors.ToolWindowTextBrushKey, Colors.Black);
            var accentColor = accent?.Color ?? DefaultAccent;
            var isDark = Luminance(background) < 0.5;

            var resources = target.Resources;
            resources[BackgroundKey] = Frozen(background);
            resources[ForegroundKey] = Frozen(foreground);
            resources[MutedForegroundKey] = Frozen(Blend(foreground, background, 0.38));
            resources[BorderKey] = Frozen(Blend(background, foreground, isDark ? 0.18 : 0.15));
            resources[SurfaceKey] = Frozen(Blend(background, foreground, 0.05));
            resources[SurfaceHoverKey] = Frozen(Blend(background, foreground, 0.11));
            resources[SurfacePressedKey] = Frozen(Blend(background, foreground, 0.17));
            resources[InputBackgroundKey] = Frozen(Blend(background, isDark ? Colors.Black : Colors.White, 0.25));
            resources[AssistantBubbleKey] = Frozen(Blend(background, foreground, isDark ? 0.06 : 0.04));
            resources[UserBubbleKey] = Frozen(Blend(background, accentColor, isDark ? 0.22 : 0.14));
            resources[AccentKey] = Frozen(accentColor);
            resources[AccentHoverKey] = Frozen(Blend(accentColor, isDark ? Colors.White : Colors.Black, 0.12));
            resources[OnAccentKey] = Frozen(Luminance(accentColor) > 0.6 ? Rgb(0x111111) : Colors.White);
            resources[DangerKey] = Frozen(isDark ? Rgb(0xF87171) : Rgb(0xDC2626));
            resources[SponsorKey] = Frozen(isDark ? Rgb(0xF472B6) : Rgb(0xDB2777));
        }

        private static Color SsmsColor(object brushKey, Color fallback)
        {
            return (Application.Current?.TryFindResource(brushKey) as SolidColorBrush)?.Color ?? fallback;
        }

        internal static Color Rgb(int rgb)
        {
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            return Color.FromRgb(
                (byte)Math.Round(from.R + (to.R - from.R) * amount),
                (byte)Math.Round(from.G + (to.G - from.G) * amount),
                (byte)Math.Round(from.B + (to.B - from.B) * amount));
        }

        private static double Luminance(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>An accent colour (Send button, selection, focus border, your message bubbles).</summary>
    public sealed class ChatAccent
    {
        // "Color" is also a property here, so the type is spelled out.
        private static System.Windows.Media.Color Rgb(int rgb) { return ChatTheme.Rgb(rgb); }

        public static IReadOnlyList<ChatAccent> All { get; } = new[]
        {
            new ChatAccent(string.Empty, "Theme default", null),
            new ChatAccent("Claude", "Claude orange", Rgb(0xD97757)),
            new ChatAccent("Blue", "Blue", Rgb(0x3B82F6)),
            new ChatAccent("Teal", "Teal", Rgb(0x14B8A6)),
            new ChatAccent("Green", "Green", Rgb(0x22A04B)),
            new ChatAccent("Purple", "Purple", Rgb(0x8B5CF6)),
            new ChatAccent("Pink", "Pink", Rgb(0xDB2777)),
            new ChatAccent("Amber", "Amber", Rgb(0xD98E04)),
        };

        private ChatAccent(string name, string displayName, System.Windows.Media.Color? color)
        {
            Name = name;
            DisplayName = displayName;
            Color = color;
            var brush = new SolidColorBrush(color ?? System.Windows.Media.Colors.Transparent);
            brush.Freeze();
            PreviewBrush = brush;
        }

        /// <summary>Stored in settings.json; empty = the theme's own accent.</summary>
        public string Name { get; }

        public string DisplayName { get; }

        public System.Windows.Media.Color? Color { get; }

        public bool IsThemeDefault { get { return Color == null; } }

        public Brush PreviewBrush { get; }

        public static ChatAccent Find(string name)
        {
            return All.FirstOrDefault(a => string.Equals(a.Name, name ?? string.Empty, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        }
    }
}
