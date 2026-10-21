using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.ViewModels;
using RestPeach.Views;

namespace RestPeach;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        TabStrip.Install();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var store = new ProjectStore();
            // Before any view or view model exists: they read their texts once, when built.
            Loc.Init(store.State.Language);
#if !DEBUG
            // Debug runs stay anonymous so they never replace the location of the real build.
            PeachApps.Announce("RestPeach");
#endif

            var vm = new MainWindowViewModel(store);
            if (desktop.Args is { Length: > 0 } args) vm.OpenFromArgs(args);
            // Invisible until placed, so the user never sees it jump from a default spot to the saved one.
            var window = new MainWindow { DataContext = vm, Opacity = 0, WindowStartupLocation = WindowStartupLocation.Manual };
            PlaceWindow(window, store.State, shown: false);
            SingleInstance.Received += args => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                vm.OpenFromArgs(args);
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            });
            window.Opened += (_, _) =>
            {
                PlaceWindow(window, store.State, shown: true);
                window.Opacity = 1;
                RunDebugActions(vm);
            };
            window.Closing += (_, _) =>
            {
                var st = store.State;
                st.Maximized = window.WindowState == WindowState.Maximized;
                if (window.WindowState == WindowState.Normal)
                {
                    st.Width = window.ClientSize.Width;
                    st.Height = window.ClientSize.Height;
                    st.X = window.Position.X;
                    st.Y = window.Position.Y;
                }
                vm.Shutdown();
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Debug builds only: RESTPEACH_DEBUG_ACTIONS="open:Users/Get user.req.json;send;run;about" drives the UI for
    /// screenshots without synthesizing keystrokes.
    /// </summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void RunDebugActions(MainWindowViewModel vm)
    {
        var spec = Environment.GetEnvironmentVariable("RESTPEACH_DEBUG_ACTIONS");
        if (string.IsNullOrEmpty(spec)) return;
        Avalonia.Threading.DispatcherTimer.RunOnce(async () =>
        {
            foreach (var action in spec.Split(';'))
            {
                var p = vm.SelectedTab as ProjectViewModel;
                if (action == "about") await vm.ShowAboutCommand.ExecuteAsync(null);
                else if (action == "home") vm.ShowHomeCommand.Execute(null);
                else if (action.StartsWith("open:") && p is not null) { p.Reveal(action[5..]); p.OpenRequest(action[5..]); }
                else if (action == "send" && p?.SelectedRequest is { } r) await r.SendCommand.ExecuteAsync(null);
                else if (action.StartsWith("section:") && p?.SelectedRequest is { } r2) r2.Section = action[8..];
                else if (action.StartsWith("response:") && p?.SelectedRequest is { } r3) r3.ResponseSection = action[9..];
                else if (action == "run" && p is not null) await p.RunCollectionCommand.ExecuteAsync(null);
                else if (action.StartsWith("pick:") && p?.SelectedDoc is RunTab run) run.Selected = run.Rows.ElementAtOrDefault(int.Parse(action[5..]));
                else if (action.StartsWith("detail:") && p?.SelectedDoc is RunTab run2) run2.Detail = action[7..];
                else if (action == "env" && p is not null) p.EditEnvironmentCommand.Execute(null);
                else if (action == "settings" && p is not null) p.CollectionSettingsCommand.Execute(null);
                else if (action.StartsWith("filter:") && p is not null) p.Filter = action[7..];
                else if (action.StartsWith("wait:")) await Task.Delay(int.Parse(action[5..]));
            }
        }, TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Teams-style placement: default ~1200x760 DIP (never more than 92% of the work area), remembers
    /// size/position/maximized, picks the monitor the window was last on, and always keeps the whole
    /// frame (title bar included) inside the work area.
    /// </summary>
    private static void PlaceWindow(Window w, AppState st, bool shown)
    {
        var screens = w.Screens;
        var primary = screens.Primary;
        if (primary is null) return;
        // Maximized windows were fully set up before showing; moving them again would shift them.
        if (shown && st.Maximized) return;

        // Monitor: the one containing the saved position, otherwise the primary one.
        var screen = primary;
        if (st.X is int sx && st.Y is int sy)
            screen = screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(sx + 60, sy + 20))) ?? primary;

        double scale = screen.Scaling;
        var wa = screen.WorkingArea;

        // Window.Height excludes the title bar/borders: measure them once the window exists.
        double chrome = shown && w.FrameSize is { } f ? Math.Max(0, f.Height - w.Height) : 40;
        double frameW = shown && w.FrameSize is { } f2 ? Math.Max(0, f2.Width - w.Width) : 0;

        double maxW = wa.Width / scale - frameW;
        double maxH = wa.Height / scale - chrome;

        bool hasSize = st.Width >= 800 && st.Height >= 500;
        double width = hasSize ? st.Width : 1200;
        double height = hasSize ? st.Height : 760;
        width = Math.Max(Math.Min(width, maxW * (hasSize ? 0.96 : 0.92)), Math.Min(800, maxW));
        height = Math.Max(Math.Min(height, maxH * 0.96), Math.Min(500, maxH));
        w.Width = width;
        w.Height = height;

        // Position: saved one if it was on a screen, else centered; then pushed fully inside.
        double totalW = (width + frameW) * scale, totalH = (height + chrome) * scale;
        int x, y;
        if (st.X is int px && st.Y is int py && ReferenceEquals(screen, screens.All.FirstOrDefault(s => s.WorkingArea.Contains(new PixelPoint(px + 60, py + 20)))))
        {
            x = px; y = py;
        }
        else
        {
            x = wa.X + (int)((wa.Width - totalW) / 2);
            y = wa.Y + (int)((wa.Height - totalH) / 2);
        }
        x = Math.Max(wa.X, Math.Min(x, wa.X + wa.Width - (int)totalW));
        y = Math.Max(wa.Y, Math.Min(y, wa.Y + wa.Height - (int)totalH));
        w.Position = new PixelPoint(x, y);

        if (st.Maximized) w.WindowState = WindowState.Maximized;
    }
}
