using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using RestPeach.Controls;
using RestPeach.Services;
using RestPeach.ViewModels;

namespace RestPeach.Views;

/// <summary>
/// Request builder and response viewer. Two AvaloniaEdit editors (the raw body, read-write; the response, read-only)
/// whose documents belong to the tab, so switching tabs swaps documents instead of rebuilding editors. Avalonia
/// recycles this view between request tabs: everything is re-attached on DataContext change.
/// </summary>
public partial class RequestView : UserControl
{
    private RequestTab? _tab;
    private ViewerSettings? _settings;
    private TextMate.Installation? _bodyTm, _responseTm;
    private string? _bodyExt, _responseExt;

    public RequestView()
    {
        InitializeComponent();
        Configure(BodyEditor, readOnly: false);
        Configure(ResponseEditor, readOnly: true);
        SearchPanel.Install(BodyEditor);
        SearchPanel.Install(ResponseEditor);
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => AttachSettings();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
            _settings = null;
        };
    }

    internal static void Configure(TextEditor e, bool readOnly)
    {
        var o = e.Options;
        o.HighlightCurrentLine = !readOnly;
        o.EnableHyperlinks = false;
        o.EnableEmailHyperlinks = false;
        o.ConvertTabsToSpaces = true;
        o.IndentationSize = 2;
        o.CutCopyWholeLine = true;
        var area = e.TextArea;
        area.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#232428"));
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.SelectionBrush = new SolidColorBrush(Color.Parse("#33507a"));
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 0;
        area.SelectionForeground = null;
        area.Caret.CaretBrush = Palette.Accent;
        e.IsReadOnly = readOnly;
    }

    private void AttachSettings()
    {
        var settings = (this.FindAncestorOfType<ProjectView>()?.DataContext as ProjectViewModel)?.Settings;
        if (ReferenceEquals(settings, _settings)) return;
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
        _settings = settings;
        if (_settings is not null) _settings.PropertyChanged += OnSettingsChanged;
        ApplySettings();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        if (_settings is null) return;
        foreach (var e in new[] { BodyEditor, ResponseEditor })
        {
            e.FontSize = _settings.FontSize - 1;
            e.WordWrap = _settings.WordWrap;
        }
    }

    private void Attach()
    {
        if (_tab is not null)
        {
            _tab.PropertyChanged -= OnTabChanged;
            _tab.FocusUrlRequested -= OnFocusUrl;
            _tab.ResponseChanged -= OnResponseChanged;
            _tab.BodyTypeChangedForView -= OnBodyTypeChanged;
        }
        _tab = DataContext as RequestTab;
        if (_tab is null) return;
        _tab.PropertyChanged += OnTabChanged;
        _tab.FocusUrlRequested += OnFocusUrl;
        _tab.ResponseChanged += OnResponseChanged;
        _tab.BodyTypeChangedForView += OnBodyTypeChanged;

        BodyEditor.Document = _tab.BodyDocument;
        ResponseEditor.Document = _tab.ResponseDocument;
        _bodyExt = _responseExt = null;
        ApplyBodyGrammar();
        ApplyResponseGrammar();
        AttachSettings();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RequestTab.ResponseExtension)) ApplyResponseGrammar();
    }

    private void OnBodyTypeChanged() => ApplyBodyGrammar();

    private void OnResponseChanged()
    {
        ApplyResponseGrammar();
        Dispatcher.UIThread.Post(() =>
        {
            ResponseEditor.ScrollToHome();
            ResponseEditor.CaretOffset = 0;
        }, DispatcherPriority.Background);
    }

    private void OnFocusUrl() => Dispatcher.UIThread.Post(() =>
    {
        UrlBox.Focus();
        UrlBox.CaretIndex = UrlBox.Text?.Length ?? 0;
    }, DispatcherPriority.Background);

    private void ApplyBodyGrammar()
    {
        if (_tab is null || _tab.BodyExtension == _bodyExt) return;
        _bodyExt = _tab.BodyExtension;
        _bodyTm = Install(BodyEditor, _bodyTm, _bodyExt);
    }

    private void ApplyResponseGrammar()
    {
        if (_tab is null || _tab.ResponseExtension == _responseExt) return;
        _responseExt = _tab.ResponseExtension;
        _responseTm = Install(ResponseEditor, _responseTm, _responseExt);
    }

    /// <summary>A fresh TextMate installation per document is the simplest way to keep its model in sync.</summary>
    internal static TextMate.Installation? Install(TextEditor editor, TextMate.Installation? old, string extension)
    {
        old?.Dispose();
        try
        {
            var tm = editor.InstallTextMate(Highlighting.Instance);
            tm.SetTheme(Highlighting.Instance.Theme);
            if (Highlighting.Instance.ScopeFor("x" + extension) is { } scope) tm.SetGrammar(scope);
            return tm;
        }
        catch { return null; } // plain text is fine
    }
}
