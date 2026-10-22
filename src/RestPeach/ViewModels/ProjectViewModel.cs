using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Services;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>
/// The tab of one project: its collection tree (the <c>.api</c> folder as it is on disk), the open documents
/// (requests, environments, settings, runs), the environment picker and the side panel with the variables, the
/// cURL of the current request and the session cookies.
/// </summary>
public partial class ProjectViewModel : TabViewModel, ICollectionHost, IDisposable
{
    private readonly ProjectStore _store;
    private Workspace? _ws;

    public string RootPath { get; }
    public Workspace Ws => _ws ?? throw new InvalidOperationException("No collection");
    public WorkspaceConfig Config { get; private set; } = new();
    public ApiEnvironment? CurrentEnvironment { get; private set; }
    public Dictionary<string, string> Runtime { get; } = new(StringComparer.Ordinal);
    public HttpEngine Engine { get; } = new();
    public Dictionary<string, GraphQLSchema> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool PrettyResponses => _store.State.PrettyResponses;

    public ObservableCollection<TreeNode> Tree { get; } = new();
    public ObservableCollection<RequestHit> FilterHits { get; } = new();
    public ObservableCollection<TabViewModel> Docs { get; } = new();
    public ObservableCollection<EnvironmentChoice> Environments { get; } = new();

    [ObservableProperty] private bool _hasCollection;
    [ObservableProperty] private TabViewModel? _selectedDoc;
    [ObservableProperty] private TreeNode? _selectedNode;
    [ObservableProperty] private RequestHit? _selectedHit;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isFiltering;
    [ObservableProperty] private EnvironmentChoice? _selectedEnvironment;
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _requestsText = "";
    [ObservableProperty] private string _collectionName = "";
    [ObservableProperty] private IReadOnlyList<VariableRow> _variableRows = Array.Empty<VariableRow>();
    [ObservableProperty] private string _variablesHeader = "";
    [ObservableProperty] private string _curlPreview = "";
    [ObservableProperty] private string _curlEmptyText = Loc.T("pv.curl.empty");
    public IReadOnlyList<Choice> CodeLanguages { get; } = CodeGen.Languages.Select(l => new Choice(l.Id, l.Label)).ToList();
    [ObservableProperty] private Choice _codeLanguage;
    [ObservableProperty] private IReadOnlyList<CookieRow> _jarCookies = Array.Empty<CookieRow>();
    [ObservableProperty] private bool _prettyResponse;
    [ObservableProperty] private bool _hasGit;
    [ObservableProperty] private string _branchText = "";
    [ObservableProperty] private string _branchTip = "";
    [ObservableProperty] private string _changesText = "";
    /// <summary>Shown instead of the branch when there is no git information, with the reason as tooltip.</summary>
    [ObservableProperty] private string _noGitTip = "";
    [ObservableProperty] private bool _showNoGit;

    public GitSnapshot? Git { get; private set; }

    public RequestTab? SelectedRequest => SelectedDoc as RequestTab;
    public bool HasRequest => SelectedRequest is not null;
    public bool HasDocs => Docs.Count > 0;
    public bool HasRuntime => Runtime.Count > 0;
    public bool HasCookies => JarCookies.Count > 0;
    public bool HasCurl => CurlPreview.Length > 0;
    public string RuntimeText => Loc.Plural("pv.runtime", Runtime.Count);

    public event Action? FilterFocusRequested;

    private FileSystemWatcher? _watcher, _gitWatcher;
    private CancellationTokenSource? _treeDebounce, _gitDebounce, _envDebounce;
    private readonly Dictionary<string, CancellationTokenSource> _fileDebounce = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _panelTimer;
    private bool _disposed, _restoring;

    public ViewerSettings Settings { get; }

