using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;
using RestPeach.Core.Testing;
using RestPeach.Views;

namespace RestPeach.ViewModels;

/// <summary>What a document tab needs from the collection it belongs to.</summary>
public interface ICollectionHost
{
    Workspace Ws { get; }
    WorkspaceConfig Config { get; }
    ApiEnvironment? CurrentEnvironment { get; }
    Dictionary<string, string> Runtime { get; }
    HttpEngine Engine { get; }
    bool PrettyResponses { get; }
    void Notify(string text);
    /// <summary>A file is about to be written by the app itself (the watcher should not treat it as an outside change).</summary>
    void MarkOwnWrite(string relPath);
    void RequestEdited(RequestTab tab);
    void RequestSaved(RequestTab tab);
    void RuntimeChanged();
    /// <summary>GraphQL schemas loaded in this session, by endpoint URL (shared by the tabs that use it).</summary>
    Dictionary<string, GraphQLSchema> Schemas { get; }
    /// <summary>A request was sent from a tab: it goes to the history.</summary>
    void AddHistory(RunResult result);
    Task OpenInTextPeachAsync(string? relPath);
    void Reveal(string relPath);
}

public sealed record HeaderRow(string Key, string Value);

public sealed record CookieRow(string Name, string Value, string Attributes);

public sealed record TestRow(string Glyph, IBrush Brush, string Line, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

/// <summary>A line about the response in the terms of its API style (see <see cref="ProtocolInsight"/>).</summary>
public sealed record InsightRow(string Text, IBrush Brush);

/// <summary>One line of the WebSocket log: a message sent (↑) or received (↓), or what happened to the connection.</summary>
public sealed record WsLogRow(string Time, string Glyph, IBrush Brush, string Preview, string Text, string Size, bool IsMessage);

/// <summary>
/// One request open in a tab: an editable copy of its file plus the last response. Nothing is written until Save
/// (Ctrl+S); the dot in the title says there are unsaved changes. Sending uses what is on screen, saved or not.
/// </summary>
public sealed partial class RequestTab : TabViewModel, IDisposable
{
    private readonly ICollectionHost _host;
    private JsonObject _extra = new();
    private string _name = "";
    private string _savedText = "";
    private bool _loading, _syncingParams;
    private CancellationTokenSource? _sendCts;
    private readonly DispatcherTimer _dirtyTimer;

    public string RelPath { get; private set; }
    public string FolderRel => Workspace.ParentOf(RelPath);
    public string FullPath => _host.Ws.FullPath(RelPath);
    public string Name => _name;

    public IReadOnlyList<string> Methods { get; } = ApiRequest.Methods;
    public IReadOnlyList<Choice> BodyTypes { get; } = new[]
    {
        new Choice(Core.Model.BodyTypes.None, Loc.T("body.none")),
        new Choice(Core.Model.BodyTypes.Json, "JSON"),
        new Choice(Core.Model.BodyTypes.GraphQL, "GraphQL"),
        new Choice(Core.Model.BodyTypes.Text, Loc.T("body.text")),
        new Choice(Core.Model.BodyTypes.Xml, "XML"),
        new Choice(Core.Model.BodyTypes.Html, "HTML"),
        new Choice(Core.Model.BodyTypes.FormData, "Form-data"),
        new Choice(Core.Model.BodyTypes.UrlEncoded, "x-www-form-urlencoded"),
        new Choice(Core.Model.BodyTypes.Binary, Loc.T("body.binary")),
    };

    public KeyValueTable Params { get; } = new() { KeyWatermark = Loc.T("kv.param") };
    public KeyValueTable Headers { get; } = new() { KeyWatermark = Loc.T("kv.header"), Suggestions = KeyValueTable.CommonHeaders };
    public KeyValueTable BodyFields { get; } = new() { ShowType = true, KeyWatermark = Loc.T("kv.field") };
    public AuthEditor Auth { get; } = new(canInherit: true);
    public TextDocument BodyDocument { get; } = new();
    /// <summary>GraphQL variables (JSON); the query itself is <see cref="BodyDocument"/>.</summary>
    public TextDocument VariablesDocument { get; } = new();
    public TextDocument ResponseDocument { get; } = new();
    /// <summary>The WebSocket message selected in the log, in full.</summary>
    public TextDocument MessageDocument { get; } = new();
    /// <summary>A WebSocket message is text: JSON (highlighted, "Format") or anything else.</summary>
    public IReadOnlyList<Choice> MessageTypes { get; }
    public ObservableCollection<WsLogRow> WsLog { get; } = new();
    public ObservableCollection<WsMessage> SavedMessages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MethodBrush))]
    private string _method = "GET";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawBody), nameof(IsFormBody), nameof(IsNoBody), nameof(BodyExtension), nameof(IsGraphQLBody), nameof(IsEditorBody), nameof(IsBinaryBody))]
    private Choice _bodyType;
    /// <summary>Binary body: the file sent, relative to the collection when it is inside it.</summary>
    [ObservableProperty] private string _binaryPath = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKind), nameof(KindName), nameof(KindTip), nameof(KindBrush), nameof(IsWebSocket), nameof(IsHttp),
        nameof(CanSend), nameof(CanConnect), nameof(IsComposer), nameof(BodyTab), nameof(BodyExtension), nameof(ResponseTitle), nameof(HasEventLog))]
    private ApiKind _kind;
    /// <summary>The last response is a Server-Sent Events stream: its events fill the same log as a WebSocket's messages.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEventLog), nameof(WsLogTab))]
    private bool _isEventStream;
    [ObservableProperty] private string _testsText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsParamsSection), nameof(IsHeadersSection), nameof(IsAuthSection), nameof(IsBodySection), nameof(IsTestsSection), nameof(IsDocsSection))]
    private string _section = "params";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _changedOnDisk;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private string _resolvedUrl = "";
    [ObservableProperty] private string _missingText = "";

    // ---- response
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(CanConnect))]
    private bool _isSending;
    [ObservableProperty] private bool _hasResponse;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private IBrush _statusBrush = Palette.OkBadge;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _testsSummary = "";
    [ObservableProperty] private IBrush _testsSummaryBrush = Palette.Dim;
    [ObservableProperty] private string? _responseError;
    [ObservableProperty] private string _responseNote = "";
    [ObservableProperty] private string _responseExtension = ".txt";
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _responseImage;
    [ObservableProperty] private IReadOnlyList<HeaderRow> _responseHeaders = Array.Empty<HeaderRow>();
    [ObservableProperty] private IReadOnlyList<CookieRow> _responseCookies = Array.Empty<CookieRow>();
    [ObservableProperty] private IReadOnlyList<TestRow> _testRows = Array.Empty<TestRow>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInsights))]
    private IReadOnlyList<InsightRow> _insights = Array.Empty<InsightRow>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyResponse), nameof(IsHeadersResponse), nameof(IsCookiesResponse), nameof(IsTestsResponse), nameof(IsMessagesResponse))]
    private string _responseSection = "body";
    [ObservableProperty] private string _responseUrl = "";

    // ---- WebSocket
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _wsConnected;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedWsRow))]
    private WsLogRow? _selectedWsRow;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedSavedMessage))]
    private WsMessage? _selectedSavedMessage;
    [ObservableProperty] private string _messageExtension = ".txt";
    private WebSocketSession? _session;

    private ApiResponse? _lastResponse;

    /// <summary>Raised when the view should put the caret in the URL box (Ctrl+L, new request).</summary>
    public event Action? FocusUrlRequested;
    /// <summary>The view swaps the highlighting of the response editor when the body kind changes.</summary>
    public event Action? ResponseChanged;
    public event Action? BodyTypeChangedForView;
    /// <summary>The view swaps the highlighting of the message viewer.</summary>
    public event Action? MessageChanged;

    public RequestTab(ICollectionHost host, string relPath)
    {
        _host = host;
        RelPath = relPath;
        _bodyType = BodyTypes[0];
        MessageTypes = BodyTypes.Where(b => b.Value is Core.Model.BodyTypes.Json or Core.Model.BodyTypes.Text).ToList();
        BodyFields.FileBase = host.Ws.ProjectRoot;
        ToolTip = relPath;

        _dirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _dirtyTimer.Tick += (_, _) => { _dirtyTimer.Stop(); RecomputeDirty(); };

        Params.Changed += OnParamsChanged;
        Headers.Changed += Edited;
        BodyFields.Changed += Edited;
        Auth.Changed += Edited;
        Auth.ConnectOAuth(host.Engine,
            a => RequestPreparer.ExpandDeferred(a, Runner.Scope(_host.Ws, _host.Config, FolderRel, _host.CurrentEnvironment, _host.Runtime)),
            () => Options(BuildModel()), host.Notify);
        BodyDocument.TextChanged += (_, _) => Edited();
        VariablesDocument.TextChanged += (_, _) => Edited();
        Load();
    }

    public IBrush MethodBrush => Look.Method(Method);
    public bool IsRawBody => Core.Model.BodyTypes.IsRaw(BodyType.Value);
    public bool IsFormBody => Core.Model.BodyTypes.IsForm(BodyType.Value);
    public bool IsNoBody => BodyType.Value == Core.Model.BodyTypes.None;
    public bool IsGraphQLBody => BodyType.Value == Core.Model.BodyTypes.GraphQL;
    public bool IsBinaryBody => BodyType.Value == Core.Model.BodyTypes.Binary;
    /// <summary>The body is edited as text (raw bodies, and the query of a GraphQL body).</summary>
    public bool IsEditorBody => IsRawBody || IsGraphQLBody;
    public bool IsWebSocket => Kind == ApiKind.WebSocket;
    public bool IsHttp => !IsWebSocket;
    /// <summary>The body editor is on screen: a text body, or the message being written to a WebSocket.</summary>
    public bool IsComposer => IsEditorBody || IsWebSocket;
    public bool HasSelectedWsRow => SelectedWsRow is not null;
    public bool HasSelectedSavedMessage => SelectedSavedMessage is not null;
    public bool HasLog => WsLog.Count > 0;
    public bool HasEventLog => IsWebSocket || IsEventStream;
    public string ResponseTitle => IsWebSocket ? Loc.T("ws.connection") : Loc.T("rv.response");
    public bool HasInsights => Insights.Count > 0;
    public bool HasKind => Kind != ApiKind.Rest;
    public string KindName => ApiKinds.Name(Kind);
    public IBrush KindBrush => Look.TagBrush(Method, Kind);
    public string KindTip => Kind switch
    {
        ApiKind.GraphQL => Loc.T("kind.graphql.tip"),
        ApiKind.JsonRpc => Loc.T("kind.jsonrpc.tip"),
        ApiKind.Soap => Loc.T("kind.soap.tip"),
        ApiKind.WebSocket => Loc.T("kind.ws.tip"),
        _ => "",
    };

    private bool? _followRedirects;

    /// <summary>"Do not follow redirects" for this request only (the ⋯ menu); off = the collection's setting.</summary>
    public bool NoRedirects
    {
        get => _followRedirects == false;
        set
        {
            bool? v = value ? false : null;
            if (_followRedirects == v) return;
            _followRedirects = v;
            OnPropertyChanged();
            Edited();
        }
    }

    public string BodyExtension => IsWebSocket && BodyType.Value != Core.Model.BodyTypes.Json ? ".txt" : BodyType.Value switch
    {
        Core.Model.BodyTypes.Json => ".json",
        Core.Model.BodyTypes.GraphQL => ".graphql",
        Core.Model.BodyTypes.Xml => ".xml",
        Core.Model.BodyTypes.Html => ".html",
        _ => ".txt",
    };
    public bool CanSend => !IsSending && !IsWebSocket;
    public bool CanConnect => IsWebSocket && !IsSending && !WsConnected;

    public bool IsParamsSection => Section == "params";
    public bool IsHeadersSection => Section == "headers";
    public bool IsAuthSection => Section == "auth";
    public bool IsBodySection => Section == "body";
    public bool IsTestsSection => Section == "tests";
    public bool IsDocsSection => Section == "docs";
    public bool IsBodyResponse => ResponseSection == "body";
    public bool IsHeadersResponse => ResponseSection == "headers";
    public bool IsCookiesResponse => ResponseSection == "cookies";
    public bool IsTestsResponse => ResponseSection == "tests";
    public bool IsMessagesResponse => ResponseSection == "messages";

    public string ParamsTab => Count(Loc.T("rv.params"), Params.ActiveCount);
    public string HeadersTab => Count(Loc.T("rv.headers"), Headers.ActiveCount);
    public string AuthTab => Auth.Summary.Length > 0 ? Loc.T("rv.auth") + " ●" : Loc.T("rv.auth");
    public string BodyTab => IsWebSocket ? Count(Loc.T("ws.message"), SavedMessages.Count)
        : IsNoBody ? Loc.T("rv.body") : Loc.T("rv.body") + " ●";
    public string TestsTab => Count(Loc.T("rv.tests"), TestLines().Count(l => !Assertions.IsComment(l)));
    public string ResponseHeadersTab => Count(Loc.T("rv.headers"), ResponseHeaders.Count);
    public string ResponseCookiesTab => Count(Loc.T("rv.cookies"), ResponseCookies.Count);
    public string ResponseTestsTab => Count(Loc.T("rv.tests"), TestRows.Count);
    public string WsLogTab => Count(Loc.T(IsWebSocket ? "ws.log" : "sse.events"), WsLog.Count(r => r.IsMessage));

    private static string Count(string label, int n) => n > 0 ? $"{label} ({n})" : label;

    // ------------------------------------------------------------ loading / model

    /// <summary>Reads the file again, dropping unsaved edits (the last response stays).</summary>
    public void Load()
    {
        string text;
        ApiRequest r;
        try
        {
            text = File.ReadAllText(FullPath);
            r = Workspace.ParseRequest(text, RelPath);
            LoadError = null;
        }
        catch (Exception e)
        {
            LoadError = Loc.F("rv.loadError", e.Message);
            _name = Workspace.NameOf(RelPath);
            UpdateTitle();
            return;
        }
        Apply(r);
        _savedText = Workspace.Serialize(r, RelPath);
        ChangedOnDisk = false;
        IsDirty = false;
        UpdateTitle();
        // A request with a body opens on the Body tab; the rest on Params.
        Section = r.Body.Type != Core.Model.BodyTypes.None || ApiKinds.IsWebSocket(r) ? "body" : "params";
    }

    private void Apply(ApiRequest r)
    {
        _loading = true;
        try
        {
            _name = r.Name;
            _extra = (JsonObject)r.Extra.DeepClone();
            Method = ApiRequest.Methods.Contains(r.Method) ? r.Method : r.Method.Length > 0 ? r.Method : "GET";
            _syncingParams = true;
            Url = r.Url;
            Params.Load(QueryString.Parse(r.Url).Concat(r.DisabledParams.Select(p => { var c = p.Clone(); c.Active = false; return c; })));
            _syncingParams = false;
            Description = r.Description;
            Headers.Load(r.Headers);
            Auth.Load(r.Auth);
            BodyType = BodyTypes.FirstOrDefault(b => b.Value == r.Body.Type) ?? BodyTypes[0];
            // A WebSocket message is JSON or text; an empty one shows as text.
            if (ApiKinds.IsWebSocket(r) && !MessageTypes.Contains(BodyType)) BodyType = MessageTypes[1];
            BinaryPath = r.Body.Type == Core.Model.BodyTypes.Binary ? r.Body.Data : "";
            BodyDocument.Text = r.Body.Type == Core.Model.BodyTypes.Binary ? "" : r.Body.Data;
            VariablesDocument.Text = r.Body.Variables;
            BodyFields.Load(r.Body.Fields);
            TestsText = string.Join("\n", r.Tests);
            SavedMessages.Clear();
            foreach (var m in r.Messages) SavedMessages.Add(m.Clone());
            _followRedirects = r.FollowRedirects;
            OnPropertyChanged(nameof(NoRedirects));
            Kind = ApiKinds.Of(r);
        }
        finally
        {
            _loading = false;
            _syncingParams = false;
        }
        NotifyTabs();
        if (IsGraphQLBody) RefreshSchemaStatus();
        _host.RequestEdited(this);
    }

    private IEnumerable<string> TestLines() => TestsText.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0);

    /// <summary>The request as it is on screen right now.</summary>
    public ApiRequest BuildModel()
    {
        var all = Params.ToList();
        var body = new BodySpec { Type = BodyType.Value, Data = IsBinaryBody ? BinaryPath.Trim() : BodyDocument.Text, Variables = VariablesDocument.Text, Fields = BodyFields.ToList() };
        if (Method == ApiRequest.WebSocket || ApiKinds.IsWebSocketUrl(Url))
        {
            // The message being written: kept (and sent by collection runs) only when there is one.
            body = BodyDocument.TextLength == 0 ? new BodySpec()
                : new BodySpec { Type = BodyType.Value == Core.Model.BodyTypes.Json ? Core.Model.BodyTypes.Json : Core.Model.BodyTypes.Text, Data = BodyDocument.Text };
        }
        return new ApiRequest
        {
            Name = _name,
            Method = Method.Trim().ToUpperInvariant(),
            Url = Url,
            Description = Description,
            Headers = Headers.ToList(),
            DisabledParams = all.Where(p => !p.Active).ToList(),
            Auth = Auth.ToModel(),
            Body = body,
            Tests = TestLines().ToList(),
            FollowRedirects = _followRedirects,
            Messages = SavedMessages.Select(m => m.Clone()).ToList(),
            Extra = (JsonObject)_extra.DeepClone(),
        };
    }

    // ------------------------------------------------------------ editing

    partial void OnMethodChanged(string value)
    {
        Badge = Look.Tag(value, Kind);
        BadgeBrush = Look.TagBrush(value, Kind);
        OnPropertyChanged(nameof(KindBrush));
        Edited();
        if (!_loading) Kind = ApiKinds.Of(BuildModel());
    }

    partial void OnKindChanged(ApiKind value)
    {
        Badge = Look.Tag(Method, value);
        BadgeBrush = Look.TagBrush(Method, value);
        BodyTypeChangedForView?.Invoke();
        if (value == ApiKind.WebSocket)
        {
            if (!MessageTypes.Contains(BodyType)) BodyType = MessageTypes[BodyType.Value == Core.Model.BodyTypes.None ? 1 : 0];
            if (!_loading && !HasResponse) Section = "body";
            if (ResponseSection is "body" or "tests") ResponseSection = "messages";
        }
        else
        {
            if (WsConnected) _ = _session?.CloseAsync();
            if (ResponseSection == "messages") ResponseSection = "body";
        }
    }

    partial void OnUrlChanged(string value)
    {
        if (_loading) return;
        // Pasting a cURL command into the URL turns it into this request.
        if (Curl.LooksLikeCurl(value) && Curl.Parse(value) is { } parsed)
        {
            Dispatcher.UIThread.Post(() => ApplyCurl(parsed));
            return;
        }
        // Typing ws:// or wss:// makes it a WebSocket.
        if (ApiKinds.IsWebSocketUrl(value) && Method != ApiRequest.WebSocket) Method = ApiRequest.WebSocket;
        if (!_syncingParams)
        {
            _syncingParams = true;
            var disabled = Params.ToList().Where(p => !p.Active);
            Params.Load(QueryString.Parse(value).Concat(disabled));
            _syncingParams = false;
        }
        Edited();
    }

    private void ApplyCurl(ApiRequest parsed)
    {
        parsed.Name = _name;
        parsed.Extra = _extra;
        parsed.Tests = TestLines().ToList();
        if (parsed.Auth.Type == AuthTypes.None && Auth.Type.Value == AuthTypes.Inherit) parsed.Auth = new AuthSpec();
        Apply(parsed);
        Edited();
        _host.Notify(Loc.T("rv.curlImported"));
    }

    private void OnParamsChanged()
    {
        if (_syncingParams || _loading) { Edited(); return; }
        _syncingParams = true;
        Url = QueryString.WithParams(Url, Params.ToList());
        _syncingParams = false;
        Edited();
    }

    partial void OnBodyTypeChanged(Choice value)
    {
        // Choosing a raw type the first time pre-fills nothing, but keeps what was typed for another raw type.
        // A GraphQL body starts from an empty operation, so the editor is not a blank page.
        if (!_loading && value.Value == Core.Model.BodyTypes.GraphQL && BodyDocument.TextLength == 0) BodyDocument.Text = "query {\n  \n}";
        if (value.Value == Core.Model.BodyTypes.GraphQL) RefreshSchemaStatus();
        BodyTypeChangedForView?.Invoke();
        Edited();
        if (!_loading) Kind = ApiKinds.Of(BuildModel());
    }

    partial void OnDescriptionChanged(string value) => Edited();
    partial void OnBinaryPathChanged(string value) => Edited();

    /// <summary>Binary body: "…" picks the file; inside the collection it is kept relative, so the path works in every clone.</summary>
    [RelayCommand]
    private async Task PickBinaryAsync()
    {
        var path = await Views.Dialogs.PickFile(Loc.T("kv.pickFile"));
        if (path is null) return;
        var root = _host.Ws.ProjectRoot;
        BinaryPath = root.Length > 0 && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, path).Replace('\\', '/')
            : path;
    }
    partial void OnTestsTextChanged(string value) => Edited();

    private void Edited()
    {
        if (_loading) return;
        NotifyTabs();
        _dirtyTimer.Stop();
        _dirtyTimer.Start();
        _host.RequestEdited(this);
    }

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(ParamsTab));
        OnPropertyChanged(nameof(HeadersTab));
        OnPropertyChanged(nameof(AuthTab));
        OnPropertyChanged(nameof(BodyTab));
        OnPropertyChanged(nameof(TestsTab));
    }

    private void RecomputeDirty()
    {
        if (LoadError is not null) return;
        var model = BuildModel();
        IsDirty = Workspace.Serialize(model, RelPath) != _savedText;
        Kind = ApiKinds.Of(model);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = (IsDirty ? "● " : "") + _name;
        Badge = Look.Tag(Method, Kind);
        BadgeBrush = Look.TagBrush(Method, Kind);
        ToolTip = RelPath;
    }

    [RelayCommand]
    private void ShowSection(string section) => Section = section;

    [RelayCommand]
    private void ShowResponseSection(string section) => ResponseSection = section;

    public void FocusUrl() => FocusUrlRequested?.Invoke();

    // ------------------------------------------------------------ save / disk

    [RelayCommand]
    public Task<bool> SaveAsync()
    {
        _dirtyTimer.Stop();
        if (LoadError is not null) return Task.FromResult(false);
        var model = BuildModel();
        var text = Workspace.Serialize(model, RelPath);
        try
        {
            _host.MarkOwnWrite(RelPath);
            Workspace.WriteText(FullPath, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.Notify(Loc.F("rv.saveError", e.Message));
            return Task.FromResult(false);
        }
        _savedText = text;
        IsDirty = false;
        ChangedOnDisk = false;
        UpdateTitle();
        _host.RequestSaved(this);
        _host.Notify(Loc.F("rv.saved", RelPath, DateTime.Now));
        return Task.FromResult(true);
    }

    /// <summary>The file was renamed or moved by the app: follow it without touching the edits.</summary>
    public void Relocate(string newRel, string? newName = null)
    {
        RelPath = newRel;
        if (newName is not null)
        {
            _name = newName;
            // The saved text has the old name; the next comparison should see only real edits.
            try { _savedText = Workspace.Serialize(Workspace.ParseRequest(File.ReadAllText(FullPath), RelPath), RelPath); } catch { }
        }
        UpdateTitle();
        RecomputeDirty();
    }

    /// <summary>The watcher saw the file change. Our own saves and identical content are ignored.</summary>
    public void OnDiskChanged()
    {
        if (!File.Exists(FullPath)) return;
        string canonical;
        try { canonical = Workspace.Serialize(Workspace.ParseRequest(File.ReadAllText(FullPath), RelPath), RelPath); }
        catch { return; } // half-written or broken: wait for the next change
        if (canonical == _savedText) return;
        if (IsDirty) ChangedOnDisk = true;
        else Load();
    }

    [RelayCommand]
    private void ReloadFromDisk() => Load();

    [RelayCommand]
    private void KeepMine() => ChangedOnDisk = false;

    /// <summary>Asks before closing with unsaved edits. False = keep the tab open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty) return true;
        var choice = await Dialogs.Choose(Loc.T("common.unsaved"), Loc.F("rv.unsaved", _name), Loc.T("common.save"), Loc.T("common.discard"));
        return choice switch
        {
            0 => await SaveAsync(),
            1 => true,
            _ => false,
        };
    }

    // ------------------------------------------------------------ sending

    // ------------------------------------------------------------ GraphQL schema

    [ObservableProperty] private string _schemaStatus = "";
    [ObservableProperty] private bool _isLoadingSchema;

    /// <summary>The schema of this request's endpoint, if it was loaded (by this tab or another one).</summary>
    public GraphQLSchema? Schema
    {
        get
        {
            try { return _host.Schemas.GetValueOrDefault(Prepare().Url.Split('?')[0]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public void RefreshSchemaStatus() => SchemaStatus = Schema is { } s ? s.Summary + " · " + Loc.T("gql.schema.hint") : "";

    /// <summary>Introspection with this request's URL, headers and auth (POST, whatever the method is).</summary>
    [RelayCommand]
    private async Task LoadSchemaAsync()
    {
        if (IsLoadingSchema) return;
        var model = BuildModel();
        model.Method = "POST";
        model.Tests = new List<string>();
        model.Body = new BodySpec { Type = Core.Model.BodyTypes.GraphQL, Data = GraphQLSchema.IntrospectionQuery };
        PreparedRequest prepared;
        try { prepared = Runner.Prepare(_host.Ws, _host.Config, FolderRel, model, _host.CurrentEnvironment, _host.Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); return; }
        IsLoadingSchema = true;
        SchemaStatus = Loc.T("gql.schema.loading");
        try
        {
            var res = await _host.Engine.SendAsync(prepared, Options(model), CancellationToken.None);
            if (res.Error is not null) throw new FormatException(res.Error);
            var schema = await Task.Run(() => GraphQLSchema.Parse(res.Text));
            _host.Schemas[prepared.Url.Split('?')[0]] = schema;
            RefreshSchemaStatus();
            _host.Notify(schema.Summary);
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            SchemaStatus = Loc.F("gql.schema.failed", e.Message);
        }
        finally { IsLoadingSchema = false; }
    }

    public PreparedRequest Prepare(Func<string, string>? mask = null) =>
        Runner.Prepare(_host.Ws, _host.Config, FolderRel, BuildModel(), _host.CurrentEnvironment, _host.Runtime, mask);

    /// <summary>The URL with variables expanded (secrets masked) under the URL box.</summary>
    public void RefreshPreview()
    {
        try
        {
            var p = Prepare(_ => "••••");
            ResolvedUrl = p.Url;
            MissingText = p.Missing.Count > 0 ? Loc.F("rv.missing", string.Join(", ", p.Missing)) : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ResolvedUrl = Url; MissingText = ""; }
    }

    /// <summary>The collection's connection settings for this request, with their {{variables}} expanded.</summary>
    private HttpOptions Options(ApiRequest model) =>
        HttpOptions.For(_host.Config, model, Runner.Scope(_host.Ws, _host.Config, FolderRel, _host.CurrentEnvironment, _host.Runtime), _host.Ws.ProjectRoot);

    [RelayCommand]
    private async Task SendAsync()
    {
        // Ctrl+Enter on a WebSocket: connect, then send the message being written.
        if (IsWebSocket)
        {
            if (WsConnected) await SendMessageAsync(null);
            else await ConnectAsync();
            return;
        }
        if (IsSending || LoadError is not null) return;
        var model = BuildModel();
        PreparedRequest prepared;
        try { prepared = Runner.Prepare(_host.Ws, _host.Config, FolderRel, model, _host.CurrentEnvironment, _host.Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); return; }
        if (prepared.Url.Length == 0) { _host.Notify(Loc.T("rv.noUrl")); FocusUrl(); return; }

        var cts = _sendCts = new CancellationTokenSource();
        IsSending = true;
        IsEventStream = false;
        if (ResponseSection == "messages") ResponseSection = "body";
        _host.Notify(Loc.F("rv.sending", prepared.Method, prepared.Url));
        try
        {
            var response = await _host.Engine.SendAsync(prepared, Options(model), cts.Token, new LiveEvents(this));
            var results = response.Error is null ? Assertions.Evaluate(model.Tests, response, _host.Runtime) : new List<AssertionResult>();
            _host.AddHistory(new RunResult(RelPath, model, prepared, response, results, null));
            await ShowResponseAsync(response, results, prepared.Kind);
            if (results.Any(r => r.IsCapture && r.Passed)) _host.RuntimeChanged();
            _host.Notify(response.Error is null
                ? Loc.F("rv.received", response.Status, response.Reason, ResponseFormatter.Duration(response.ElapsedMs))
                : Loc.F("rv.failed", response.Error));
        }
        catch (OperationCanceledException) { _host.Notify(Loc.T("rv.cancelled")); }
        finally
        {
            if (_sendCts == cts) IsSending = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _sendCts?.Cancel();

    private async Task ShowResponseAsync(ApiResponse r, List<AssertionResult> results, ApiKind kind)
    {
        _lastResponse = r;
        Insights = ProtocolInsight.Describe(kind, r).Select(i => new InsightRow(i.Text, Look.Insight(i.Level))).ToList();
        ResponseUrl = r.FinalUrl;
        ResponseError = r.Error;
        StatusText = r.Error is not null ? Loc.T("rv.error") : $"{r.Status} {r.Reason}";
        StatusBrush = Look.StatusBadge(r.Status, r.Error is not null);
        TimeText = ResponseFormatter.Duration(r.ElapsedMs);
        SizeText = ResponseFormatter.Size(r.Size) + (r.Truncated ? " " + Loc.T("rv.truncated") : "");
        ResponseHeaders = r.Headers.Select(h => new HeaderRow(h.Key, h.Value)).ToList();
        ResponseCookies = r.HeaderValues("Set-Cookie").Select(ParseCookie).ToList();
        TestRows = results.Select(ToRow).ToList();
        int checks = results.Count(x => !x.IsCapture), passed = results.Count(x => !x.IsCapture && x.Passed);
        TestsSummary = checks == 0 ? "" : Loc.F("rv.testsSummary", passed, checks);
        TestsSummaryBrush = passed == checks ? Palette.Green : Palette.Red;
        OnPropertyChanged(nameof(ResponseHeadersTab));
        OnPropertyChanged(nameof(ResponseCookiesTab));
        OnPropertyChanged(nameof(ResponseTestsTab));
        await RenderBodyAsync();
        HasResponse = true;
        if (checks > 0 && passed < checks && ResponseSection == "body") ResponseSection = "tests";
    }

    /// <summary>Formats the body off the UI thread (a few MB of JSON take a moment to re-indent).</summary>
    private async Task RenderBodyAsync()
    {
        var r = _lastResponse;
        if (r is null) return;
        ResponseImage?.Dispose();
        ResponseImage = null;
        if (r.Error is not null)
        {
            ResponseNote = "";
            ResponseDocument.Text = "";
            ResponseChanged?.Invoke();
            return;
        }
        bool pretty = _host.PrettyResponses;
        var formatted = await Task.Run(() => ResponseFormatter.Format(r, pretty));
        ResponseExtension = formatted.Extension;
        ResponseNote = formatted.Kind switch
        {
            BodyKind.Empty => Loc.T("rv.emptyBody"),
            BodyKind.Binary => Loc.F("rv.binaryBody", ResponseFormatter.Size(r.Size), r.ContentType ?? "?"),
            _ => "",
        };
        if (formatted.Kind == BodyKind.Image)
        {
            try { ResponseImage = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(r.Body)); }
            catch { ResponseNote = Loc.F("rv.binaryBody", ResponseFormatter.Size(r.Size), r.ContentType ?? "?"); }
        }
        ResponseDocument.Text = formatted.Text;
        ResponseChanged?.Invoke();
    }

    public void RefreshResponseFormat() => _ = RenderBodyAsync();

    private static TestRow ToRow(AssertionResult a)
    {
        if (a.IsCapture)
            return a.Passed
                ? new TestRow("=", Palette.Teal, a.Line, Loc.F("rv.captured", a.CapturedName, Short(a.Actual ?? "")))
                : new TestRow("✗", Palette.Red, a.Line, a.Error ?? "");
        if (a.Error is not null) return new TestRow("!", Palette.Red, a.Line, a.Error);
        return a.Passed
            ? new TestRow("✓", Palette.Green, a.Line, "")
            : new TestRow("✗", Palette.Red, a.Line, Loc.F("rv.got", a.Actual ?? Loc.T("rv.nothing")));
    }

    private static string Short(string s) => s.Length <= 80 ? s : s[..77] + "...";

    private static CookieRow ParseCookie(string header)
    {
        var parts = header.Split(';', 2);
        var nv = parts[0];
        var eq = nv.IndexOf('=');
        return new CookieRow(eq < 0 ? nv.Trim() : nv[..eq].Trim(), eq < 0 ? "" : nv[(eq + 1)..].Trim(), parts.Length > 1 ? parts[1].Trim() : "");
    }

    // ------------------------------------------------------------ WebSocket

    private const int MaxLogRows = 5000;

    /// <summary>Opens the connection. The log keeps what earlier connections said (Clear empties it).</summary>
    private async Task ConnectAsync()
    {
        if (IsSending || WsConnected || LoadError is not null) return;
        var model = BuildModel();
        PreparedRequest prepared;
        try { prepared = Runner.Prepare(_host.Ws, _host.Config, FolderRel, model, _host.CurrentEnvironment, _host.Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); return; }
        if (prepared.Url.Length == 0) { _host.Notify(Loc.T("rv.noUrl")); FocusUrl(); return; }

        var cts = _sendCts = new CancellationTokenSource();
        IsSending = true;
        _host.Notify(Loc.F("ws.connecting", prepared.Url));
        try
        {
            var (session, handshake) = await WebSocketSession.ConnectAsync(prepared, Options(model), _host.Engine, cts.Token);
            ShowHandshake(handshake, prepared.Url);
            if (session is null)
            {
                Log(WsDirection.Error, handshake.Status > 0 ? Loc.F("ws.refused", handshake.Status, handshake.Error) : handshake.Error ?? "");
                _host.Notify(Loc.F("rv.failed", handshake.Error));
                return;
            }
            _session = session;
            session.Event += e => Dispatcher.UIThread.Post(() => OnWsEvent(session, e));
            session.Closed += text => Dispatcher.UIThread.Post(() => OnWsClosed(session, text));
            WsConnected = true;
            Log(WsDirection.Info, Loc.F("ws.connected", prepared.Url) + (handshake.SubProtocol is { Length: > 0 } sp ? " · " + sp : ""));
            session.Start();
            _host.Notify(Loc.F("ws.connectedIn", ResponseFormatter.Duration(handshake.ElapsedMs)));
        }
        catch (OperationCanceledException) { _host.Notify(Loc.T("rv.cancelled")); }
        finally
        {
            if (_sendCts == cts) IsSending = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (_session is not { } session) return;
        Log(WsDirection.Info, Loc.T("ws.closing"));
        await session.CloseAsync();
    }

    /// <summary>Sends <paramref name="text"/> (by default, the message being written) with its variables expanded.</summary>
    [RelayCommand]
    private async Task SendMessageAsync(string? text)
    {
        text ??= BodyDocument.Text;
        if (_session is not { IsOpen: true } session) { _host.Notify(Loc.T("ws.notConnected")); return; }
        if (text.Length == 0) { _host.Notify(Loc.T("ws.emptyMessage")); return; }
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var scope = Runner.Scope(_host.Ws, _host.Config, FolderRel, _host.CurrentEnvironment, _host.Runtime);
            text = Interpolator.Expand(text, scope, missing);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (missing.Count > 0) _host.Notify(Loc.F("rv.missing", string.Join(", ", missing)));
        try { await session.SendAsync(text, CancellationToken.None); }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log(WsDirection.Error, e.Message);
        }
    }

    /// <summary>A saved message, sent right away (double-click in the list).</summary>
    public Task SendSavedAsync(WsMessage? m) => m is null ? Task.CompletedTask : SendMessageAsync(m.Data);

    partial void OnSelectedSavedMessageChanged(WsMessage? value)
    {
        // Picking a saved message puts it in the editor, ready to change or send.
        if (value is null || BodyDocument.Text == value.Data) return;
        BodyDocument.Text = value.Data;
        BodyType = LooksLikeJson(value.Data) ? MessageTypes[0] : MessageTypes[1];
    }

    private static bool LooksLikeJson(string s) =>
        s.TrimStart() is { Length: > 0 } t && (t[0] == '{' || t[0] == '[') && Core.Text.CanonicalJson.Pretty(s) is not null;

    /// <summary>Keeps the message being written in the request (named; a name already used is replaced).</summary>
    [RelayCommand]
    private async Task SaveMessageAsync()
    {
        var data = BodyDocument.Text;
        if (data.Trim().Length == 0) { _host.Notify(Loc.T("ws.emptyMessage")); return; }
        var name = await Dialogs.Prompt(Loc.T("ws.saveMessage"), Loc.T("ws.messageName"), SuggestName(data));
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var saved = new WsMessage { Name = name, Data = data };
        int i = SavedMessages.ToList().FindIndex(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) SavedMessages[i] = saved;
        else SavedMessages.Add(saved);
        SelectedSavedMessage = saved;
        Edited();
    }

    [RelayCommand]
    private void RemoveMessage()
    {
        if (SelectedSavedMessage is not { } m || !SavedMessages.Remove(m)) return;
        Edited();
    }

    /// <summary>{"type": "join", …} → "join": chat-like protocols name their messages in a field.</summary>
    private static string SuggestName(string data)
    {
        var m = Regex.Match(data, @"""(?:type|action|event|op|method|cmd)""\s*:\s*""([^""]{1,40})""");
        if (m.Success) return m.Groups[1].Value;
        var oneLine = Regex.Replace(data.Trim(), @"\s+", " ");
        return oneLine.Length <= 30 ? oneLine : oneLine[..30];
    }

    [RelayCommand]
    private void ClearLog()
    {
        WsLog.Clear();
        SelectedWsRow = null;
        OnPropertyChanged(nameof(WsLogTab));
        OnPropertyChanged(nameof(HasLog));
    }

    private void ShowHandshake(WsHandshake h, string url)
    {
        _lastResponse = null;
        ResponseImage?.Dispose();
        ResponseImage = null;
        ResponseUrl = url;
        ResponseError = null;
        Insights = h.Ok ? Array.Empty<InsightRow>()
            : new[] { new InsightRow(h.Status > 0 ? Loc.F("ws.refused", h.Status, h.Error) : h.Error ?? "", Palette.Red) };
        StatusText = h.Ok ? Loc.T("ws.open") : h.Status > 0 ? h.Status.ToString() : Loc.T("rv.error");
        StatusBrush = h.Ok ? Palette.OkBadge : Look.StatusBadge(h.Status, h.Status == 0);
        TimeText = ResponseFormatter.Duration(h.ElapsedMs);
        SizeText = "";
        ResponseHeaders = h.Headers.Select(x => new HeaderRow(x.Key, x.Value)).ToList();
        ResponseCookies = h.Headers.Where(x => x.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)).Select(x => ParseCookie(x.Value)).ToList();
        TestRows = Array.Empty<TestRow>();
        TestsSummary = "";
        OnPropertyChanged(nameof(ResponseHeadersTab));
        OnPropertyChanged(nameof(ResponseCookiesTab));
        HasResponse = true;
        ResponseSection = "messages";
    }

    private void OnWsEvent(WebSocketSession session, WsEvent e)
    {
        var size = !e.IsMessage ? "" : e.Binary ? Loc.F("ws.binary", ResponseFormatter.Size(e.Size)) : ResponseFormatter.Size(e.Size);
        Log(e.Direction, e.Text, e.At, size);
        if (session == _session) SizeText = $"↑ {session.Sent}  ↓ {session.Received}";
    }

    private void OnWsClosed(WebSocketSession session, string text)
    {
        bool abnormal = RunResult.AbnormalClose(text);
        var line = Loc.F("ws.closed", text, ResponseFormatter.Duration(session.Uptime.TotalMilliseconds));
        Log(abnormal ? WsDirection.Error : WsDirection.Info, line);
        if (session != _session) return;
        _session = null;
        WsConnected = false;
        StatusText = Loc.F("ws.closedBadge", text);
        StatusBrush = abnormal ? Palette.ServerErrorBadge : Palette.FileBg;
        _host.Notify(line);
        _ = session.DisposeAsync().AsTask();
    }

    /// <summary>Hands the events of a stream to the log as they arrive (the engine reads on a background thread).</summary>
    private sealed class LiveEvents(RequestTab tab) : IStreamListener
    {
        private readonly SseParser _parser = new();

        public void Started(ApiResponse head) => Dispatcher.UIThread.Post(() => tab.StreamStarted(head));

        public void Data(byte[] chunk)
        {
            var events = _parser.Feed(chunk);
            if (events.Count > 0) Dispatcher.UIThread.Post(() => tab.StreamEvents(events));
        }
    }

    private void StreamStarted(ApiResponse head)
    {
        ClearLog();
        IsEventStream = true;
        ResponseUrl = head.FinalUrl;
        ResponseError = null;
        Insights = Array.Empty<InsightRow>();
        StatusText = $"{head.Status} {head.Reason}";
        StatusBrush = Look.StatusBadge(head.Status, false);
        TimeText = ResponseFormatter.Duration(head.ElapsedMs);
        SizeText = "";
        ResponseHeaders = head.Headers.Select(h => new HeaderRow(h.Key, h.Value)).ToList();
        OnPropertyChanged(nameof(ResponseHeadersTab));
        ResponseSection = "messages";
        Log(WsDirection.Info, Loc.T("sse.started"));
    }

    private void StreamEvents(List<SseEvent> events)
    {
        foreach (var e in events)
            Log(WsDirection.Received, e.Data, size: ResponseFormatter.Size(System.Text.Encoding.UTF8.GetByteCount(e.Data)), label: e.Event == "message" ? null : e.Event);
    }

    private void Log(WsDirection d, string text, DateTimeOffset? at = null, string size = "", string? label = null)
    {
        var (glyph, brush) = d switch
        {
            WsDirection.Sent => ("↑", Palette.Blue),
            WsDirection.Received => ("↓", Palette.Green),
            WsDirection.Error => ("!", Palette.Red),
            _ => ("•", Palette.Dim),
        };
        var preview = Regex.Replace(text.Length > 600 ? text[..600] : text, @"\s+", " ").Trim();
        if (label is not null) preview = label + " · " + preview;
        var row = new WsLogRow((at ?? DateTimeOffset.Now).ToString("HH:mm:ss.fff"), glyph, brush, preview, text, size, d is WsDirection.Sent or WsDirection.Received);
        // A chatty socket (an event feed) can send thousands: the oldest lines go.
        if (WsLog.Count >= MaxLogRows) WsLog.RemoveAt(0);
        WsLog.Add(row);
        OnPropertyChanged(nameof(WsLogTab));
        OnPropertyChanged(nameof(HasLog));
        HasResponse = true;
    }

    partial void OnSelectedWsRowChanged(WsLogRow? value)
    {
        var text = value?.Text ?? "";
        var json = value is { IsMessage: true } && LooksLikeJson(text);
        MessageExtension = json ? ".json" : ".txt";
        MessageDocument.Text = json && _host.PrettyResponses ? Core.Text.CanonicalJson.Pretty(text) ?? text : text;
        MessageChanged?.Invoke();
    }

    // ------------------------------------------------------------ clipboard and files

    [RelayCommand]
    private async Task CopyCurlAsync()
    {
        if (Dialogs.Owner?.Clipboard is not { } cb) return;
        if (IsWebSocket) { _host.Notify(Loc.T("ws.noCurl")); return; }
        var p = Prepare();
        await cb.SetTextAsync(Curl.Write(p));
        _host.Notify(Loc.T("rv.curlCopied"));
    }

    /// <summary>The request as code in <paramref name="language"/>, with the real values (the panel shows them masked).</summary>
    public async Task CopyCodeAsync(Choice language)
    {
        if (Dialogs.Owner?.Clipboard is not { } cb) return;
        if (IsWebSocket) { _host.Notify(Loc.T("ws.noCurl")); return; }
        await cb.SetTextAsync(CodeGen.Generate(language.Value, Prepare()));
        _host.Notify(Loc.F("rv.codeCopied", language.Label));
    }

    [RelayCommand]
    private async Task CopyResponseAsync()
    {
        if (IsWebSocket)
        {
            if (Dialogs.Owner?.Clipboard is not { } clip || WsLog.Count == 0) return;
            await clip.SetTextAsync(string.Join("\n", WsLog.Select(r => $"{r.Time} {r.Glyph} {r.Text}")));
            _host.Notify(Loc.T("ws.logCopied"));
            return;
        }
        if (Dialogs.Owner?.Clipboard is not { } cb || _lastResponse is null) return;
        await cb.SetTextAsync(ResponseDocument.Text.Length > 0 ? ResponseDocument.Text : _lastResponse.Text);
        _host.Notify(Loc.T("rv.responseCopied"));
    }

    public async Task CopyTextAsync(string text, string what)
    {
        if (Dialogs.Owner?.Clipboard is not { } cb || text.Length == 0) return;
        await cb.SetTextAsync(text);
        _host.Notify(Loc.F("rv.copiedWhat", what));
    }

    [RelayCommand]
    private async Task SaveResponseAsync()
    {
        if (_lastResponse is not { Error: null } r) return;
        var ext = ResponseFormatter.Detect(r.Body, r.ContentType) switch
        {
            BodyKind.Json => ".json",
            BodyKind.Xml => ".xml",
            BodyKind.Html => ".html",
            BodyKind.Image => "." + (r.ContentType?.Split('/').ElementAtOrDefault(1)?.Split(';')[0] ?? "bin"),
            BodyKind.Text => ".txt",
            _ => ".bin",
        };
        var path = await Dialogs.SaveFile(Loc.T("rv.saveResponse"), Workspace.SafeFileName(_name) + ext);
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, r.Body);
            _host.Notify(Loc.F("rv.responseSaved", path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); }
    }

    [RelayCommand]
    private Task OpenInTextPeachAsync() => _host.OpenInTextPeachAsync(RelPath);

    [RelayCommand]
    private void RevealInTree() => _host.Reveal(RelPath);

    /// <summary>Formats the JSON body in place (the body editor's "{ }" button).</summary>
    [RelayCommand]
    private void FormatBody()
    {
        if (IsGraphQLBody)
        {
            // The query is left as written; the variables are JSON.
            if (VariablesDocument.TextLength == 0) return;
            if (Core.Text.CanonicalJson.Pretty(VariablesDocument.Text) is { } vars) VariablesDocument.Text = vars;
            else _host.Notify(Loc.T("rv.formatFailed"));
            return;
        }
        var text = BodyType.Value switch
        {
            Core.Model.BodyTypes.Json => Core.Text.CanonicalJson.Pretty(BodyDocument.Text),
            Core.Model.BodyTypes.Xml => ResponseFormatter.PrettyXml(BodyDocument.Text),
            _ => null,
        };
        if (text is null) { _host.Notify(Loc.T("rv.formatFailed")); return; }
        BodyDocument.Text = text;
    }

    public void Dispose()
    {
        _sendCts?.Cancel();
        if (_session is { } session) { _session = null; _ = session.DisposeAsync().AsTask(); }
        _dirtyTimer.Stop();
        ResponseImage?.Dispose();
    }
}
