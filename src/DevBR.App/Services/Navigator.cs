using DevBR.App.ViewModels;

namespace DevBR.App.Services;

/// <summary>Decouples page view models from the shell so they can request navigation without a reference cycle.</summary>
public sealed class Navigator
{
    public event EventHandler<Type>? NavigationRequested;

    public void NavigateTo<TPage>() where TPage : PageViewModel
        => NavigationRequested?.Invoke(this, typeof(TPage));
}
