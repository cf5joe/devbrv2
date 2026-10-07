using System.Windows;
using DevBR.App.ViewModels;

namespace DevBR.App.Views;

public partial class BackupView
{
    public BackupView()
    {
        InitializeComponent();
    }

    // PasswordBox values cannot be data-bound; they are handed to the view model as they change.
    private void OnPasswordChanged(object sender, RoutedEventArgs e)
        => (DataContext as BackupViewModel)?.SetPassword(Password.Password, Confirm.Password);
}
