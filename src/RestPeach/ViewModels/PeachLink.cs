using System.Diagnostics;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>
/// "Abrir en GitPeach / TextPeach": launches the sibling app, or says what it is and offers its repository when it
/// is not on this machine (same wording and buttons as the other apps of the family).
/// </summary>
public static class PeachLink
{
    public static async Task OpenAsync(string app, string workingDir, params string[] paths)
    {
        if (PeachLocator.Open(app, workingDir, paths)) return;
        var key = app == PeachLocator.GitPeach ? "gitpeach" : "textpeach";
        if (await Dialogs.Confirm(Loc.T(key + ".missing.title"), Loc.T(key + ".missing.text"), Loc.T("peach.missing.go"), Loc.T("peach.missing.later")))
        {
            try { Process.Start(new ProcessStartInfo(PeachLocator.RepoUrl(app)) { UseShellExecute = true }); }
            catch { /* no browser: nothing else to do */ }
        }
    }
}
