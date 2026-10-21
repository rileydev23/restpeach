using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RestPeach.Controls;

namespace RestPeach.ViewModels;

/// <summary>A tab in the window strip (Proyectos / one per project) or in a project's document strip.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _toolTip = "";
    /// <summary>Small tag before the title: the HTTP method of a request, "ENV", "RUN"…</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    private string _badge = "";
    [ObservableProperty] private IBrush _badgeBrush = Palette.Dim;
    public bool HasBadge => Badge.Length > 0;
    public virtual bool Closable => true;
    public ICommand? CloseCommand { get; set; }
}
