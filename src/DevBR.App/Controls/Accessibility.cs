using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DevBR.App.Controls;

public static class Accessibility
{
    /// <summary>
    /// Names an <see cref="Expander"/>'s header toggle for screen readers when the header is rich content
    /// (WPF only derives a name from a plain string header).
    /// </summary>
    public static readonly DependencyProperty HeaderNameProperty = DependencyProperty.RegisterAttached(
        "HeaderName", typeof(string), typeof(Accessibility), new PropertyMetadata(null, OnHeaderNameChanged));

    public static string? GetHeaderName(DependencyObject element) => (string?)element.GetValue(HeaderNameProperty);

    public static void SetHeaderName(DependencyObject element, string? value) => element.SetValue(HeaderNameProperty, value);

    private static void OnHeaderNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Expander expander)
        {
            return;
        }

        AutomationProperties.SetName(expander, (string?)e.NewValue ?? string.Empty);
        expander.Loaded -= OnLoaded;
        expander.Loaded += OnLoaded;
        if (expander.IsLoaded)
        {
            Apply(expander);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e) => Apply((Expander)sender);

    private static void Apply(Expander expander)
    {
        expander.ApplyTemplate();
        if (expander.Template?.FindName("HeaderSite", expander) is DependencyObject header)
        {
            AutomationProperties.SetName(header, GetHeaderName(expander) ?? string.Empty);
        }
    }
}
