using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>
/// Settings of the collection (<c>config.json</c>) or of one folder (<c>folder.json</c>): auth inherited by what is
/// below and variables of that level. The collection adds timeouts, redirects, TLS and the default environment.
/// </summary>
public sealed partial class SettingsTab : TabViewModel
{
    private readonly ICollectionHost _host;
    private string _saved = "";

    public string RelDir { get; private set; }
    public bool IsCollection => RelDir.Length == 0;
    public bool IsFolder => !IsCollection;
    public AuthEditor Auth { get; }
    public KeyValueTable Variables { get; } = new() { ShowActive = false, KeyWatermark = Loc.T("kv.variable") };
    public IReadOnlyList<Choice> Environments { get; private set; } = Array.Empty<Choice>();
    public IReadOnlyList<Choice> HttpVersions { get; } = new[]
    {
        new Choice("", Loc.T("set.http.default")), new Choice("2", Loc.T("set.http.2")), new Choice("3", Loc.T("set.http.3")),
    };
    public IReadOnlyList<Choice> ProxyModes { get; } = new[]
    {
        new Choice("", Loc.T("set.proxy.system")), new Choice("none", Loc.T("set.proxy.none")), new Choice("manual", Loc.T("set.proxy.manual")),
    };

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _timeout = "30000";
    [ObservableProperty] private bool _followRedirects = true;
    [ObservableProperty] private bool _verifyTls = true;
    [ObservableProperty] private Choice? _defaultEnvironment;
    [ObservableProperty] private Choice _httpVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualProxy))]
    private Choice _proxyMode;
    [ObservableProperty] private string _proxyUrl = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPemCert))]
    private string _certPath = "";
    [ObservableProperty] private string _certKey = "";
    [ObservableProperty] private string _certPassword = "";
    [ObservableProperty] private string _certHost = "";

    public bool IsManualProxy => ProxyMode.Value == "manual";
    /// <summary>A PEM certificate may have its key in a separate file; a .pfx carries it inside.</summary>
    public bool IsPemCert => CertPath.Trim().Length > 0 && Path.GetExtension(CertPath.Trim()).ToLowerInvariant() is not (".pfx" or ".p12");
    [ObservableProperty] private bool _isDirty;

    public string FileText => IsCollection ? ".api/config.json" : $".api/{RelDir}/folder.json";
    public string Heading => IsCollection ? Loc.T("set.collection") : Loc.F("set.folder", RelDir);
    public string AuthHint => IsCollection ? Loc.T("set.authHint.collection") : Loc.T("set.authHint.folder");

    public event Action<SettingsTab>? Saved;

    public SettingsTab(ICollectionHost host, string relDir)
    {
        _host = host;
        RelDir = relDir;
        Auth = new AuthEditor(canInherit: relDir.Length > 0);
        _httpVersion = HttpVersions[0];
        _proxyMode = ProxyModes[0];
        Badge = IsCollection ? "API" : "DIR";
        BadgeBrush = IsCollection ? Palette.Green : Palette.Amber;
        Auth.Changed += Edited;
        VariableScope Scope() => Runner.Scope(_host.Ws, _host.Config, RelDir, _host.CurrentEnvironment, _host.Runtime);
        Auth.ConnectOAuth(host.Engine, a => RequestPreparer.ExpandDeferred(a, Scope()),
            () => HttpOptions.For(_host.Config, null, Scope(), _host.Ws.ProjectRoot), host.Notify);
        Variables.Changed += Edited;
        Load();
    }

    public void Load()
    {
        if (IsCollection)
        {
            var c = _host.Ws.LoadConfig();
            Name = c.Name;
            Timeout = c.TimeoutMs.ToString(CultureInfo.InvariantCulture);
            FollowRedirects = c.FollowRedirects;
            VerifyTls = c.VerifyTls;
            HttpVersion = HttpVersions.FirstOrDefault(v => v.Value == c.HttpVersion) ?? HttpVersions[0];
            ProxyMode = c.Proxy.Length == 0 ? ProxyModes[0] : c.Proxy.Equals("none", StringComparison.OrdinalIgnoreCase) ? ProxyModes[1] : ProxyModes[2];
            ProxyUrl = IsManualProxy ? c.Proxy : "";
            CertPath = c.ClientCert.Path;
            CertKey = c.ClientCert.Key;
            CertPassword = c.ClientCert.Password;
            CertHost = c.ClientCert.Host;
            Auth.Load(c.Auth);
            Variables.Load(c.Variables);
            Environments = new[] { new Choice("", Loc.T("env.none")) }
                .Concat(_host.Ws.ListEnvironments().Select(s => new Choice(s, _host.Ws.LoadEnvironment(s).DisplayName))).ToList();
            OnPropertyChanged(nameof(Environments));
            DefaultEnvironment = Environments.FirstOrDefault(e => string.Equals(e.Value, c.DefaultEnvironment, StringComparison.OrdinalIgnoreCase)) ?? Environments[0];
        }
        else
        {
            var f = _host.Ws.LoadFolder(RelDir);
            Auth.Load(f.Auth);
            Variables.Load(f.Variables);
        }
        _saved = Snapshot();
        IsDirty = false;
        UpdateTitle();
    }

    private string Snapshot() => string.Join("\0", Name, Timeout, FollowRedirects, VerifyTls, DefaultEnvironment?.Value,
        HttpVersion.Value, ProxyMode.Value, ProxyUrl, CertPath, CertKey, CertPassword, CertHost,
        Core.Text.CanonicalJson.Write(Core.Storage.Codec.WriteAuth(Auth.ToModel())),
        string.Join("\n", Variables.ToMap().Select(kv => kv.Key + "=" + kv.Value)));

    partial void OnNameChanged(string value) => Edited();
    partial void OnTimeoutChanged(string value) => Edited();
    partial void OnFollowRedirectsChanged(bool value) => Edited();
    partial void OnVerifyTlsChanged(bool value) => Edited();
    partial void OnDefaultEnvironmentChanged(Choice? value) => Edited();
    partial void OnHttpVersionChanged(Choice value) => Edited();
    partial void OnProxyModeChanged(Choice value) => Edited();
    partial void OnProxyUrlChanged(string value) => Edited();
    partial void OnCertPathChanged(string value) => Edited();
    partial void OnCertKeyChanged(string value) => Edited();
    partial void OnCertPasswordChanged(string value) => Edited();
    partial void OnCertHostChanged(string value) => Edited();

    [RelayCommand]
    private async Task PickCertAsync() { if (await PickAsync(new[] { "*.pfx", "*.p12", "*.pem", "*.crt", "*.cer" }) is { } p) CertPath = p; }

    [RelayCommand]
    private async Task PickKeyAsync() { if (await PickAsync(new[] { "*.key", "*.pem" }) is { } p) CertKey = p; }

    /// <summary>A file inside the collection is kept relative, so the setting works in every clone.</summary>
    private async Task<string?> PickAsync(string[] patterns)
    {
        var path = await Dialogs.PickFile(Loc.T("kv.pickFile"), patterns);
        if (path is null) return null;
        var root = _host.Ws.ProjectRoot;
        return root.Length > 0 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path).Replace('\\', '/')
            : path;
    }

    private void Edited()
    {
        IsDirty = _saved.Length > 0 && Snapshot() != _saved;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var label = IsCollection ? Loc.T("set.tab.collection") : RelDir[(RelDir.LastIndexOf('/') + 1)..];
        Title = (IsDirty ? "● " : "") + label;
        ToolTip = FileText;
    }

    public void Relocate(string newRel)
    {
        RelDir = newRel;
        OnPropertyChanged(nameof(FileText));
        OnPropertyChanged(nameof(Heading));
        UpdateTitle();
    }

    [RelayCommand]
    public Task<bool> SaveAsync()
    {
        try
        {
            if (IsCollection)
            {
                var c = _host.Ws.LoadConfig();
                c.Name = Name.Trim();
                c.TimeoutMs = int.TryParse(Timeout.Trim(), out var t) ? Math.Clamp(t, 100, 3_600_000) : c.TimeoutMs;
                c.FollowRedirects = FollowRedirects;
                c.VerifyTls = VerifyTls;
                c.HttpVersion = HttpVersion.Value;
                c.Proxy = ProxyMode.Value == "manual" ? ProxyUrl.Trim() : ProxyMode.Value;
                if (c.Proxy.Length == 0 && ProxyMode.Value == "manual") ProxyMode = ProxyModes[0];
                c.ClientCert = new ClientCertSpec { Path = CertPath.Trim(), Key = CertKey.Trim(), Password = CertPassword, Host = CertHost.Trim() };
                c.DefaultEnvironment = DefaultEnvironment?.Value ?? "";
                c.Auth = Auth.ToModel();
                c.Variables = Variables.ToMap();
                _host.MarkOwnWrite(Workspace.ConfigFile);
                _host.Ws.SaveConfig(c);
                Timeout = c.TimeoutMs.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                var f = _host.Ws.LoadFolder(RelDir);
                f.Auth = Auth.ToModel();
                f.Variables = Variables.ToMap();
                _host.MarkOwnWrite(Workspace.Join(RelDir, Workspace.FolderFile));
                _host.Ws.SaveFolder(RelDir, f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.Notify(Loc.F("set.saveError", e.Message));
            return Task.FromResult(false);
        }
        _saved = Snapshot();
        IsDirty = false;
        UpdateTitle();
        Saved?.Invoke(this);
        _host.Notify(Loc.F("set.saved", FileText));
        return Task.FromResult(true);
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        var choice = await Dialogs.Choose(Loc.T("common.unsaved"), Loc.F("set.unsaved", FileText), Loc.T("common.save"), Loc.T("common.discard"));
        return choice switch { 0 => await SaveAsync(), 1 => true, _ => false };
    }

    public void OnDiskChanged()
    {
        if (!IsDirty) Load();
    }
}
