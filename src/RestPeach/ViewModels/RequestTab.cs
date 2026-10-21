using System.Collections.ObjectModel;
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
    Task OpenInTextPeachAsync(string? relPath);
    void Reveal(string relPath);
}

public sealed record HeaderRow(string Key, string Value);

public sealed record CookieRow(string Name, string Value, string Attributes);

public sealed record TestRow(string Glyph, IBrush Brush, string Line, string Detail)
{
    public bool HasDetail => Detail.Length > 0;
}

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
        new Choice(Core.Model.BodyTypes.Text, Loc.T("body.text")),
        new Choice(Core.Model.BodyTypes.Xml, "XML"),
        new Choice(Core.Model.BodyTypes.Html, "HTML"),
        new Choice(Core.Model.BodyTypes.FormData, "Form-data"),
        new Choice(Core.Model.BodyTypes.UrlEncoded, "x-www-form-urlencoded"),
    };

    public KeyValueTable Params { get; } = new() { KeyWatermark = Loc.T("kv.param") };
    public KeyValueTable Headers { get; } = new() { KeyWatermark = Loc.T("kv.header"), Suggestions = KeyValueTable.CommonHeaders };
    public KeyValueTable BodyFields { get; } = new() { ShowType = true, KeyWatermark = Loc.T("kv.field") };
    public AuthEditor Auth { get; } = new(canInherit: true);
    public TextDocument BodyDocument { get; } = new();
    public TextDocument ResponseDocument { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MethodBrush))]
    private string _method = "GET";
    [ObservableProperty] private string _url = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRawBody), nameof(IsFormBody), nameof(IsNoBody), nameof(BodyExtension))]
    private Choice _bodyType;
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
    [NotifyPropertyChangedFor(nameof(CanSend))]
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
    [NotifyPropertyChangedFor(nameof(IsBodyResponse), nameof(IsHeadersResponse), nameof(IsCookiesResponse), nameof(IsTestsResponse))]
    private string _responseSection = "body";
    [ObservableProperty] private string _responseUrl = "";

    private ApiResponse? _lastResponse;

    /// <summary>Raised when the view should put the caret in the URL box (Ctrl+L, new request).</summary>
    public event Action? FocusUrlRequested;
    /// <summary>The view swaps the highlighting of the response editor when the body kind changes.</summary>
    public event Action? ResponseChanged;
    public event Action? BodyTypeChangedForView;

    public RequestTab(ICollectionHost host, string relPath)
    {
        _host = host;
        RelPath = relPath;
        _bodyType = BodyTypes[0];
        BodyFields.FileBase = host.Ws.ProjectRoot;
        ToolTip = relPath;

        _dirtyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _dirtyTimer.Tick += (_, _) => { _dirtyTimer.Stop(); RecomputeDirty(); };

        Params.Changed += OnParamsChanged;
        Headers.Changed += Edited;
        BodyFields.Changed += Edited;
        Auth.Changed += Edited;
        BodyDocument.TextChanged += (_, _) => Edited();
        Load();
    }

    public IBrush MethodBrush => Look.Method(Method);
    public bool IsRawBody => Core.Model.BodyTypes.IsRaw(BodyType.Value);
    public bool IsFormBody => Core.Model.BodyTypes.IsForm(BodyType.Value);
    public bool IsNoBody => BodyType.Value == Core.Model.BodyTypes.None;
    public string BodyExtension => BodyType.Value switch
    {
        Core.Model.BodyTypes.Json => ".json",
        Core.Model.BodyTypes.Xml => ".xml",
        Core.Model.BodyTypes.Html => ".html",
        _ => ".txt",
    };
    public bool CanSend => !IsSending;

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

    public string ParamsTab => Count(Loc.T("rv.params"), Params.ActiveCount);
    public string HeadersTab => Count(Loc.T("rv.headers"), Headers.ActiveCount);
    public string AuthTab => Auth.Summary.Length > 0 ? Loc.T("rv.auth") + " ●" : Loc.T("rv.auth");
    public string BodyTab => IsNoBody ? Loc.T("rv.body") : Loc.T("rv.body") + " ●";
    public string TestsTab => Count(Loc.T("rv.tests"), TestLines().Count(l => !Assertions.IsComment(l)));
    public string ResponseHeadersTab => Count(Loc.T("rv.headers"), ResponseHeaders.Count);
    public string ResponseCookiesTab => Count(Loc.T("rv.cookies"), ResponseCookies.Count);
    public string ResponseTestsTab => Count(Loc.T("rv.tests"), TestRows.Count);

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
        Section = r.Body.Type != Core.Model.BodyTypes.None ? "body" : "params";
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
            BodyDocument.Text = r.Body.Data;
            BodyFields.Load(r.Body.Fields);
            TestsText = string.Join("\n", r.Tests);
        }
        finally
        {
            _loading = false;
            _syncingParams = false;
        }
        NotifyTabs();
        _host.RequestEdited(this);
    }

    private IEnumerable<string> TestLines() => TestsText.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0);

    /// <summary>The request as it is on screen right now.</summary>
    public ApiRequest BuildModel()
    {
        var all = Params.ToList();
        return new ApiRequest
        {
            Name = _name,
            Method = Method.Trim().ToUpperInvariant(),
            Url = Url,
            Description = Description,
            Headers = Headers.ToList(),
            DisabledParams = all.Where(p => !p.Active).ToList(),
            Auth = Auth.ToModel(),
            Body = new BodySpec { Type = BodyType.Value, Data = BodyDocument.Text, Fields = BodyFields.ToList() },
            Tests = TestLines().ToList(),
            Extra = (JsonObject)_extra.DeepClone(),
        };
    }

    // ------------------------------------------------------------ editing

    partial void OnMethodChanged(string value)
    {
        Badge = Look.MethodTag(value);
        BadgeBrush = Look.Method(value);
        Edited();
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
        BodyTypeChangedForView?.Invoke();
        Edited();
    }

    partial void OnDescriptionChanged(string value) => Edited();
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
        IsDirty = Workspace.Serialize(BuildModel(), RelPath) != _savedText;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = (IsDirty ? "● " : "") + _name;
        Badge = Look.MethodTag(Method);
        BadgeBrush = Look.Method(Method);
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

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsSending || LoadError is not null) return;
        var model = BuildModel();
        PreparedRequest prepared;
        try { prepared = Runner.Prepare(_host.Ws, _host.Config, FolderRel, model, _host.CurrentEnvironment, _host.Runtime); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _host.Notify(e.Message); return; }
        if (prepared.Url.Length == 0) { _host.Notify(Loc.T("rv.noUrl")); FocusUrl(); return; }

        var cts = _sendCts = new CancellationTokenSource();
        IsSending = true;
        _host.Notify(Loc.F("rv.sending", prepared.Method, prepared.Url));
        try
        {
            var response = await _host.Engine.SendAsync(prepared, HttpOptions.From(_host.Config), cts.Token);
            var results = response.Error is null ? Assertions.Evaluate(model.Tests, response, _host.Runtime) : new List<AssertionResult>();
            await ShowResponseAsync(response, results);
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

    private async Task ShowResponseAsync(ApiResponse r, List<AssertionResult> results)
    {
        _lastResponse = r;
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

    // ------------------------------------------------------------ clipboard and files

    [RelayCommand]
    private async Task CopyCurlAsync()
    {
        if (Dialogs.Owner?.Clipboard is not { } cb) return;
        var p = Prepare();
        await cb.SetTextAsync(Curl.Write(p));
        _host.Notify(Loc.T("rv.curlCopied"));
    }

    [RelayCommand]
    private async Task CopyResponseAsync()
    {
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
        _dirtyTimer.Stop();
        ResponseImage?.Dispose();
    }
}
