using System.Windows;

namespace SsmsChatExtension.UI
{
    /// <summary>
    /// Attached properties for the chat window's button templates: <c>Icon</c> is a Segoe Fluent Icons /
    /// Segoe MDL2 Assets glyph shown before the content, <c>IsActive</c> highlights a toggle (open panel).
    /// </summary>
    public static class ButtonChrome
    {
        public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
            "Icon", typeof(string), typeof(ButtonChrome), new FrameworkPropertyMetadata(null));

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
            "IsActive", typeof(bool), typeof(ButtonChrome), new FrameworkPropertyMetadata(false));

        public static string GetIcon(DependencyObject element) { return (string)element.GetValue(IconProperty); }

        public static void SetIcon(DependencyObject element, string value) { element.SetValue(IconProperty, value); }

        public static bool GetIsActive(DependencyObject element) { return (bool)element.GetValue(IsActiveProperty); }

        public static void SetIsActive(DependencyObject element, bool value) { element.SetValue(IsActiveProperty, value); }
    }
}
