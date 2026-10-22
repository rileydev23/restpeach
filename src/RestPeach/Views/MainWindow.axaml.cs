using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.ViewModels;

namespace RestPeach.Views;

public partial class MainWindow : Window
{
    private bool _confirmedClose;

    public MainWindow()
    {
        InitializeComponent();
        // X11 can't draw into the window frame: keep the system title bar there instead of showing two.
        if (OperatingSystem.IsLinux())
        {
            ExtendClientAreaToDecorationsHint = false;
            TitleBar.IsVisible = false;
        }
        TitleBar.PointerPressed += OnTitleBarPressed;
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => ToggleMaximized();
        CloseButton.Click += (_, _) => Close();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        });
    }

    // ------------------------------------------------------------ title bar

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>Empty title bar: drag moves the window, double click maximizes or restores.</summary>
    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximized();
        else BeginMoveDrag(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || MaxGlyph is null) return;
        bool max = WindowState == WindowState.Maximized;
        MaxGlyph.Data = Geometry.Parse(max
            ? "M2.5,2.5 V0.5 H9.5 V7.5 H7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z"
            : "M0.5,0.5 H9.5 V9.5 H0.5 Z");
        ToolTip.SetTip(MaxButton, Loc.T(max ? "win.restore" : "win.maximize"));
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_confirmedClose && DataContext is MainWindowViewModel { HasUnsavedChanges: true })
        {
            e.Cancel = true;
            if (await Dialogs.Confirm(Loc.T("common.unsaved"), Loc.T("main.quit.text"), Loc.T("main.quit.ok")))
            {
                _confirmedClose = true;
                Close();
            }
            return;
        }
        base.OnClosing(e);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var files = e.Data.GetFiles();
        if (files is null) return;
        foreach (var item in files)
        {
            var path = item.TryGetLocalPath();
            if (path is null) continue;
            if (Directory.Exists(path)) await vm.Home.AddOrImportAsync(path);
            else if (!File.Exists(path)) continue;
            // A request file opens where it lives; anything else dropped on a collection is an import.
            else if (Core.Storage.Workspace.IsRequestFile(path)) vm.OpenFromArgs(new[] { path });
            else if (vm.SelectedTab is ProjectViewModel { HasCollection: true } p) await p.ImportFileAsync(path);
        }
    }
}
