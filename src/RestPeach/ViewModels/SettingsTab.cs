using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
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

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _timeout = "30000";
    [ObservableProperty] private bool _followRedirects = true;
    [ObservableProperty] private bool _verifyTls = true;
    [ObservableProperty] private Choice? _defaultEnvironment;
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
        Badge = IsCollection ? "API" : "DIR";
        BadgeBrush = IsCollection ? Palette.Green : Palette.Amber;
        Auth.Changed += Edited;
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
        Core.Text.CanonicalJson.Write(Core.Storage.Codec.WriteAuth(Auth.ToModel())),
        string.Join("\n", Variables.ToMap().Select(kv => kv.Key + "=" + kv.Value)));

    partial void OnNameChanged(string value) => Edited();
    partial void OnTimeoutChanged(string value) => Edited();
    partial void OnFollowRedirectsChanged(bool value) => Edited();
    partial void OnVerifyTlsChanged(bool value) => Edited();
    partial void OnDefaultEnvironmentChanged(Choice? value) => Edited();

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