    public ProjectViewModel(string path, ProjectStore store, ViewerSettings settings)
    {
        RootPath = path;
        _store = store;
        _codeLanguage = CodeLanguages.FirstOrDefault(c => c.Value == store.State.CodeLanguage) ?? CodeLanguages[0];
        Settings = settings;
        Title = Path.GetFileName(path.TrimEnd('\\', '/'));
        ToolTip = path;
        _prettyResponse = store.State.PrettyResponses;
        _panelTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _panelTimer.Tick += (_, _) => { _panelTimer.Stop(); RefreshPanel(); };
        Docs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasDocs));
        OpenCollection();
        ScheduleGitRefresh(0);
        StartGitWatcher();
    }

    // ------------------------------------------------------------ collection

    private void OpenCollection()
    {
        _ws = Workspace.Find(RootPath);
        HasCollection = _ws is not null;
        if (_ws is null) return;
        Config = _ws.LoadConfig();
        CollectionName = Config.Name.Length > 0 ? Config.Name : Title;
        LoadEnvironments();
        _ = RebuildTreeAsync(restoreSession: true);
        StartWatcher();
    }

    [RelayCommand]
    private void InitCollection()
    {
        try { Workspace.Init(RootPath, Title); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = e.Message; return; }
        OpenCollection();
        Notice = Loc.T("pv.initDone");
    }

    private void LoadEnvironments()
    {
        var session = _store.SessionFor(RootPath);
        var keep = SelectedEnvironment?.Stem ?? (session.NoEnvironment ? null : session.Environment ?? Config.DefaultEnvironment);
        Environments.Clear();
        Environments.Add(new EnvironmentChoice(null, Loc.T("env.none")));
        foreach (var stem in Ws.ListEnvironments())
        {
            string name;
            try { name = Ws.LoadEnvironment(stem).DisplayName; } catch { name = stem; }
            Environments.Add(new EnvironmentChoice(stem, name));
        }
        _restoring = true;
        SelectedEnvironment = Environments.FirstOrDefault(e => e.Stem is not null && string.Equals(e.Stem, keep, StringComparison.OrdinalIgnoreCase))
                              ?? (session.NoEnvironment ? Environments[0] : Environments.Skip(1).FirstOrDefault() ?? Environments[0]);
        _restoring = false;
        CurrentEnvironment = SelectedEnvironment?.Stem is { } s ? Ws.LoadEnvironment(s) : null;
    }

    partial void OnSelectedEnvironmentChanged(EnvironmentChoice? value)
    {
        if (_ws is null) return;
        CurrentEnvironment = value?.Stem is { } s ? Ws.LoadEnvironment(s) : null;
        if (_restoring) return;
        var session = _store.SessionFor(RootPath);
        session.Environment = value?.Stem;
        session.NoEnvironment = value?.Stem is null;
        _store.Save();
        SchedulePanel();
        Notice = value?.Stem is null ? Loc.T("env.noneSelected") : Loc.F("env.selected", value.Name);
    }

    // ------------------------------------------------------------ tree

    private async Task RebuildTreeAsync(bool restoreSession = false)
    {
        if (_ws is null) return;
        var expanded = new HashSet<string>(Tree.SelectMany(n => n.Descendants().Prepend(n)).Where(n => n.IsFolder && n.IsExpanded).Select(n => n.RelPath),
            StringComparer.OrdinalIgnoreCase);
        if (restoreSession) foreach (var e in _store.SessionFor(RootPath).Expanded) expanded.Add(e);
        var selected = SelectedNode?.RelPath;
        var ws = _ws;
        List<TreeNode> nodes;
        try { nodes = await Task.Run(() => TreeNode.Build(ws)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = e.Message; return; }
        if (_disposed) return;

        _suppressOpen = true;
        Tree.Clear();
        foreach (var n in nodes) Tree.Add(n);
        foreach (var n in All())
        {
            if (n.IsFolder && expanded.Contains(n.RelPath)) n.IsExpanded = true;
            if (selected is not null && string.Equals(n.RelPath, selected, StringComparison.OrdinalIgnoreCase)) SelectedNode = n;
        }
        _suppressOpen = false;
        ApplyGitToTree();
        var count = All().Count(n => n.IsRequest);
        RequestsText = Loc.Plural("common.requests", count);
        ApplyFilter();
        if (restoreSession) RestoreSession();
    }

    public IEnumerable<TreeNode> All() => Tree.SelectMany(n => n.Descendants().Prepend(n));

    private TreeNode? NodeFor(string rel) => All().FirstOrDefault(n => string.Equals(n.RelPath, rel, StringComparison.OrdinalIgnoreCase));

    private bool _suppressOpen;

    partial void OnSelectedNodeChanged(TreeNode? value)
    {
        if (_suppressOpen || value is null || value.IsFolder) return;
        OpenRequest(value.RelPath);
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = Filter.Trim();
        IsFiltering = q.Length > 0;
        FilterHits.Clear();
        if (!IsFiltering) return;
        var requests = All().Where(n => n.IsRequest).ToDictionary(n => Workspace.Join(Workspace.ParentOf(n.RelPath), n.Name), n => n);
        foreach (var key in Dialogs.RankPaths(requests.Keys, q, 300)) FilterHits.Add(new RequestHit { Node = requests[key] });
        SelectedHit = FilterHits.FirstOrDefault();
    }

    public void OpenSelectedHit()
    {
        if (SelectedHit is { } h) OpenRequest(h.Node.RelPath)?.FocusUrl();
    }

    [RelayCommand]
    private void FocusFilter() => FilterFocusRequested?.Invoke();

    /// <summary>Expands the tree down to a request and selects it.</summary>
    public void Reveal(string relPath)
    {
        Filter = "";
        var parts = relPath.Split('/');
        var dir = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            dir = Workspace.Join(dir, parts[i]);
            if (NodeFor(dir) is { } f) f.IsExpanded = true;
        }
        _suppressOpen = true;
        SelectedNode = NodeFor(relPath);
        _suppressOpen = false;
    }

    // ------------------------------------------------------------ documents

    public RequestTab? OpenRequest(string relPath)
    {
        if (_ws is null || !File.Exists(Ws.FullPath(relPath))) { Notice = Loc.F("pv.fileMissing", relPath); return null; }
        var doc = Docs.OfType<RequestTab>().FirstOrDefault(d => string.Equals(d.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            doc = new RequestTab(this, relPath);
            AddDoc(doc);
        }
        SelectedDoc = doc;
        return doc;
    }

    private void AddDoc(TabViewModel doc)
    {
        doc.CloseCommand = new AsyncRelayCommand(() => CloseDocAsync(doc));
        int at = SelectedDoc is null ? Docs.Count : Docs.IndexOf(SelectedDoc) + 1;
        Docs.Insert(Math.Clamp(at, 0, Docs.Count), doc);
    }

    private T OpenSingleton<T>(Func<T, bool> match, Func<T> create) where T : TabViewModel
    {
        var doc = Docs.OfType<T>().FirstOrDefault(match);
        if (doc is null) { doc = create(); AddDoc(doc); }
        SelectedDoc = doc;
        return doc;
    }

    partial void OnSelectedDocChanged(TabViewModel? value)
    {
        OnPropertyChanged(nameof(SelectedRequest));
        OnPropertyChanged(nameof(HasRequest));
        if (value is RequestTab r && !_suppressOpen)
        {
            _suppressOpen = true;
            SelectedNode = NodeFor(r.RelPath);
            _suppressOpen = false;
        }
        RefreshPanel();
        SaveSession();
    }

    public async Task<bool> CloseDocAsync(TabViewModel doc)
    {
        if (!await ConfirmClose(doc)) return false;
        int idx = Docs.IndexOf(doc);
        if (idx < 0) return false;
        bool wasSelected = SelectedDoc == doc;
        Docs.Remove(doc);
        (doc as IDisposable)?.Dispose();
        if (wasSelected || SelectedDoc is null) SelectedDoc = Docs.Count == 0 ? null : Docs[Math.Clamp(idx - 1, 0, Docs.Count - 1)];
        SaveSession();
        return true;
    }

    private static Task<bool> ConfirmClose(TabViewModel doc) => doc switch
    {
        RequestTab r => r.ConfirmCloseAsync(),
        EnvironmentTab e => e.ConfirmCloseAsync(),
        SettingsTab s => s.ConfirmCloseAsync(),
        _ => Task.FromResult(true),
    };

    /// <summary>Ctrl+W: closes the current document. False when there was none.</summary>
    public bool CloseCurrentDoc()
    {
        if (SelectedDoc is not { } d) return false;
        _ = CloseDocAsync(d);
        return true;
    }

    [RelayCommand]
    private async Task CloseOthersAsync()
    {
        foreach (var d in Docs.Where(d => d != SelectedDoc).ToList())
            if (!await CloseDocAsync(d)) return;
    }

    [RelayCommand]
    private async Task CloseAllAsync()
    {
        foreach (var d in Docs.ToList())
            if (!await CloseDocAsync(d)) return;
    }

    [RelayCommand]
    private void NextDoc()
    {
        if (Docs.Count < 2 || SelectedDoc is null) return;
        SelectedDoc = Docs[(Docs.IndexOf(SelectedDoc) + 1) % Docs.Count];
    }

    [RelayCommand]
    private void PreviousDoc()
    {
        if (Docs.Count < 2 || SelectedDoc is null) return;
        SelectedDoc = Docs[(Docs.IndexOf(SelectedDoc) - 1 + Docs.Count) % Docs.Count];
    }

    public bool HasUnsavedChanges => Docs.Any(d => d is RequestTab { IsDirty: true } or EnvironmentTab { IsDirty: true } or SettingsTab { IsDirty: true });

    private void RestoreSession()
    {
        var s = _store.SessionFor(RootPath);
        _restoring = true;
        try
        {
            foreach (var rel in s.Files.Where(f => File.Exists(Ws.FullPath(f)))) OpenRequest(rel);
            SelectedDoc = Docs.OfType<RequestTab>().FirstOrDefault(d => string.Equals(d.RelPath, s.Active, StringComparison.OrdinalIgnoreCase))
                          ?? Docs.FirstOrDefault();
        }
        finally { _restoring = false; }
    }

    private void SaveSession()
    {
        if (_restoring || _disposed || _ws is null) return;
        var s = _store.SessionFor(RootPath);
        s.Files = Docs.OfType<RequestTab>().Select(d => d.RelPath).ToList();
        s.Active = SelectedRequest?.RelPath;
        s.Expanded = All().Where(n => n.IsFolder && n.IsExpanded).Select(n => n.RelPath).ToList();
        _store.Save();
    }

    public void SaveExpanded() => SaveSession();

    // ------------------------------------------------------------ ICollectionHost

    public void Notify(string text) => Notice = text;

    private readonly Dictionary<string, DateTime> _ownWrites = new(StringComparer.OrdinalIgnoreCase);

    public void MarkOwnWrite(string relPath) => _ownWrites[relPath] = DateTime.UtcNow;

    private bool IsOwnWrite(string rel)
    {
        foreach (var (path, at) in _ownWrites)
            if ((rel.StartsWith(path, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow - at < TimeSpan.FromSeconds(2)) return true;
        return false;
    }

    public void RequestEdited(RequestTab tab)
    {
        if (tab == SelectedDoc) SchedulePanel();
    }

    public void RequestSaved(RequestTab tab)
    {
        // The method may have changed: the tree shows it.
        ScheduleTreeRefresh(150);
        SaveSession();
    }

    // ------------------------------------------------------------ history

    private List<RunResult>? _history;

    /// <summary>One file per collection in the app's data folder (outside the project, so never in git).</summary>
    private string HistoryFile => Path.Combine(_store.DataFolder, "history",
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(RootPath.ToLowerInvariant())))[..16] + ".jsonl");

    public IReadOnlyList<RunResult> HistoryEntries => _history ??= LoadHistory();

    private List<RunResult> LoadHistory()
    {
        try { return Core.Storage.History.Load(HistoryFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new List<RunResult>(); }
    }

    public void AddHistory(RunResult result)
    {
        var list = _history ??= LoadHistory();
        list.Add(result);
        var secrets = CurrentEnvironment?.Secrets.Values.ToList() ?? new List<string>();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
            if (list.Count > Core.Storage.History.MaxEntries + 50)
            {
                // Now and then the file is rewritten with the newest entries only.
                list.RemoveRange(0, list.Count - Core.Storage.History.MaxEntries);
                File.WriteAllLines(HistoryFile, list.Select(r => Core.Storage.History.Serialize(r, secrets)));
            }
            else File.AppendAllText(HistoryFile, Core.Storage.History.Serialize(result, secrets) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        foreach (var tab in Docs.OfType<RunTab>().Where(t => t.IsHistory)) tab.Prepend(result);
    }

    public void ClearHistory()
    {
        _history = new List<RunResult>();
        try { File.Delete(HistoryFile); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [RelayCommand]
    private void OpenHistory()
    {
        if (_ws is null) return;
        OpenSingleton<RunTab>(r => r.IsHistory, () => RunTab.History(this, rel => OpenRequest(rel), HistoryEntries, ClearHistory));
    }

    public void RuntimeChanged()
    {
        OnPropertyChanged(nameof(HasRuntime));
        OnPropertyChanged(nameof(RuntimeText));
        SchedulePanel();
    }

    public Task OpenInTextPeachAsync(string? relPath) =>
        relPath is null ? PeachLink.OpenAsync(PeachLocator.TextPeach, RootPath, RootPath)
            : PeachLink.OpenAsync(PeachLocator.TextPeach, RootPath, RootPath, Ws.FullPath(relPath));

    // ------------------------------------------------------------ side panel

    private void SchedulePanel()
    {
        _panelTimer.Stop();
        _panelTimer.Start();
    }

    /// <summary>Variables visible from the current document, the cURL of the current request and the cookie jar.</summary>
    public void RefreshPanel()
    {
        if (_ws is null) return;
        string relDir = SelectedDoc switch
        {
            RequestTab r => r.FolderRel,
            SettingsTab s => s.RelDir,
            RunTab run => run.RelDir,
            _ => "",
        };
        VariableScope scope;
        try { scope = Runner.Scope(Ws, Config, relDir, CurrentEnvironment, Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        var used = new HashSet<string>(StringComparer.Ordinal);
        PreparedRequest? masked = null;
        if (SelectedRequest is { } req)
        {
            var model = req.BuildModel();
            var texts = new List<string> { model.Url, model.Body.Data, model.Body.Variables, model.Auth.Token, model.Auth.Username, model.Auth.Password, model.Auth.Key, model.Auth.Value };
            texts.AddRange(model.Headers.Where(h => h.Active).SelectMany(h => new[] { h.Key, h.Value }));
            texts.AddRange(model.Body.Fields.Where(h => h.Active).SelectMany(h => new[] { h.Key, h.Value }));
            var effective = AuthResolver.Resolve(model.Auth, Ws.FolderChain(req.FolderRel), Config);
            texts.AddRange(new[] { effective.Token, effective.Username, effective.Password, effective.Key, effective.Value });
            foreach (var t in texts) foreach (var n in Interpolator.Names(t)) used.Add(n);
            try { masked = req.Prepare(_ => "••••"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            req.RefreshPreview();
            req.Auth.InheritedText = model.Auth.IsInherit ? DescribeInherited(req.FolderRel) : "";
        }

        var rows = new List<VariableRow>();
        foreach (var v in scope.All)
            rows.Add(new VariableRow(v.Name, v.Source == VariableSource.Secret ? "••••" : v.Value, Look.SourceName(v.Source), Look.Source(v.Source), used.Contains(v.Name), false));
        foreach (var m in used.Where(n => scope.Get(n) is null))
            rows.Add(new VariableRow(m, Loc.T("var.undefined"), "", Palette.Red, true, true));
        VariableRows = rows.OrderByDescending(r => r.Used).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        VariablesHeader = CurrentEnvironment is { } env ? Loc.F("pv.variablesIn", env.DisplayName) : Loc.T("pv.variables");
        // A WebSocket has no cURL form: the panel says so instead of showing "curl -X WS".
        bool ws = masked?.Kind == ApiKind.WebSocket;
        CurlPreview = masked is null || ws ? "" : CodeGen.Generate(CodeLanguage.Value, masked);
        CurlEmptyText = Loc.T(ws ? "ws.noCurl" : "pv.curl.empty");
        JarCookies = Engine.AllCookies().Select(c => new CookieRow(c.Name, c.Value, c.Domain + c.Path)).ToList();
        OnPropertyChanged(nameof(HasCookies));
        OnPropertyChanged(nameof(HasCurl));
        OnPropertyChanged(nameof(HasRuntime));
        OnPropertyChanged(nameof(RuntimeText));
    }

    /// <summary>"Bearer · from folder Users" for requests whose auth is inherited.</summary>
    private string DescribeInherited(string relDir)
    {
        var chain = Ws.FolderChain(relDir);
        var parts = relDir.Length == 0 ? Array.Empty<string>() : relDir.Split('/');
        for (int i = chain.Count - 1; i >= 0; i--)
            if (!chain[i].Auth.IsInherit)
                return Loc.F("auth.inheritedFrom", AuthName(chain[i].Auth), string.Join('/', parts.Take(i + 1)));
        return Config.Auth.Type == AuthTypes.None ? Loc.T("auth.inheritedNone") : Loc.F("auth.inheritedCollection", AuthName(Config.Auth));
    }

    private static string AuthName(AuthSpec a) => a.Type switch
    {
        AuthTypes.Bearer => "Bearer Token",
        AuthTypes.Basic => "Basic Auth",
        AuthTypes.ApiKey => "API Key",
        AuthTypes.Digest => "Digest Auth",
        AuthTypes.AwsSigV4 => "AWS Signature V4",
        AuthTypes.OAuth2 => "OAuth 2.0",
        _ => Loc.T("auth.none"),
    };

    partial void OnPrettyResponseChanged(bool value)
    {
        _store.State.PrettyResponses = value;
        _store.Save();
        foreach (var r in Docs.OfType<RequestTab>()) r.RefreshResponseFormat();
    }

    [RelayCommand]
    private void ClearCookies()
    {
        Engine.ClearCookies();
        RefreshPanel();
        Notice = Loc.T("pv.cookiesCleared");
    }

    [RelayCommand]
    private void ClearRuntime()
    {
        Runtime.Clear();
        RuntimeChanged();
        Notice = Loc.T("pv.runtimeCleared");
    }

    [RelayCommand]
    private async Task CopyCurlPreviewAsync()
    {
        if (SelectedRequest is { } r) await r.CopyCodeAsync(CodeLanguage);
    }

    partial void OnCodeLanguageChanged(Choice value)
    {
        _store.State.CodeLanguage = value.Value;
        _store.Save();
        RefreshPanel();
    }

    // ------------------------------------------------------------ requests and folders

    private string TargetFolder(TreeNode? node) =>
        node is null ? (SelectedNode is { } s ? (s.IsFolder ? s.RelPath : Workspace.ParentOf(s.RelPath)) : "")
            : node.IsFolder ? node.RelPath : Workspace.ParentOf(node.RelPath);

    [RelayCommand]
    private async Task NewRequestAsync(TreeNode? node)
    {
        if (_ws is null) return;
        var folder = TargetFolder(node);
        var name = await Dialogs.Prompt(Loc.T("pv.newRequest.title"), Loc.F("pv.newRequest.label", folder.Length == 0 ? CollectionName : folder), Loc.T("pv.newRequest.default"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var hasBase = Runner.Scope(Ws, Config, folder, CurrentEnvironment, Runtime).Get("BASE_URL") is not null;
        var rel = Ws.CreateRequest(folder, new ApiRequest { Name = name.Trim(), Method = "GET", Url = hasBase ? "{{BASE_URL}}/" : "" });
        await RebuildTreeAsync();
        Reveal(rel);
        OpenRequest(rel)?.FocusUrl();
        Notice = Loc.F("pv.created", rel);
    }

    /// <summary>A POST to {{BASE_URL}}/graphql with an empty operation: the shape every GraphQL request has.</summary>
    private async Task NewGraphQLRequestAsync(TreeNode? node)
    {
        if (_ws is null) return;
        var folder = TargetFolder(node);
        var name = await Dialogs.Prompt(Loc.T("pv.newRequest.title"), Loc.F("pv.newRequest.label", folder.Length == 0 ? CollectionName : folder), Loc.T("pv.newGraphQL.default"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var hasBase = Runner.Scope(Ws, Config, folder, CurrentEnvironment, Runtime).Get("BASE_URL") is not null;
        var rel = Ws.CreateRequest(folder, new ApiRequest
        {
            Name = name.Trim(),
            Method = "POST",
            Url = hasBase ? "{{BASE_URL}}/graphql" : "",
            Body = new BodySpec { Type = BodyTypes.GraphQL, Data = "query {\n  \n}" },
        });
        await RebuildTreeAsync();
        Reveal(rel);
        OpenRequest(rel)?.FocusUrl();
        Notice = Loc.F("pv.created", rel);
    }

    [RelayCommand]
    private async Task NewFolderAsync(TreeNode? node)
    {
        if (_ws is null) return;
        var parent = TargetFolder(node);
        var name = await Dialogs.Prompt(Loc.T("pv.newFolder.title"), Loc.T("pv.newFolder.label"), Loc.T("pv.newFolder.default"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var rel = Ws.CreateFolder(parent, name);
        await RebuildTreeAsync();
        if (NodeFor(rel) is { } n) { Reveal(rel); n.IsExpanded = true; }
        Notice = Loc.F("pv.created", rel);
    }

    private async Task RenameAsync(TreeNode node)
    {
        var name = await Dialogs.Prompt(Loc.T(node.IsFolder ? "pv.renameFolder.title" : "pv.renameRequest.title"), Loc.T("pv.rename.label"), node.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name) return;
        try
        {
            if (node.IsFolder)
            {
                var target = Ws.RenameFolder(node.RelPath, name);
                Relocated(node.RelPath, target, folder: true);
            }
            else
            {
                MarkOwnWrite(node.RelPath);
                var target = Ws.RenameRequest(node.RelPath, name);
                MarkOwnWrite(target);
                foreach (var d in Docs.OfType<RequestTab>().Where(d => d.RelPath.Equals(node.RelPath, StringComparison.OrdinalIgnoreCase)))
                    d.Relocate(target, name.Trim());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Loc.T("pv.rename.error"), e.Message);
            return;
        }
        await RebuildTreeAsync();
        SaveSession();
    }

    /// <summary>Open documents follow a folder that was renamed or moved.</summary>
    private void Relocated(string oldRel, string newRel, bool folder)
    {
        foreach (var d in Docs.OfType<RequestTab>())
        {
            if (!folder && d.RelPath.Equals(oldRel, StringComparison.OrdinalIgnoreCase)) d.Relocate(newRel);
            else if (folder && d.RelPath.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)) d.Relocate(newRel + d.RelPath[oldRel.Length..]);
        }
        foreach (var s in Docs.OfType<SettingsTab>())
            if (folder && (s.RelDir.Equals(oldRel, StringComparison.OrdinalIgnoreCase) || s.RelDir.StartsWith(oldRel + "/", StringComparison.OrdinalIgnoreCase)))
                s.Relocate(newRel + s.RelDir[oldRel.Length..]);
    }

    private async Task DuplicateAsync(TreeNode node)
    {
        var rel = Ws.DuplicateRequest(node.RelPath);
        await RebuildTreeAsync();
        Reveal(rel);
        OpenRequest(rel);
        Notice = Loc.F("pv.created", rel);
    }

    [RelayCommand]
    private async Task DuplicateCurrentAsync()
    {
        if (SelectedRequest is { } r && NodeFor(r.RelPath) is { } n) await DuplicateAsync(n);
    }

    private async Task DeleteAsync(TreeNode node)
    {
        var count = node.IsFolder ? node.Descendants().Count(d => d.IsRequest) : 1;
        var text = node.IsFolder ? Loc.F("pv.delete.folder", node.RelPath, Loc.Plural("common.requests", count)) : Loc.F("pv.delete.request", node.RelPath);
        if (!await Dialogs.Confirm(Loc.T("pv.delete.title"), text, Loc.T("pv.delete.ok"))) return;
        foreach (var d in Docs.ToList())
        {
            bool inside = d switch
            {
                RequestTab r => r.RelPath.Equals(node.RelPath, StringComparison.OrdinalIgnoreCase) || (node.IsFolder && r.RelPath.StartsWith(node.RelPath + "/", StringComparison.OrdinalIgnoreCase)),
                SettingsTab s => node.IsFolder && (s.RelDir.Equals(node.RelPath, StringComparison.OrdinalIgnoreCase) || s.RelDir.StartsWith(node.RelPath + "/", StringComparison.OrdinalIgnoreCase)),
                _ => false,
            };
            if (inside)
            {
                Docs.Remove(d);
                (d as IDisposable)?.Dispose();
            }
        }
        try { Recycle(Ws.FullPath(node.RelPath), node.IsFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            await Dialogs.Message(Loc.T("pv.delete.error"), e.Message);
        }
        await RebuildTreeAsync();
        Notice = Loc.F("pv.deleted", node.RelPath);
        SaveSession();
    }

    /// <summary>To the Recycle Bin (it is a delete you can undo), or a plain delete where there is none.</summary>
    private static void Recycle(string path, bool folder)
    {
        if (OperatingSystem.IsWindows())
        {
            if (folder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else if (folder) Directory.Delete(path, true);
        else File.Delete(path);
    }

    private async Task MoveAsync(TreeNode node)
    {
        var folders = new List<string> { "" };
        folders.AddRange(All().Where(n => n.IsFolder && (!node.IsFolder || !(n.RelPath + "/").StartsWith(node.RelPath + "/", StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.RelPath));
        folders.Remove(Workspace.ParentOf(node.RelPath));
        var labels = folders.Select(f => f.Length == 0 ? "/ " + CollectionName : f).ToList();
        var picked = await Dialogs.PickPath(Loc.F("pv.move.title", node.Name), labels, Loc.T("pv.move.watermark"));
        if (picked is null) return;
        var target = folders[labels.IndexOf(picked)];
        try
        {
            var moved = Ws.Move(node.RelPath, target, node.IsFolder);
            Relocated(node.RelPath, moved, node.IsFolder);
            await RebuildTreeAsync();
            Reveal(moved);
            Notice = Loc.F("pv.moved", moved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Loc.T("pv.move.error"), e.Message);
        }
    }

    public async Task MoveByDropAsync(TreeNode node, TreeNode? target)
    {
        var dir = target is null ? "" : target.IsFolder ? target.RelPath : Workspace.ParentOf(target.RelPath);
        if (string.Equals(dir, Workspace.ParentOf(node.RelPath), StringComparison.OrdinalIgnoreCase)) return;
        if (node.IsFolder && (dir + "/").StartsWith(node.RelPath + "/", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var moved = Ws.Move(node.RelPath, dir, node.IsFolder);
            Relocated(node.RelPath, moved, node.IsFolder);
            await RebuildTreeAsync();
            Reveal(moved);
            Notice = Loc.F("pv.moved", moved);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notice = e.Message; }
    }

    // ------------------------------------------------------------ settings, environments, runs

    [RelayCommand]
    private void CollectionSettings()
    {
        if (_ws is null) return;
        var tab = OpenSingleton<SettingsTab>(s => s.IsCollection, () => Wire(new SettingsTab(this, "")));
    }

    private void FolderSettings(TreeNode node) =>
        OpenSingleton<SettingsTab>(s => s.RelDir.Equals(node.RelPath, StringComparison.OrdinalIgnoreCase), () => Wire(new SettingsTab(this, node.RelPath)));

    private SettingsTab Wire(SettingsTab t)
    {
        t.Saved += _ =>
        {
            Config = Ws.LoadConfig();
            CollectionName = Config.Name.Length > 0 ? Config.Name : Title;
            SchedulePanel();
        };
        return t;
    }

    [RelayCommand]
    private void EditEnvironment()
    {
        if (_ws is null) return;
        if (SelectedEnvironment?.Stem is not { } stem) { _ = NewEnvironmentAsync(); return; }
        OpenEnvironment(stem);
    }

    private void OpenEnvironment(string stem)
    {
        OpenSingleton<EnvironmentTab>(e => e.Stem.Equals(stem, StringComparison.OrdinalIgnoreCase), () =>
        {
            var tab = new EnvironmentTab(this, stem);
            tab.Saved += _ => { LoadEnvironments(); SchedulePanel(); };
            tab.Deleted += t =>
            {
                Docs.Remove(t);
                SelectedEnvironment = null;
                LoadEnvironments();
                SchedulePanel();
                Notice = Loc.F("env.deleted", t.Name);
            };
            return tab;
        });
    }

    [RelayCommand]
    private async Task NewEnvironmentAsync()
    {
        if (_ws is null) return;
        var name = await Dialogs.Prompt(Loc.T("env.new.title"), Loc.T("env.new.label"), Loc.T("env.new.default"));
        if (string.IsNullOrWhiteSpace(name)) return;
        MarkOwnWrite(Workspace.EnvironmentsDir);
        var stem = Ws.CreateEnvironment(name);
        LoadEnvironments();
        SelectedEnvironment = Environments.FirstOrDefault(e => e.Stem == stem);
        OpenEnvironment(stem);
    }

    [RelayCommand]
    private Task RunCollectionAsync() => RunFolderAsync("");

    private async Task RunFolderAsync(string relDir)
    {
        if (_ws is null) return;
        var tab = OpenSingleton<RunTab>(r => r.RelDir.Equals(relDir, StringComparison.OrdinalIgnoreCase), () => new RunTab(this, relDir, rel => OpenRequest(rel)));
        await tab.RunAsync();
    }

    // ------------------------------------------------------------ import / export

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_ws is null) return;
        var file = await Dialogs.PickFile(Loc.T("pv.import.pick"), new[] { "*.json", "*.yaml", "*.yml", "*.txt", "*.sh" });
        if (file is null) return;
        string text;
        try { text = await File.ReadAllTextAsync(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Message(Loc.T("pv.import.error"), e.Message); return; }
        await ImportTextAsync(text, Path.GetFileName(file));
    }

    /// <summary>A file dropped on the window.</summary>
    public async Task ImportFileAsync(string file)
    {
        string text;
        try { text = await File.ReadAllTextAsync(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Message(Loc.T("pv.import.error"), e.Message); return; }
        await ImportTextAsync(text, Path.GetFileName(file));
    }

    [RelayCommand]
    private async Task ImportCurlAsync()
    {
        if (_ws is null) return;
        var text = await Dialogs.TextInput(Loc.T("pv.importCurl.title"), Loc.T("pv.importCurl.label"), "curl https://");
        if (string.IsNullOrWhiteSpace(text)) return;
        await ImportTextAsync(text, "");
    }

    private async Task ImportTextAsync(string text, string fileName)
    {
        ImportResult result;
        try { result = await Task.Run(() => Importer.Import(text, fileName)); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
        {
            await Dialogs.Message(Loc.T("pv.import.error"), e.Message);
            return;
        }
        var target = TargetFolder(null);
        ImportSummary summary;
        try { summary = Importer.Write(Ws, result, target); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Loc.T("pv.import.error"), e.Message);
            return;
        }
        Config = Ws.LoadConfig();
        CollectionName = Config.Name.Length > 0 ? Config.Name : Title;
        LoadEnvironments();
        await RebuildTreeAsync();
        if (summary.FolderRel.Length > 0 && NodeFor(summary.FolderRel) is { IsFolder: true } f) { Reveal(f.RelPath); f.IsExpanded = true; }
        if (summary.Requests == 1 && result.Root.Folders.Count == 0)
        {
            var only = Ws.ListRequests(target).OrderByDescending(r => File.GetLastWriteTimeUtc(Ws.FullPath(r))).FirstOrDefault();
            if (only is not null) { Reveal(only); OpenRequest(only); }
        }
        var message = Loc.F("pv.imported", result.Format, Loc.Plural("common.requests", summary.Requests),
            summary.FolderRel.Length == 0 ? CollectionName : summary.FolderRel);
        if (summary.EnvironmentsCreated + summary.EnvironmentsMerged > 0)
            message += " " + Loc.F("pv.importedEnvs", summary.EnvironmentsCreated, summary.EnvironmentsMerged);
        Notice = message;
        if (result.Warnings.Count > 0)
        {
            // A big collection can have hundreds: the first ones say what kind of thing was left out.
            const int shown = 25;
            var lines = result.Warnings.Take(shown).Select(w => "• " + w).ToList();
            if (result.Warnings.Count > shown) lines.Add(Loc.F("pv.import.more", result.Warnings.Count - shown));
            await Dialogs.Message(Loc.T("pv.import.warnings"), message + "\n\n" + string.Join("\n", lines));
        }
    }

    private async Task ExportAsync(string relDir, string format)
    {
        if (_ws is null) return;
        var baseName = Workspace.SafeFileName(relDir.Length == 0 ? CollectionName : relDir[(relDir.LastIndexOf('/') + 1)..]);
        var (suggested, filter) = format switch
        {
            "postman" => (baseName + ".postman_collection.json", "*.json"),
            "openapi" => (baseName + ".openapi.json", "*.json"),
            _ => (baseName + ".sh", "*.sh"),
        };
        var path = await Dialogs.SaveFile(Loc.T("pv.export." + format), suggested, filter);
        if (path is null) return;
        try
        {
            var text = await Task.Run(() => format switch
            {
                "postman" => Exporter.ToPostman(Ws, relDir),
                "openapi" => Exporter.ToOpenApi(Ws, relDir),
                _ => Exporter.ToBash(Ws, relDir),
            });
            Workspace.WriteText(path, text);
            Notice = Loc.F("pv.exported", path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await Dialogs.Message(Loc.T("pv.export.error"), e.Message);
        }
    }

    [RelayCommand]
    private Task ExportCollection(string? format) => ExportAsync("", format ?? "postman");

    // ------------------------------------------------------------ menus

    public IReadOnlyList<MenuAction> MenuFor(TreeNode? node)
    {
        if (_ws is null) return Array.Empty<MenuAction>();
        var items = new List<MenuAction>();
        if (node is null || node.IsFolder)
        {
            items.Add(new MenuAction(Loc.T("pv.menu.newRequest"), () => NewRequestAsync(node), "Ctrl+N"));
            items.Add(new MenuAction(Loc.T("pv.menu.newGraphQL"), () => NewGraphQLRequestAsync(node)));
            items.Add(new MenuAction(Loc.T("pv.menu.newFolder"), () => NewFolderAsync(node), "Ctrl+Shift+N"));
            items.Add(MenuAction.Separator);
            var dir = node?.RelPath ?? "";
            items.Add(new MenuAction(Loc.T("pv.menu.run"), () => RunFolderAsync(dir)));
            items.Add(node is null
                ? MenuAction.Sync(Loc.T("pv.menu.collectionSettings"), CollectionSettings)
                : MenuAction.Sync(Loc.T("pv.menu.folderSettings"), () => FolderSettings(node)));
            items.Add(MenuAction.Submenu(Loc.T("pv.menu.export"), new[]
            {
                new MenuAction("Postman 2.1…", () => ExportAsync(dir, "postman")),
                new MenuAction("OpenAPI 3…", () => ExportAsync(dir, "openapi")),
                new MenuAction(Loc.T("pv.menu.exportBash"), () => ExportAsync(dir, "bash")),
            }));
        }
        else
        {
            items.Add(MenuAction.Sync(Loc.T("common.open"), () => OpenRequest(node.RelPath)));
            items.Add(new MenuAction(Loc.T("pv.menu.send"), async () => { if (OpenRequest(node.RelPath) is { } t) await t.SendCommand.ExecuteAsync(null); }, "Ctrl+Enter"));
            items.Add(new MenuAction(Loc.T("pv.menu.copyCurl"), async () => { if (OpenRequest(node.RelPath) is { } t) await t.CopyCurlCommand.ExecuteAsync(null); }, "Ctrl+Shift+C"));
            items.Add(new MenuAction(Loc.T("pv.menu.duplicate"), () => DuplicateAsync(node), "Ctrl+D"));
        }
        if (node is not null)
        {
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("pv.menu.rename"), () => RenameAsync(node), "F2"));
            items.Add(new MenuAction(Loc.T("pv.menu.move"), () => MoveAsync(node)));
            items.Add(new MenuAction(Loc.T("pv.menu.copyPath"), () => CopyPathAsync(node)));
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("peach.openTextPeach"), () => OpenInTextPeachAsync(node.RelPath)));
            items.Add(MenuAction.Sync(Loc.T("pv.menu.showInFolder"), () => ShowInFolder(node)));
            items.Add(MenuAction.Separator);
            items.Add(new MenuAction(Loc.T("pv.menu.delete"), () => DeleteAsync(node), "Delete"));
        }
        return MenuList.Clean(items);
    }

    public IReadOnlyList<MenuAction> MenuForSelected() => MenuFor(SelectedNode);

    public Task RenameSelectedAsync() => SelectedNode is { } n ? RenameAsync(n) : Task.CompletedTask;
    public Task DeleteSelectedAsync() => SelectedNode is { } n ? DeleteAsync(n) : Task.CompletedTask;

    private async Task CopyPathAsync(TreeNode node)
    {
        if (Dialogs.Owner?.Clipboard is not { } cb) return;
        var text = Path.GetRelativePath(RootPath, Ws.FullPath(node.RelPath)).Replace('\\', '/');
        await cb.SetTextAsync(text);
        Notice = Loc.F("rv.copiedWhat", text);
    }

    private void ShowInFolder(TreeNode node)
    {
        var full = Ws.FullPath(node.RelPath);
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start("explorer.exe", node.IsFolder ? $"\"{full}\"" : $"/select,\"{full}\"");
            else Process.Start(new ProcessStartInfo(node.IsFolder ? full : Path.GetDirectoryName(full)!) { UseShellExecute = true });
        }
        catch (Exception e) { Notice = e.Message; }
    }

    // ------------------------------------------------------------ toolbar and keys

    [RelayCommand]
    private async Task QuickOpenRequestAsync()
    {
        if (_ws is null) return;
        var nodes = All().Where(n => n.IsRequest).ToList();
        var labels = nodes.Select(n => Workspace.Join(Workspace.ParentOf(n.RelPath), n.Name)).ToList();
        var picked = await Dialogs.PickPath(Loc.T("pv.goToRequest"), labels, Loc.T("pv.goToRequest.watermark"),
            label => nodes[labels.IndexOf(label)].MethodBrush);
        if (picked is null) return;
        var rel = nodes[labels.IndexOf(picked)].RelPath;
        Reveal(rel);
        OpenRequest(rel)?.FocusUrl();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_ws is null) { OpenCollection(); return; }
        Config = Ws.LoadConfig();
        LoadEnvironments();
        await RebuildTreeAsync();
        foreach (var d in Docs)
        {
            switch (d)
            {
                case RequestTab r: r.OnDiskChanged(); break;
                case EnvironmentTab e: e.OnDiskChanged(); break;
                case SettingsTab s: s.OnDiskChanged(); break;
            }
        }
        ScheduleGitRefresh(0);
        RefreshPanel();
        Notice = Loc.T("pv.refreshed");
    }

    [RelayCommand]
    private Task OpenProjectInTextPeachAsync() => OpenInTextPeachAsync(SelectedRequest?.RelPath);

    [RelayCommand]
    private void OpenProjectFolder()
    {
        try { Process.Start(new ProcessStartInfo(_ws?.Root ?? RootPath) { UseShellExecute = true }); }
        catch (Exception e) { Notice = e.Message; }
    }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        switch (SelectedDoc)
        {
            case RequestTab r: await r.SaveAsync(); break;
            case EnvironmentTab e: await e.SaveAsync(); break;
            case SettingsTab s: await s.SaveAsync(); break;
        }
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (var d in Docs.ToList())
        {
            switch (d)
            {
                case RequestTab { IsDirty: true } r: await r.SaveAsync(); break;
                case EnvironmentTab { IsDirty: true } e: await e.SaveAsync(); break;
                case SettingsTab { IsDirty: true } s: await s.SaveAsync(); break;
            }
        }
    }

    [RelayCommand]
    private async Task SendCurrentAsync()
    {
        if (SelectedRequest is { } r) await r.SendCommand.ExecuteAsync(null);
        else if (SelectedDoc is RunTab run) await run.RunAsync();
    }

    [RelayCommand]
    private void FocusUrl() => SelectedRequest?.FocusUrl();

    [RelayCommand] private void ZoomIn() => Settings.Zoom(+1);
    [RelayCommand] private void ZoomOut() => Settings.Zoom(-1);
    [RelayCommand] private void ZoomReset() => Settings.Zoom(0);
    [RelayCommand] private void ToggleWrap() => Settings.WordWrap = !Settings.WordWrap;

    [RelayCommand]
    private async Task CopyCurrentCurlAsync()
    {
        if (SelectedRequest is { } r) await r.CopyCurlCommand.ExecuteAsync(null);
    }

    // ------------------------------------------------------------ watcher

    private void StartWatcher()
    {
        _watcher?.Dispose();
        if (_ws is null) return;
        try
        {
            _watcher = new FileSystemWatcher(_ws.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += (_, e) => OnFsEvent(e.FullPath, structural: false);
            _watcher.Created += (_, e) => OnFsEvent(e.FullPath, structural: true);
            _watcher.Deleted += (_, e) => OnFsEvent(e.FullPath, structural: true);
            _watcher.Renamed += (_, e) => { OnFsEvent(e.OldFullPath, true); OnFsEvent(e.FullPath, true); };
            _watcher.Error += (_, _) => ScheduleTreeRefresh();
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* no watching; F5 still works */ }
    }

    private void OnFsEvent(string fullPath, bool structural)
    {
        if (_disposed || _ws is null) return;
        var rel = _ws.RelPath(fullPath);
        if (rel.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("..")) return;
        Dispatcher.UIThread.Post(() =>
        {
            ScheduleGitRefresh();
            if (rel.StartsWith(Workspace.EnvironmentsDir + "/", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOwnWrite(Workspace.EnvironmentsDir)) ScheduleEnvironmentsReload();
                return;
            }
            if (rel.Equals(Workspace.ConfigFile, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOwnWrite(Workspace.ConfigFile)) ScheduleEnvironmentsReload();
                return;
            }
            if (rel.EndsWith("/" + Workspace.FolderFile, StringComparison.OrdinalIgnoreCase) || rel.Equals(Workspace.FolderFile, StringComparison.OrdinalIgnoreCase))
            {
                if (!IsOwnWrite(rel))
                    foreach (var s in Docs.OfType<SettingsTab>().Where(s => s.RelDir.Equals(Workspace.ParentOf(rel), StringComparison.OrdinalIgnoreCase))) s.OnDiskChanged();
                SchedulePanel();
                return;
            }
            bool isRequest = Workspace.IsRequestFile(rel);
            if (structural || isRequest) ScheduleTreeRefresh();
            if (isRequest) ScheduleFileReload(rel);
        });
    }

    private void ScheduleFileReload(string rel)
    {
        var doc = Docs.OfType<RequestTab>().FirstOrDefault(d => d.RelPath.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return;
        if (_fileDebounce.TryGetValue(rel, out var old)) old.Cancel();
        var cts = _fileDebounce[rel] = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Task.Delay(300, cts.Token); } catch (OperationCanceledException) { return; }
            doc.OnDiskChanged();
        });
    }

    private void ScheduleTreeRefresh(int delayMs = 700)
    {
        _treeDebounce?.Cancel();
        var cts = _treeDebounce = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Task.Delay(delayMs, cts.Token); } catch (OperationCanceledException) { return; }
            if (!_disposed) await RebuildTreeAsync();
        });
    }

    private void ScheduleEnvironmentsReload()
    {
        _envDebounce?.Cancel();
        var cts = _envDebounce = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await Task.Delay(400, cts.Token); } catch (OperationCanceledException) { return; }
            if (_disposed || _ws is null) return;
            Config = Ws.LoadConfig();
            CollectionName = Config.Name.Length > 0 ? Config.Name : Title;
            LoadEnvironments();
            foreach (var e in Docs.OfType<EnvironmentTab>()) e.OnDiskChanged();
            foreach (var s in Docs.OfType<SettingsTab>().Where(s => s.IsCollection)) s.OnDiskChanged();
            SchedulePanel();
        });
    }

    // ------------------------------------------------------------ git (read-only)

    private void StartGitWatcher()
    {
        var repo = GitStatus.FindRepoRoot(RootPath);
        var gitDir = repo is null ? null : Path.Combine(repo, ".git");
        if (gitDir is null || !Directory.Exists(gitDir)) return;
        try
        {
            _gitWatcher = new FileSystemWatcher(gitDir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
            FileSystemEventHandler h = (_, e) => { if (!e.Name!.EndsWith(".lock")) Dispatcher.UIThread.Post(() => ScheduleGitRefresh()); };
            _gitWatcher.Changed += h;
            _gitWatcher.Created += h;
            _gitWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    /// <summary>Re-reads branch and file states after a short pause, so a burst of changes costs one <c>git status</c>.</summary>
    public void ScheduleGitRefresh(int delayMs = 600)
    {
        if (_disposed) return;
        _gitDebounce?.Cancel();
        var cts = _gitDebounce = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { if (delayMs > 0) await Task.Delay(delayMs, cts.Token); } catch (OperationCanceledException) { return; }
            var snap = await GitStatus.ReadAsync(RootPath, cts.Token);
            if (cts.IsCancellationRequested || _disposed) return;
            ApplyGit(snap);
        });
    }

    private void ApplyGit(GitSnapshot? snap)
    {
        Git = snap;
        HasGit = snap is not null;
        ApplyGitToTree();
        if (snap is null)
        {
            BranchText = BranchTip = ChangesText = "";
            // Any folder can hold a collection; without git only the git extras are missing.
            NoGitTip = Loc.T(GitStatus.FindRepoRoot(RootPath) is null ? "git.none.notRepo"
                : !GitStatus.IsAvailable ? "git.none.noGit" : "git.none.failed");
            ShowNoGit = true;
            return;
        }
        ShowNoGit = false;
        NoGitTip = "";
        var name = snap.Detached ? GitStatus.ReadDetachedCommit(snap.RepoRoot) ?? "HEAD" : snap.Branch;
        var sync = (snap.Ahead > 0 ? $" ↑{snap.Ahead}" : "") + (snap.Behind > 0 ? $" ↓{snap.Behind}" : "");
        BranchText = name + sync;
        // Only what changed in the collection: that is what this app is responsible for.
        var prefix = ApiPrefix();
        int changes = snap.Files.Count(f => f.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && f.Value is not (GitChange.None or GitChange.Ignored));
        ChangesText = changes > 0 ? Loc.Plural("git.changes", changes) : "";
        var lines = new List<string> { Loc.F(snap.Detached ? "git.tip.detached" : "git.tip.branch", name) };
        if (snap.Ahead > 0) lines.Add(Loc.Plural("git.tip.ahead", snap.Ahead));
        if (snap.Behind > 0) lines.Add(Loc.Plural("git.tip.behind", snap.Behind));
        lines.Add(changes > 0 ? Loc.Plural("git.tip.changes", changes) : Loc.T("git.tip.clean"));
        lines.Add(Loc.T("git.tip.open"));
        BranchTip = string.Join(Environment.NewLine, lines);
    }

    /// <summary>".api/" — where the collection sits inside the project, as git paths see it.</summary>
    private string ApiPrefix() => _ws is null ? "" : Path.GetRelativePath(RootPath, _ws.Root).Replace('\\', '/').TrimEnd('/') + "/";

    private void ApplyGitToTree()
    {
        var prefix = ApiPrefix();
        foreach (var n in All()) n.Change = Git?.For(prefix + n.RelPath, n.IsFolder) ?? GitChange.None;
    }

    /// <summary>The branch in the status bar: commits, pushes and diffs of the collection happen in GitPeach.</summary>
    [RelayCommand]
    private Task OpenInGitPeachAsync() =>
        PeachLink.OpenAsync(PeachLocator.GitPeach, RootPath, Git?.RepoRoot ?? GitStatus.FindRepoRoot(RootPath) ?? RootPath);

    public void Dispose()
    {
        _disposed = true;
        _treeDebounce?.Cancel();
        _gitDebounce?.Cancel();
        _envDebounce?.Cancel();
        _panelTimer.Stop();
        _watcher?.Dispose();
        _gitWatcher?.Dispose();
        foreach (var d in Docs.OfType<IDisposable>()) d.Dispose();
        Engine.Dispose();
    }
}
