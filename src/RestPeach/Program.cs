using Avalonia;

namespace RestPeach;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A second launch (GitPeach or TextPeach opening a project, for instance) forwards its paths and exits.
        if (!Services.SingleInstance.TryBecomePrimary(args)) return;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
            .LogToTrace();
}
