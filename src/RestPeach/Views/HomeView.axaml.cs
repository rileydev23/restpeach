using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.ViewModels;

namespace RestPeach.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        ProjectList.DoubleTapped += (_, e) =>
        {
            if (Vm is { } vm && RowAt(e.Source) is ProjectRow r) vm.OpenCommand.Execute(r.Entry);
        };
        ProjectList.KeyDown += (_, e) =>
        {
            if (Vm is not { } vm) return;
            if (e.Key == Key.Enter)
            {
                if (ProjectList.SelectedItem is GroupHeaderRow h) h.ToggleCommand.Execute(null);
                else vm.OpenCommand.Execute(null);
            }
            else if (e.Key == Key.Delete && SelectedProjects() is { Count: > 0 } projects) vm.RemoveMany(projects);
        };
        ProjectList.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (Vm is not { } vm) return;
            switch (RowAt(e.Source))
            {
                case GroupHeaderRow h:
                    MenuPresenter.Show(ProjectList, vm.MenuForGroup(h));
                    break;
                case ProjectRow r:
                    // Right-click outside the current selection acts on that row only.
                    if (!ProjectList.Selection.SelectedItems.Contains(r)) ProjectList.SelectedItem = r;
                    MenuPresenter.Show(ProjectList, vm.MenuForProjects(SelectedProjects()));
                    break;
            }
        };
        AttachedToVisualTree += (_, _) => FilterBox.Focus();
    }

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private static HomeRow? RowAt(object? source)
        => (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as HomeRow;

    private List<ProjectEntry> SelectedProjects()
        => ProjectList.Selection.SelectedItems.OfType<ProjectRow>().Select(r => r.Entry).ToList();
}
