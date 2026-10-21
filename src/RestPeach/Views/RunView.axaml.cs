using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Search;
using AvaloniaEdit.TextMate;
using RestPeach.Services;
using RestPeach.ViewModels;

namespace RestPeach.Views;

/// <summary>
/// Results of a run and, below, the detail of the selected one: its tests, the response (body and headers) and what
/// was sent. Like <see cref="RequestView"/>, the editors' documents belong to the tab and the view is recycled.
/// </summary>
public partial class RunView : UserControl
{
    private RunTab? _tab;
    private ViewerSettings? _settings;
    private TextMate.Installation? _responseTm, _sentTm;
    private string? _responseExt, _sentExt;

    public RunView()
    {
        InitializeComponent();
        RequestView.Configure(ResponseEditor, readOnly: true);
        RequestView.Configure(SentEditor, readOnly: true);
        SearchPanel.Install(ResponseEditor);
        SearchPanel.Install(SentEditor);
        // Double click (or Enter) opens the request of the result.
        Results.DoubleTapped += (_, _) => (DataContext as RunTab)?.OpenSelected();
        Results.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter) { (DataContext as RunTab)?.OpenSelected(); e.Handled = true; }
        };
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => AttachSettings();
        DetachedFromVisualTree += (_, _) =>
        {
            if (_settings is not null) _settings.PropertyChanged -= OnSettingsChanged;
            _settings = null;
        };
    }

    private void Attach()
    {
        if (_tab is not null) _tab.DetailChanged -= OnDetailChanged;
        _tab = DataContext as RunTab;
        if (_tab is null) return;
        _tab.DetailChanged += OnDetailChanged;
        ResponseEditor.Document = _tab.ResponseDocument;
        SentEditor.Document = _tab.SentDocument;
        _responseExt = _sentExt = null;
        ApplyGrammars();
        AttachSettings();
    }

    private void OnDetailChanged()
    {
        ApplyGrammars();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var e in new[] { ResponseEditor, SentEditor })
            {
                e.ScrollToHome();
                e.CaretOffset = 0;
            }
        }, DispatcherPriority.Background);
    }

    private void ApplyGrammars()
    {
        if (_tab is null) return;
        if (_tab.ResponseExtension != _responseExt)
        {
            _responseExt = _tab.ResponseExtension;
            _responseTm = RequestView.Install(ResponseEditor, _responseTm, _responseExt);
        }
        var sentExt = _tab.Selected?.SentBodyExtension ?? ".txt";
        if (sentExt != _sentExt)
        {
            _sentExt = sentExt;
            _sentTm = RequestView.Install(SentEditor, _sentTm, _sentExt);
        }
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
        foreach (var e in new[] { ResponseEditor, SentEditor })
        {
            e.FontSize = _settings.FontSize - 1;
            e.WordWrap = _settings.WordWrap;
        }
    }
}
