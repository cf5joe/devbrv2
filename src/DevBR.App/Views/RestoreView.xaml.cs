using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DevBR.App.ViewModels;

namespace DevBR.App.Views;

public partial class RestoreView
{
    public RestoreView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RestoreViewModel old)
            {
                old.PropertyChanged -= OnViewModelPropertyChanged;
            }

            if (e.NewValue is RestoreViewModel current)
            {
                current.PropertyChanged += OnViewModelPropertyChanged;
            }
        };
    }

    private RestoreViewModel? ViewModel => DataContext as RestoreViewModel;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RestoreViewModel.IsNeedsPassword) && ViewModel?.IsNeedsPassword == true)
        {
            Dispatcher.InvokeAsync(() => Password.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    // PasswordBox cannot be data-bound; the value is handed to the view model once and cleared.
    private void OnUnlockClick(object sender, RoutedEventArgs e) => Submit();

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Submit();
            e.Handled = true;
        }
    }

    private void Submit()
    {
        var password = Password.Password;
        Password.Clear();
        ViewModel?.SubmitPasswordCommand.Execute(password);
    }
}
