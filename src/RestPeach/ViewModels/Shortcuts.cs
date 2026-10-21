using RestPeach.Core;

namespace RestPeach.ViewModels;

/// <summary>Single source for the keyboard shortcuts shown in the help dialog (F1).</summary>
public static class Shortcuts
{
    public static (string Gesture, string Description)[] Global =>
    [
        ("Ctrl+P", Loc.T("keys.ctrlP")),
        ("Ctrl+Shift+O", Loc.T("keys.ctrlShiftO")),
        ("Ctrl+Tab / Ctrl+Shift+Tab", Loc.T("keys.tabs")),
        ("Ctrl+1 … Ctrl+9", Loc.T("keys.tabN")),
        ("Ctrl+W / Ctrl+F4", Loc.T("keys.close")),
        ("F1", Loc.T("keys.about")),
    ];

    public static (string Gesture, string Description)[] Project =>
    [
        ("Ctrl+Enter", Loc.T("keys.send")),
        ("Ctrl+S / Ctrl+Shift+S", Loc.T("keys.save")),
        ("Ctrl+L", Loc.T("keys.url")),
        ("Ctrl+N / Ctrl+Shift+N", Loc.T("keys.new")),
        ("Ctrl+D", Loc.T("keys.duplicate")),
        ("Ctrl+Shift+C", Loc.T("keys.copyCurl")),
        ("Ctrl+F", Loc.T("keys.find")),
        ("Ctrl+Shift+E", Loc.T("keys.filterTree")),
        ("F2 / Supr", Loc.T("keys.renameDelete")),
        ("Ctrl+Shift+R", Loc.T("keys.run")),
        ("Ctrl+PageDown / PageUp", Loc.T("keys.nextDoc")),
        ("Ctrl + / Ctrl − / Ctrl+0", Loc.T("keys.zoom")),
        ("Alt+Z", Loc.T("keys.wrap")),
        ("F5", Loc.T("keys.refresh")),
    ];

    public static (string Title, (string Gesture, string Description)[] Items)[] Groups =>
        new[] { (Loc.T("keys.general"), Global), (Loc.T("keys.project"), Project) };
}
