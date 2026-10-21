using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Controls;
using RestPeach.Core;
using RestPeach.Core.Formats;
using RestPeach.Core.Http;
using RestPeach.Core.Model;
using RestPeach.Core.Storage;

namespace RestPeach.ViewModels;

/// <summary>One line of a run: a request and how it went.</summary>
public sealed class RunRow
{
    public required RunResult Result { get; init; }
    /// <summary>Secret values of the environment: the detail shows them as •••• (copying as cURL keeps them).</summary>
    public IReadOnlyList<string> Secrets { get; init; } = Array.Empty<string>();
    public string RelPath => Result.RelPath;
    public string Name => Workspace.Join(Workspace.ParentOf(RelPath), Result.Name);
    public string Glyph => Result.Passed ? "✓" : "✗";
    public IBrush GlyphBrush => Result.Passed ? Palette.Green : Palette.Red;
    private Core.Model.ApiKind Kind => Result.Sent?.Kind ?? (Result.Request is { } r ? Core.Model.ApiKinds.Of(r) : Core.Model.ApiKind.Rest);
    public string Method => Look.Tag(Result.Sent?.Method ?? Result.Request?.Method ?? "", Kind);
    public IBrush MethodBrush => Look.TagBrush(Result.Sent?.Method ?? Result.Request?.Method ?? "", Kind);
    public string Status => Result.Error is not null ? Loc.T("run.notSent")
        : Result.Response?.Error is not null ? Loc.T("rv.error")
        : $"{Result.Response?.Status} {Result.Response?.Reason}";
    public IBrush StatusBrush => Look.StatusText(Result.Response?.Status ?? 0, Result.Error is not null || Result.Response?.Error is not null);
    public string Time => Result.Response is { } r ? ResponseFormatter.Duration(r.ElapsedMs) : "";
    public string Checks
    {
        get
        {
            var checks = Result.Assertions.Where(a => !a.IsCapture).ToList();
            return checks.Count == 0 ? "" : $"{checks.Count(a => a.Passed)}/{checks.Count}";
        }
    }

    public bool HasResponse => Result.Response is { Error: null };
    public bool WasSent => Result.Sent is not null;
    public IBrush StatusBadgeBrush => Look.StatusBadge(Result.Response?.Status ?? 0, Result.Error is not null || Result.Response?.Error is not null);
    public string Size => Result.Response is { Error: null } r ? ResponseFormatter.Size(r.Size) + (r.Truncated ? " " + Loc.T("rv.truncated") : "") : "";
    /// <summary>Why nothing came back (not sent, or the connection failed).</summary>
    public string? Failure => Result.Error ?? Result.Response?.Error;
    public bool HasFailure => Failure is not null;

    public IReadOnlyList<HeaderRow> ResponseHeaders =>
        Result.Response?.Headers.Select(h => new HeaderRow(h.Key, h.Value)).ToList() ?? (IReadOnlyList<HeaderRow>)Array.Empty<HeaderRow>();

    public string SentLine => Result.Sent is { } p ? p.Method + " " + Mask(p.Url) : "";
    public string SentMissing => Result.Sent is { Missing.Count: > 0 } p ? Loc.F("rv.missing", string.Join(", ", p.Missing)) : "";

