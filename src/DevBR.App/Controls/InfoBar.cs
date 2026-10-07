using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DevBR.App.Controls;

public enum InfoSeverity
{
    Information,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Inline status message. Severity is conveyed by an icon and a text label, never by color alone.
/// The template lives in Themes/Controls.xaml.
/// </summary>
public sealed class InfoBar : Control
{
    public static readonly DependencyProperty SeverityProperty =
        DependencyProperty.Register(nameof(Severity), typeof(InfoSeverity), typeof(InfoBar), new PropertyMetadata(InfoSeverity.Information));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata(null));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata(null));

    public static readonly DependencyProperty CloseCommandProperty =
        DependencyProperty.Register(nameof(CloseCommand), typeof(ICommand), typeof(InfoBar), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionContentProperty =
        DependencyProperty.Register(nameof(ActionContent), typeof(object), typeof(InfoBar), new PropertyMetadata(null));

    public InfoSeverity Severity
    {
        get => (InfoSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>When set, a close button is shown.</summary>
    public ICommand? CloseCommand
    {
        get => (ICommand?)GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new System.Windows.Automation.Peers.FrameworkElementAutomationPeer(this);
}
