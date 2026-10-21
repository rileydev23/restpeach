using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RestPeach.ViewModels;

namespace RestPeach.Views;

/// <summary>
/// The tab of a project. Avalonia recycles this view between project tabs, so everything that depends on the
/// view model is (re)attached on DataContext change.
/// </summary>
public partial class ProjectView : UserControl
{
    private const string NodeFormat = "restpeach/tree-node";
    private ProjectViewModel? _vm;
    private Point _pressPoint;
    private TreeNode? _pressNode;

    public ProjectView()
    {
        InitializeComponent();

        Tree.DoubleTapped += (_, e) =>
        {
            if (NodeAt(e.Source) is { IsFolder: true } n) n.IsExpanded = !n.IsExpanded;
        };
        Tree.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            if (_vm is null) return;
            var node = NodeAt(e.Source);
            if (node is not null) _vm.SelectedNode = node;
            MenuPresenter.Show(Tree, _vm.MenuFor(node));
        };
        Tree.KeyDown += async (_, e) =>
        {
            if (_vm is null) return;
            switch (e.Key)
            {
                case Key.F2: e.Handled = true; await _vm.RenameSelectedAsync(); break;
                case Key.Delete: e.Handled = true; await _vm.DeleteSelectedAsync(); break;
                case Key.Enter when _vm.SelectedNode is { IsRequest: true } n: e.Handled = true; _vm.OpenRequest(n.RelPath)?.FocusUrl(); break;
            }
        };
        RootMenuButton.Click += (_, _) => { if (_vm is not null) MenuPresenter.Show(RootMenuButton, _vm.MenuFor(null)); };

        FilterBox.KeyDown += (_, e) =>
        {
            if (_vm is null) return;
            switch (e.Key)
            {
                case Key.Down when _vm.IsFiltering: HitList.Focus(); e.Handled = true; break;
                case Key.Enter: _vm.OpenSelectedHit(); e.Handled = true; break;
                case Key.Escape: _vm.Filter = ""; e.Handled = true; break;
            }
        };
        HitList.Tapped += (_, _) => _vm?.OpenSelectedHit();
        HitList.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _vm?.OpenSelectedHit(); e.Handled = true; } };

        // Drag a request or folder onto another folder (or the empty area for the collection root) to move it on disk.
        Tree.AddHandler(PointerPressedEvent, (_, e) =>
        {
            _pressNode = e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed ? NodeAt(e.Source) : null;
            _pressPoint = e.GetPosition(Tree);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, async (_, e) =>
        {
            if (_pressNode is not { } node || !e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(Tree);
            if (Math.Abs(p.X - _pressPoint.X) < 6 && Math.Abs(p.Y - _pressPoint.Y) < 6) return;
            _pressNode = null;
            var data = new DataObject();
            data.Set(NodeFormat, node.RelPath);
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Tree.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.Data.Contains(NodeFormat)) return;
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
        });
        Tree.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (_vm is null || e.Data.Get(NodeFormat) is not string rel) return;
            e.Handled = true;
            var node = _vm.All().FirstOrDefault(n => n.RelPath == rel);
            if (node is not null) await _vm.MoveByDropAsync(node, NodeAt(e.Source));
        });

        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => FocusDefault();
    }

    private static TreeNode? NodeAt(object? source) =>
        (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as TreeNode;

    private void Attach()
    {
        if (_vm is not null)
        {
            _vm.FilterFocusRequested -= OnFilterFocus;
            _vm.PropertyChanged -= OnVmChanged;
        }
        _vm = DataContext as ProjectViewModel;
        if (_vm is not null)
        {
            _vm.FilterFocusRequested += OnFilterFocus;
            _vm.PropertyChanged += OnVmChanged;
        }
        FocusDefault();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Expanding folders is part of the session.
        if (e.PropertyName == nameof(ProjectViewModel.SelectedDoc) && _vm?.SelectedDoc is null) FocusDefault();
    }

    /// <summary>Project shortcuts live on this view, so something inside it must own the focus.</summary>
    private void FocusDefault() => Dispatcher.UIThread.Post(() =>
    {
        if (_vm is null || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Control { } c && c.FindAncestorOfType<ProjectView>() == this) return;
        if (_vm.HasCollection) Tree.Focus();
    }, DispatcherPriority.Background);

    private void OnFilterFocus() => Dispatcher.UIThread.Post(() => { FilterBox.Focus(); FilterBox.SelectAll(); });
}
