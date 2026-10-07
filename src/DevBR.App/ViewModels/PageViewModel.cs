using CommunityToolkit.Mvvm.ComponentModel;

namespace DevBR.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Segoe Fluent Icons glyph for the sidebar.</summary>
    public abstract string Glyph { get; }

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
}
