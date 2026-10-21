using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
using RestPeach.Core.Model;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>
/// Editor of one environment: public variables (shared through git) and secrets (only on this machine). Moving a
/// value between both tables is how a key gets promoted to secret.
/// </summary>
public sealed partial class EnvironmentTab : TabViewModel
{
    private readonly ICollectionHost _host;
    private ApiEnvironment _env;
    private string _saved = "";

    public string Stem => _env.Stem;
    public KeyValueTable Variables { get; } = new() { ShowActive = false, KeyWatermark = Loc.T("kv.variable") };
    public KeyValueTable Secrets { get; } = new() { ShowActive = false, Secret = true, KeyWatermark = Loc.T("kv.secret") };

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _showSecrets;
    [ObservableProperty] private bool _isDefault;

    public string PublicFile => $".api/environments/{Stem}.env.json";
    public string SecretsFile => ".api/environments/.env.secrets.json";

    public event Action<EnvironmentTab>? Saved;
    public event Action<EnvironmentTab>? Deleted;

    public EnvironmentTab(ICollectionHost host, string stem)
    {
        _host = host;
        _env = host.Ws.LoadEnvironment(stem);
        Badge = "ENV";
        BadgeBrush = Palette.Blue;
        Variables.Changed += Edited;
        Secrets.Changed += Edited;
        Load();
    }

    public void Load()
    {
        _env = _host.Ws.LoadEnvironment(_env.Stem);
        Name = _env.DisplayName;
        Variables.Load(_env.Variables);
        Secrets.Load(_env.Secrets);
        IsDefault = string.Equals(_host.Config.DefaultEnvironment, _env.Stem, StringComparison.OrdinalIgnoreCase);
        _saved = Snapshot();
        IsDirty = false;
        UpdateTitle();
    }

    private string Snapshot() =>
        Name.Trim() + "\0" + string.Join("\n", Variables.ToMap().Select(kv => kv.Key + "=" + kv.Value)) + "\0" +
        string.Join("\n", Secrets.ToMap().Select(kv => kv.Key + "=" + kv.Value));

    partial void OnNameChanged(string value) => Edited();
    partial void OnShowSecretsChanged(bool value) => Secrets.Reveal = value;

    private void Edited()
    {
        IsDirty = Snapshot() != _saved;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = (IsDirty ? "● " : "") + (Name.Trim().Length > 0 ? Name.Trim() : _env.Stem);
        ToolTip = PublicFile;
    }

    [RelayCommand]
    public Task<bool> SaveAsync()
    {
        _env.Name = Name.Trim().Length > 0 ? Name.Trim() : _env.Stem;
        _env.Variables = Variables.ToMap();
        _env.Secrets = Secrets.ToMap();
        try
        {
            _host.MarkOwnWrite(Core.Storage.Workspace.EnvironmentsDir);
            _host.Ws.SaveEnvironment(_env);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.Notify(Loc.F("env.saveError", e.Message));
            return Task.FromResult(false);
        }
        _saved = Snapshot();
        IsDirty = false;
        UpdateTitle();
        Saved?.Invoke(this);
        _host.Notify(Loc.F("env.saved", _env.DisplayName));
        return Task.FromResult(true);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!await Dialogs.Confirm(Loc.T("env.delete.title"), Loc.F("env.delete.text", _env.DisplayName), Loc.T("env.delete.ok"))) return;
        try
        {
            _host.MarkOwnWrite(Core.Storage.Workspace.EnvironmentsDir);
            _host.Ws.DeleteEnvironment(_env.Stem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); return; }
        IsDirty = false;
        Deleted?.Invoke(this);
    }

    [RelayCommand]
    private void MakeDefault()
    {
        var c = _host.Ws.LoadConfig();
        c.DefaultEnvironment = _env.Stem;
        _host.MarkOwnWrite(Core.Storage.Workspace.ConfigFile);
        _host.Ws.SaveConfig(c);
        IsDefault = true;
        _host.Notify(Loc.F("env.defaultSet", _env.DisplayName));
    }

    /// <summary>Moves a public variable into the secrets (and the other way): the value leaves the shared file.</summary>
    public void MoveToSecrets(KeyValueRow row) => Move(row, Variables, Secrets);
    public void MoveToPublic(KeyValueRow row) => Move(row, Secrets, Variables);

    private static void Move(KeyValueRow row, KeyValueTable from, KeyValueTable to)
    {
        if (row.Key.Trim().Length == 0) return;
        var items = to.ToList();
        items.RemoveAll(i => i.Key == row.Key.Trim());
        items.Add(row.ToModel());
        to.Load(items);
        var left = from.ToList();
        left.RemoveAll(i => i.Key == row.Key.Trim());
        from.Load(left);
        to.NotifyChanged();
        from.NotifyChanged();
    }

    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        var choice = await Dialogs.Choose(Loc.T("common.unsaved"), Loc.F("env.unsaved", Name), Loc.T("common.save"), Loc.T("common.discard"));
        return choice switch { 0 => await SaveAsync(), 1 => true, _ => false };
    }

    public void OnDiskChanged()
    {
        if (!IsDirty) Load();
    }
}
