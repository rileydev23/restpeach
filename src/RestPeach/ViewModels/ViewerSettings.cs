using CommunityToolkit.Mvvm.ComponentModel;
using RestPeach.Services;

namespace RestPeach.ViewModels;

/// <summary>Viewer preferences shared by every open project and persisted in the state file.</summary>
public sealed partial class ViewerSettings : ObservableObject
{
    public const double DefaultFontSize = 13, MinFontSize = 8, MaxFontSize = 32;

    private readonly ProjectStore _store;

    [ObservableProperty] private double _fontSize;
    [ObservableProperty] private bool _wordWrap;
    [ObservableProperty] private bool _showWhitespace;

    public ViewerSettings(ProjectStore store)
    {
        _store = store;
        _fontSize = Math.Clamp(store.State.FontSize, MinFontSize, MaxFontSize);
        _wordWrap = store.State.WordWrap;
    }

    partial void OnFontSizeChanged(double value)
    {
        _store.State.FontSize = value;
        _store.Save();
    }

    partial void OnWordWrapChanged(bool value)
    {
        _store.State.WordWrap = value;
        _store.Save();
    }

    public void Zoom(int step) => FontSize = step == 0 ? DefaultFontSize : Math.Clamp(FontSize + step, MinFontSize, MaxFontSize);
}
