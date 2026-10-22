using System.Text.RegularExpressions;
using RestPeach.Core;
using RestPeach.Services;
using RestPeach.ViewModels;

namespace RestPeach.Tests;

public class AppTests
{
    [Fact]
    public void Every_text_has_both_languages_with_the_same_placeholders()
    {
        var placeholder = new Regex(@"\{(\d+)(?::[^}]*)?\}");
        foreach (var (key, (en, es)) in Strings.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key + " (en)");
            Assert.False(string.IsNullOrWhiteSpace(es), key + " (es)");
            var a = placeholder.Matches(en).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            var b = placeholder.Matches(es).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x);
            Assert.True(a.SequenceEqual(b), $"{key}: placeholders differ");
        }
    }

    [Fact]
    public void Plurals_come_in_pairs()
    {
        foreach (var key in Strings.All.Keys.Where(k => k.EndsWith(".one")))
            Assert.True(Strings.All.ContainsKey(key[..^4] + ".many"), key);
    }

    [Fact]
    public void Language_follows_the_saved_choice_then_windows()
    {
        Assert.Equal("es", Loc.Resolve("es", "en"));
        Assert.Equal("en", Loc.Resolve(null, "de"));
        Assert.Equal("es", Loc.Resolve("auto", "es"));
    }

    [Fact]
    public void Sibling_apps_are_looked_for_in_the_usual_places()
    {
        // Native paths and the platform's executable name, so the test also holds on Linux.
        var root = Path.Combine(Path.GetTempPath(), "peach");
        string Exe(string app) => OperatingSystem.IsWindows() ? app + ".exe" : app;
        string P(params string[] parts) => Path.Combine([root, .. parts]);
        var c = PeachLocator.Candidates("GitPeach", P("Tools", "RestPeach"), P("Local"), P("Program Files"),
            P("bin") + Path.PathSeparator + "\"" + P("x y") + "\"").ToList();
        Assert.Equal(new[]
        {
            P("Tools", "RestPeach", Exe("GitPeach")),
            P("Tools", "GitPeach", Exe("GitPeach")),
            P("Local", "Programs", "GitPeach", Exe("GitPeach")),
            P("Program Files", "GitPeach", Exe("GitPeach")),
            P("bin", Exe("GitPeach")),
            P("x y", Exe("GitPeach")),
        }, c);
        Assert.Equal(P("Tools", "TextPeach", Exe("TextPeach")),
            PeachLocator.Find("TextPeach", p => p.StartsWith(P("Tools", "TextPeach")), P("Tools", "RestPeach"), null, null, null));
        Assert.Equal("https://github.com/rileydev23/textpeach", PeachLocator.RepoUrl("TextPeach"));
    }

    [Fact]
    public void Project_groups_are_shared_with_the_family()
    {
        var s = new AppState();
        s.Projects.Add(new ProjectEntry { Name = "a", Path = @"C:\r\a", Group = "API" });
        s.Projects.Add(new ProjectEntry { Name = "b", Path = @"C:\r\b" });
        ProjectGrouping.Ensure(s, "API");
        var sections = ProjectGrouping.Build(s, _ => true, hideEmpty: false);
        Assert.Equal(new[] { "API", null }, sections.Select(x => x.Group?.Name));
        // Taken (ignoring case): numbered, keeping the casing as typed.
        Assert.Equal("api (2)", ProjectGrouping.UniqueName(s, "api"));
    }

    [Fact]
    public void Method_tags_stay_short()
    {
        Assert.Equal("DEL", RestPeach.Controls.Look.MethodTag("delete"));
        Assert.Equal("OPT", RestPeach.Controls.Look.MethodTag("OPTIONS"));
        Assert.Equal("PATCH", RestPeach.Controls.Look.MethodTag("PATCH"));
    }

    [Fact]
    public void Run_detail_shows_what_went_out()
    {
        var form = new RestPeach.Core.Http.PreparedRequest { Method = "POST", Url = "https://x.test/up", BodyType = RestPeach.Core.Model.BodyTypes.FormData };
        form.Headers.Add(new("Authorization", "Bearer t"));
        form.Fields.Add(new RestPeach.Core.Model.KeyValue { Key = "name", Value = "Ana" });
        form.Fields.Add(new RestPeach.Core.Model.KeyValue { Key = "doc", Value = "a.pdf", Type = "file" });
        var row = new RunRow { Result = new RunResult("users/up", null, form, null, Array.Empty<RestPeach.Core.Testing.AssertionResult>(), null) };
        Assert.Equal("POST https://x.test/up", row.SentLine);
        Assert.Equal("name: Ana\ndoc: @a.pdf", row.SentBody);
        // The engine adds User-Agent and multipart's Content-Type: the detail lists them too.
        Assert.Equal(new[] { "Authorization", "Content-Type", "User-Agent" }, row.SentHeaders.Select(h => h.Key));

        var urlencoded = new RestPeach.Core.Http.PreparedRequest { Method = "POST", Url = "https://x.test", BodyType = RestPeach.Core.Model.BodyTypes.UrlEncoded };
        urlencoded.Fields.Add(new RestPeach.Core.Model.KeyValue { Key = "q", Value = "a b" });
        Assert.Equal("q=a%20b", new RunRow { Result = new RunResult("q", null, urlencoded, null, Array.Empty<RestPeach.Core.Testing.AssertionResult>(), null) }.SentBody);

        // Secrets of the environment never show in the detail, not even inside Basic auth.
        var secret = new RestPeach.Core.Http.PreparedRequest { Method = "GET", Url = "https://x.test/?key=s3cr%3Dt" };
        secret.Headers.Add(new("Authorization", "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("ana:s3cr=t"))));
        secret.Headers.Add(new("X-Token", "s3cr=t"));
        var masked = new RunRow { Result = new RunResult("s", null, secret, null, Array.Empty<RestPeach.Core.Testing.AssertionResult>(), null), Secrets = new[] { "s3cr=t" } };
        Assert.Equal("GET https://x.test/?key=••••", masked.SentLine);
        Assert.Equal(new[] { "Basic ••••", "••••" }, masked.SentHeaders.Take(2).Select(h => h.Value));
        Assert.Contains("s3cr=t", masked.Curl); // copying keeps the real values

        var notSent = new RunRow { Result = new RunResult("broken", null, null, null, Array.Empty<RestPeach.Core.Testing.AssertionResult>(), "bad file") };
        Assert.False(notSent.WasSent);
        Assert.Equal("bad file", notSent.Failure);
        Assert.Empty(notSent.SentHeaders);
    }

    [Fact]
    public void Menu_shortcut_hints_never_break_the_menu()
    {
        // "Del" is not an Avalonia key: it used to throw while building the tree's context menu and close the app.
        Assert.Null(RestPeach.Views.MenuPresenter.ParseGesture("Del"));
        foreach (var g in new[] { "Delete", "F2", "Ctrl+N", "Ctrl+Shift+N", "Ctrl+Enter", "Ctrl+Shift+C", "Ctrl+D" })
            Assert.NotNull(RestPeach.Views.MenuPresenter.ParseGesture(g));
    }

    [Fact]
    public void Shortcuts_have_texts()
    {
        foreach (var (_, items) in Shortcuts.Groups)
            foreach (var (gesture, description) in items)
                Assert.DoesNotContain(".", description.Length > 0 && !description.Contains(' ') ? description : "ok", StringComparison.Ordinal);
    }
}
