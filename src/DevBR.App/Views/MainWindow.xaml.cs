using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevBR.App.Theming;
using DevBR.App.ViewModels;

namespace DevBR.App.Views;

public partial class MainWindow
{
    private readonly ThemeService _theme;

    public MainWindow(MainViewModel viewModel, ThemeService theme)
    {
        _theme = theme;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += (_, _) => FocusSelectedPage();
    }

    /// <summary>Keyboard focus starts on the current sidebar item, so arrow keys move between pages at once.</summary>
    private void FocusSelectedPage()
    {
        Navigation.UpdateLayout();
        if (Navigation.ItemContainerGenerator.ContainerFromItem(Navigation.SelectedItem) is System.Windows.Controls.ListBoxItem item)
        {
            item.Focus();
        }
        else
        {
            Navigation.Focus();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedPage))
        {
            return;
        }

        PageScroller.ScrollToTop();

        // A short, subtle entrance; skipped entirely when Windows animations are off or high contrast is on.
        if (!_theme.AnimationsEnabled)
        {
            return;
        }

        var duration = TimeSpan.FromMilliseconds(160);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = easing });
        ((TranslateTransform)PageHost.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = easing });
    }
}
