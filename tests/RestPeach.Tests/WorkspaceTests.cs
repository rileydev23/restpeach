using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "restpeach-tests", Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class WorkspaceTests
{
    [Fact]
    public void Init_creates_config_local_environment_and_ignores_secrets()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        Assert.Equal(Path.Combine(tmp.Path, ".api"), ws.Root);
        Assert.True(File.Exists(ws.ConfigPath));
        Assert.Equal(new[] { "local" }, ws.ListEnvironments());
        Assert.Contains("environments/.env.secrets.json", File.ReadAllText(Path.Combine(ws.Root, ".gitignore")));
        Assert.False(ws.EnsureGitignore()); // already there
        Assert.Equal("local", ws.LoadConfig().DefaultEnvironment);
        Assert.NotNull(Workspace.Find(tmp.Path));
        Assert.NotNull(Workspace.Find(ws.Root)); // opening the .api folder itself works too
    }

    [Fact]
    public void Secrets_are_kept_apart_from_the_public_file()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var env = ws.LoadEnvironment("local");
        env.Secrets["TOKEN"] = "s3cr3t";
        ws.SaveEnvironment(env);

        Assert.DoesNotContain("s3cr3t", File.ReadAllText(Path.Combine(ws.EnvironmentsPath, "local.env.json")));
        Assert.Contains("s3cr3t", File.ReadAllText(ws.SecretsPath));
        Assert.Equal("s3cr3t", ws.LoadEnvironment("local").Secrets["TOKEN"]);
        Assert.DoesNotContain(".env.secrets", ws.ListEnvironments());
    }

    [Fact]
    public void Tree_lists_folders_first_and_hides_settings()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var users = ws.CreateFolder("", "Users");
        ws.CreateRequest("", new ApiRequest { Name = "Health" });
        ws.CreateRequest(users, new ApiRequest { Name = "Get user" });
        ws.CreateRequest(users, new ApiRequest { Name = "Get user" }); // name taken → "(2)"
        ws.SaveFolder(users, new FolderConfig { Auth = AuthSpec.BearerOf("x") });

        var root = ws.ListChildren("");
        Assert.Equal(new[] { "Users", "Health" }, root.Select(i => i.Name));
        Assert.Equal(new[] { "Get user", "Get user (2)" }, ws.ListChildren(users).Select(i => i.Name));
        Assert.Equal(3, ws.CountRequests());
    }

    [Fact]
    public void Rename_move_and_duplicate()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var rel = ws.CreateRequest("", new ApiRequest { Name = "Old" });
        rel = ws.RenameRequest(rel, "New: name");
        Assert.Equal("New- name.req.json", rel);
        Assert.Equal("New: name", ws.LoadRequest(rel).Name);

        var folder = ws.CreateFolder("", "A");
        var moved = ws.Move(rel, folder, isFolder: false);
        Assert.Equal("A/New- name.req.json", moved);
        var copy = ws.DuplicateRequest(moved);
        Assert.Equal(2, ws.ListRequests(folder).Count);
        Assert.Throws<IOException>(() => ws.Move("A", "A/B", isFolder: true)); // a folder into itself
        Assert.NotEqual(moved, copy);
    }

    [Fact]
    public void Auth_and_variables_inherit_through_folders()
    {
        using var tmp = new TempDir();
        var ws = Workspace.Init(tmp.Path, "demo");
        var config = ws.LoadConfig();
        config.Auth = AuthSpec.BearerOf("{{TOKEN}}");
        config.Variables["HOST"] = "collection";
        ws.SaveConfig(config);
        var a = ws.CreateFolder("", "A");
        var b = ws.CreateFolder(a, "B");
        ws.SaveFolder(a, new FolderConfig { Variables = { ["HOST"] = "folder-a" } });
        ws.SaveFolder(b, new FolderConfig { Auth = new AuthSpec { Type = AuthTypes.ApiKey, Key = "X-Key", Value = "k" } });

        var env = ws.LoadEnvironment("local");
        env.Secrets["TOKEN"] = "secret";
        var chain = ws.FolderChain(b);
        Assert.Equal(AuthTypes.ApiKey, AuthResolver.Resolve(new AuthSpec(), chain, ws.LoadConfig()).Type);
        Assert.Equal(AuthTypes.Bearer, AuthResolver.Resolve(new AuthSpec(), ws.FolderChain(a), ws.LoadConfig()).Type);
        Assert.Equal(AuthTypes.None, AuthResolver.Resolve(AuthSpec.None(), chain, ws.LoadConfig()).Type);

        var req = new ApiRequest { Url = "{{BASE_URL}}/{{HOST}}", Auth = new AuthSpec() };
        var p = RestPeach.Core.Runner.Prepare(ws, ws.LoadConfig(), a, req, env, null);
        Assert.Equal("http://localhost:8080/folder-a", p.Url);
        Assert.Equal("Bearer secret", p.Header("Authorization"));

        var masked = RestPeach.Core.Runner.Prepare(ws, ws.LoadConfig(), a, req, env, null, _ => "••••");
        Assert.Equal("Bearer ••••", masked.Header("Authorization"));
    }

    [Fact]
    public void Interpolation_reports_missing_and_handles_nesting_and_dynamics()
    {
        var scope = new VariableScope();
        scope.Set("A", "{{B}}-x", VariableSource.Environment);
        scope.Set("B", "b", VariableSource.Secret);
        var missing = new SortedSet<string>();
        Assert.Equal("b-x/{{ C }}", Interpolator.Expand("{{A}}/{{ C }}", scope, missing));
        Assert.Equal(new[] { "C" }, missing);
        Assert.Matches("^[0-9a-f-]{36}$", Interpolator.Expand("{{$guid}}", scope));
        Assert.Equal(new[] { "A", "C" }, Interpolator.Names("{{A}} {{$guid}} {{C}} {{A}}"));
    }

    [Fact]
    public void Query_string_and_params_table_agree()
    {
        var url = "{{BASE}}/s?q=peach&page=2#top";
        var rows = QueryString.Parse(url);
        Assert.Equal(new[] { "q", "page" }, rows.Select(r => r.Key));
        rows[1].Active = false;
        rows.Add(KeyValue.Of("sort", "asc"));
        Assert.Equal("{{BASE}}/s?q=peach&sort=asc#top", QueryString.WithParams(url, rows));
        Assert.Equal("http://localhost:3000/x", RequestPreparer.NormalizeUrl("localhost:3000/x"));
    }
}