    /// <summary>The headers that went out, including the ones the engine adds itself (User-Agent, multipart's Content-Type).</summary>
    public IReadOnlyList<HeaderRow> SentHeaders
    {
        get
        {
            if (Result.Sent is not { } p) return Array.Empty<HeaderRow>();
            var rows = p.Headers.Where(h => !(p.BodyType == BodyTypes.FormData && h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                .Select(h => new HeaderRow(h.Key, MaskHeader(h.Key, h.Value))).ToList();
            if (p.BodyType == BodyTypes.FormData) rows.Add(new HeaderRow("Content-Type", Loc.T("run.multipart")));
            if (!p.HasHeader("User-Agent")) rows.Add(new HeaderRow("User-Agent", HttpEngine.UserAgent + "  (" + Loc.T("run.addedOnSend") + ")"));
            return rows;
        }
    }

    /// <summary>The body as sent: raw text, the url-encoded string, or one line per multipart field.</summary>
    public string SentBody => Result.Sent is not { } p ? "" : Mask(p.BodyType switch
    {
        BodyTypes.None => "",
        BodyTypes.UrlEncoded => HttpEngine.FormEncode(p.Fields),
        BodyTypes.FormData => string.Join('\n', p.Fields.Select(f => f.Key + ": " + (f.IsFile ? "@" + f.Value : f.Value))),
        _ => p.RawBody,
    });

    private string Mask(string text)
    {
        foreach (var secret in Secrets)
        {
            text = text.Replace(secret, "••••", StringComparison.Ordinal);
            var escaped = Uri.EscapeDataString(secret);
            if (escaped != secret) text = text.Replace(escaped, "••••", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>Basic auth travels in base64, where the secret cannot be found as text.</summary>
    private string MaskHeader(string name, string value)
    {
        if (Secrets.Count > 0 && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var pair = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value[6..].Trim()));
                if (Mask(pair) != pair) return "Basic ••••";
            }
            catch (FormatException) { }
        }
        return Mask(value);
    }

    public string SentBodyExtension => Result.Sent?.BodyType switch
    {
        BodyTypes.Json => ".json",
        BodyTypes.Xml => ".xml",
        BodyTypes.Html => ".html",
        _ => ".txt",
    };

    public string Curl => Result.Sent is { } p ? Core.Formats.Curl.Write(p) : "";

    public string HeadersTab => Count(Loc.T("rv.headers"), Result.Response?.Headers.Count ?? 0);
    public string TestsTab => Count(Loc.T("rv.tests"), Result.Assertions.Count);
    private static string Count(string label, int n) => n > 0 ? $"{label} ({n})" : label;

    public IReadOnlyList<TestRow> Details
    {
        get
        {
            var rows = new List<TestRow>();
            if (Result.Error is not null) rows.Add(new TestRow("!", Palette.Red, Result.Error, ""));
            if (Result.Response?.Error is { } e) rows.Add(new TestRow("!", Palette.Red, e, Result.Sent?.Url ?? ""));
            if (Result.Response is { } response)
                foreach (var i in ProtocolInsight.Describe(Kind, response)) rows.Add(new TestRow("»", Look.Insight(i.Level), i.Text, ""));
            foreach (var a in Result.Assertions)
            {
                rows.Add(a.IsCapture
                    ? new TestRow(a.Passed ? "=" : "✗", a.Passed ? Palette.Teal : Palette.Red, a.Line, a.Passed ? Loc.F("rv.captured", a.CapturedName, a.Actual) : a.Error ?? "")
                    : a.Error is not null ? new TestRow("!", Palette.Red, a.Line, a.Error)
                    : new TestRow(a.Passed ? "✓" : "✗", a.Passed ? Palette.Green : Palette.Red, a.Line, a.Passed ? "" : Loc.F("rv.got", a.Actual ?? Loc.T("rv.nothing"))));
            }
            if (Result.Assertions.Count == 0 && Result.Error is null && Result.Response is { Error: null } r)
                rows.Add(new TestRow(Result.Passed ? "✓" : "✗", GlyphBrush, Result.Passed ? Loc.T("run.noTestsOk") : Loc.T("run.noTestsFail"), Result.Sent?.Url ?? ""));
            return rows;
        }
    }
}

/// <summary>
/// Runs a folder (or the whole collection) in order, the same way the CLI does, and lists the results. Captured
/// variables are shared with the session, so a run that logs in leaves the token available to the other tabs.
/// </summary>
public sealed partial class RunTab : TabViewModel
{
    private readonly ICollectionHost _host;
    private readonly Action<string> _open;
    private CancellationTokenSource? _cts;

    public string RelDir { get; }
    public ObservableCollection<RunRow> Rows { get; } = new();

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private IBrush _summaryBrush = Palette.Dim;
    [ObservableProperty] private RunRow? _selected;
    [ObservableProperty] private bool _bail;

    /// <summary>What the detail of the selected result shows: tests, body, headers or sent. Kept across selections, so
    /// going down the list compares the same thing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTestsDetail), nameof(IsBodyDetail), nameof(IsHeadersDetail), nameof(IsSentDetail))]
    private string _detail = "body";

    [ObservableProperty] private string _responseExtension = ".txt";
    [ObservableProperty] private string _responseNote = "";
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _responseImage;

    public TextDocument ResponseDocument { get; } = new();
    public TextDocument SentDocument { get; } = new();

    /// <summary>The documents changed: the view resets grammars and scroll.</summary>
    public event Action? DetailChanged;
    private int _renderVersion;

    public bool IsTestsDetail => Detail == "tests";
    public bool IsBodyDetail => Detail == "body";
    public bool IsHeadersDetail => Detail == "headers";
    public bool IsSentDetail => Detail == "sent";

    public string Heading => RelDir.Length == 0 ? Loc.T("run.collection") : Loc.F("run.folder", RelDir);
    public string CliHint => "restpeach-cli run " + (RelDir.Length == 0 ? "." : ".api/" + RelDir) + (_host.CurrentEnvironment is { } e ? " --env " + e.Stem : "");

    public RunTab(ICollectionHost host, string relDir, Action<string> open)
    {
        _host = host;
        _open = open;
        RelDir = relDir;
        Badge = "RUN";
        BadgeBrush = Palette.Teal;
        Title = relDir.Length == 0 ? Loc.T("run.tab.collection") : relDir[(relDir.LastIndexOf('/') + 1)..];
        ToolTip = Heading;
    }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsRunning) return;
        Rows.Clear();
        Selected = null;
        var requests = _host.Ws.ListRequests(RelDir);
        if (requests.Count == 0) { Summary = Loc.T("run.empty"); return; }
        var cts = _cts = new CancellationTokenSource();
        IsRunning = true;
        OnPropertyChanged(nameof(CliHint));
        var secrets = (_host.CurrentEnvironment?.Secrets.Values ?? Enumerable.Empty<string>())
            .Where(v => v.Length >= 3).Distinct().OrderByDescending(v => v.Length).ToList();
        var runner = new Runner(_host.Ws, _host.Engine, _host.CurrentEnvironment?.Stem, _host.Runtime);
        var sw = Stopwatch.StartNew();
        int passed = 0, failed = 0;
        try
        {
            await foreach (var r in runner.RunAllAsync(requests, Bail, cts.Token))
            {
                Rows.Add(new RunRow { Result = r, Secrets = secrets });
                if (r.Passed) passed++; else failed++;
                Summary = Loc.F("run.progress", Rows.Count, requests.Count);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsRunning = false;
            _host.RuntimeChanged();
        }
        var total = passed + failed;
        Summary = Loc.Plural("cli.requests", total) + " · " + Loc.Plural("cli.passed", passed)
                  + (failed > 0 ? " · " + Loc.Plural("cli.failed", failed) : "") + " · " + ResponseFormatter.Duration(sw.Elapsed.TotalMilliseconds)
                  + (cts.IsCancellationRequested ? " · " + Loc.T("rv.cancelled") : "");
        SummaryBrush = failed == 0 && !cts.IsCancellationRequested ? Palette.Green : Palette.Red;
        Selected = Rows.FirstOrDefault(r => !r.Result.Passed) ?? Rows.FirstOrDefault();
        // A failure is explained by its tests (or its error, listed there too).
        if (Selected is { Result.Passed: false } && Detail == "body" && (Selected.Result.Assertions.Any(a => !a.Passed) || Selected.Failure is not null)) Detail = "tests";
    }

    [RelayCommand]
    private void ShowDetail(string detail) => Detail = detail;

    partial void OnSelectedChanged(RunRow? value) => _ = RenderAsync(value);

    /// <summary>Formats the body off the UI thread, like the request tab; a newer selection wins.</summary>
    private async Task RenderAsync(RunRow? row)
    {
        var version = ++_renderVersion;
        ResponseImage?.Dispose();
        ResponseImage = null;
        ResponseNote = "";
        SentDocument.Text = row?.SentBody ?? "";
        if (row?.Result.Response is not { Error: null } r)
        {
            ResponseDocument.Text = "";
            DetailChanged?.Invoke();
            return;
        }
        bool pretty = _host.PrettyResponses;
        var formatted = await Task.Run(() => ResponseFormatter.Format(r, pretty));
        if (version != _renderVersion) return;
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
        DetailChanged?.Invoke();
    }

    [RelayCommand]
    private async Task CopyCurlAsync()
    {
        if (Selected is not { WasSent: true } s || Views.Dialogs.Owner?.Clipboard is not { } cb) return;
        await cb.SetTextAsync(s.Curl);
        _host.Notify(Loc.T("rv.curlCopied"));
    }

    [RelayCommand]
    private async Task CopyBodyAsync()
    {
        if (Views.Dialogs.Owner?.Clipboard is not { } cb) return;
        var text = IsSentDetail ? SentDocument.Text : ResponseDocument.Text;
        if (text.Length == 0) return;
        await cb.SetTextAsync(text);
        _host.Notify(Loc.T(IsSentDetail ? "run.sentCopied" : "rv.responseCopied"));
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected is { } s) _open(s.RelPath);
    }

    [RelayCommand]
    private async Task CopyCliAsync()
    {
        if (Views.Dialogs.Owner?.Clipboard is not { } cb) return;
        await cb.SetTextAsync(CliHint);
        _host.Notify(Loc.F("rv.copiedWhat", CliHint));
    }
}
