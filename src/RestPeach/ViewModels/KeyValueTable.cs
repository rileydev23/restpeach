using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Core.Model;

namespace RestPeach.ViewModels;

/// <summary>One editable row of a <see cref="KeyValueTable"/>.</summary>
public sealed partial class KeyValueRow : ObservableObject
{
    private readonly KeyValueTable _owner;

    [ObservableProperty] private bool _active = true;
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    /// <summary>Form-data: the value is the path of a file to upload.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TypeText))]
    private bool _isFile;

    public KeyValueRow(KeyValueTable owner) => _owner = owner;

    public KeyValueTable Owner => _owner;
    public bool IsPlaceholder => _owner.Rows.Count > 0 && ReferenceEquals(_owner.Rows[^1], this) && Key.Length == 0 && Value.Length == 0;
    public string TypeText => IsFile ? Core.Loc.T("kv.file") : Core.Loc.T("kv.text");
    public bool ShowActive => _owner.ShowActive;
    public bool ShowType => _owner.ShowType;
    /// <summary>The empty last row keeps its column but shows no checkbox.</summary>
    public double CheckOpacity => IsPlaceholder ? 0 : 1;
    public bool Secret => _owner.Secret && !_owner.Reveal;
    public char PasswordChar => Secret ? '•' : '\0';
    public string KeyWatermark => _owner.KeyWatermark;
    public string ValueWatermark => _owner.ValueWatermark;
    public IReadOnlyList<string>? Suggestions => _owner.Suggestions;
    public bool HasSuggestions => _owner.Suggestions is not null;

    partial void OnActiveChanged(bool value) => _owner.RowChanged(this);
    partial void OnKeyChanged(string value) => _owner.RowChanged(this);
    partial void OnValueChanged(string value) => _owner.RowChanged(this);
    partial void OnIsFileChanged(bool value) => _owner.RowChanged(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    [RelayCommand]
    private void ToggleType() => IsFile = !IsFile;

    /// <summary>Form-data file rows: "…" picks the file.</summary>
    [RelayCommand]
    private async Task PickFileAsync()
    {
        var path = await Views.Dialogs.PickFile(Core.Loc.T("kv.pickFile"));
        if (path is null) return;
        IsFile = true;
        Value = _owner.FileBase is { Length: > 0 } b && path.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(b, path).Replace('\\', '/')
            : path;
    }

    public void NotifyLook()
    {
        OnPropertyChanged(nameof(Secret));
        OnPropertyChanged(nameof(PasswordChar));
        OnPropertyChanged(nameof(IsPlaceholder));
        OnPropertyChanged(nameof(CheckOpacity));
    }

    public KeyValue ToModel() => new() { Active = Active, Key = Key.Trim(), Value = Value, Type = IsFile ? "file" : null };
}

/// <summary>
/// An editable key/value list (params, headers, form fields, variables). There is always one empty row at the end:
/// typing in it adds the next one, like a spreadsheet. <see cref="Changed"/> fires on every edit.
/// </summary>
public sealed class KeyValueTable : ObservableObject
{
    public ObservableCollection<KeyValueRow> Rows { get; } = new();

    public bool ShowActive { get; init; } = true;
    public bool ShowType { get; init; }
    /// <summary>Values are masked unless <see cref="Reveal"/>.</summary>
    public bool Secret { get; init; }
    public string KeyWatermark { get; init; } = Core.Loc.T("kv.key");
    public string ValueWatermark { get; init; } = Core.Loc.T("kv.value");
    /// <summary>Header names for the key autocomplete.</summary>
    public IReadOnlyList<string>? Suggestions { get; init; }
    /// <summary>Folder relative file paths start from (form-data uploads).</summary>
    public string? FileBase { get; set; }

    private bool _reveal;
    public bool Reveal
    {
        get => _reveal;
        set
        {
            if (!SetProperty(ref _reveal, value)) return;
            foreach (var r in Rows) r.NotifyLook();
        }
    }

    public event Action? Changed;
    private bool _loading;

    public KeyValueTable() => Rows.Add(new KeyValueRow(this));

    public int Count => Rows.Count(r => r.Key.Trim().Length > 0);
    public int ActiveCount => Rows.Count(r => r.Active && r.Key.Trim().Length > 0);

    public void Load(IEnumerable<KeyValue> items)
    {
        _loading = true;
        try
        {
            Rows.Clear();
            foreach (var i in items)
                Rows.Add(new KeyValueRow(this) { Active = i.Active, Key = i.Key, Value = i.Value, IsFile = i.IsFile });
            Rows.Add(new KeyValueRow(this));
        }
        finally { _loading = false; }
        foreach (var r in Rows) r.NotifyLook();
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ActiveCount));
    }

    public void Load(IEnumerable<KeyValuePair<string, string>> map) => Load(map.Select(kv => KeyValue.Of(kv.Key, kv.Value)));

    public List<KeyValue> ToList() => Rows.Where(r => r.Key.Trim().Length > 0 || r.Value.Length > 0).Select(r => r.ToModel()).ToList();

    public SortedDictionary<string, string> ToMap()
    {
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Rows.Where(r => r.Key.Trim().Length > 0)) map[r.Key.Trim()] = r.Value;
        return map;
    }

    /// <summary>For bulk edits made through <see cref="Load(IEnumerable{KeyValue})"/>, which stays silent.</summary>
    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ActiveCount));
        Changed?.Invoke();
    }

    internal void RowChanged(KeyValueRow row)
    {
        if (_loading) return;
        // Typing in the last (empty) row grows the table.
        if (Rows.Count > 0 && ReferenceEquals(Rows[^1], row) && (row.Key.Length > 0 || row.Value.Length > 0))
        {
            var prev = Rows[^1];
            Rows.Add(new KeyValueRow(this));
            prev.NotifyLook();
        }
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ActiveCount));
        Changed?.Invoke();
    }

    internal void Remove(KeyValueRow row)
    {
        if (ReferenceEquals(Rows[^1], row) && row.Key.Length == 0 && row.Value.Length == 0) return;
        Rows.Remove(row);
        if (Rows.Count == 0 || Rows[^1].Key.Length > 0 || Rows[^1].Value.Length > 0) Rows.Add(new KeyValueRow(this));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(ActiveCount));
        Changed?.Invoke();
    }

    /// <summary>Standard request headers for the key autocomplete.</summary>
    public static readonly string[] CommonHeaders =
    {
        "Accept", "Accept-Charset", "Accept-Encoding", "Accept-Language", "Authorization", "Cache-Control", "Connection",
        "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length", "Content-Type", "Cookie", "DNT",
        "Expect", "Forwarded", "From", "Host", "If-Match", "If-Modified-Since", "If-None-Match", "If-Range",
        "If-Unmodified-Since", "Origin", "Pragma", "Prefer", "Range", "Referer", "TE", "Upgrade", "User-Agent", "Via",
        "X-Api-Key", "X-Correlation-Id", "X-CSRF-Token", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto",
        "X-Request-Id", "X-Requested-With",
    };
}
