using CommunityToolkit.Mvvm.ComponentModel;
using RestPeach.Core;
using RestPeach.Core.Model;

namespace RestPeach.ViewModels;

public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>The Auth tab (of a request, a folder or the collection).</summary>
public sealed partial class AuthEditor : ObservableObject
{
    public IReadOnlyList<Choice> Types { get; }
    public IReadOnlyList<Choice> Locations { get; } = new[] { new Choice("header", Loc.T("auth.inHeader")), new Choice("query", Loc.T("auth.inQuery")) };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBearer), nameof(IsBasic), nameof(IsApiKey), nameof(IsInherit), nameof(IsNone), nameof(Summary))]
    private Choice _type;
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private Choice _location;
    /// <summary>What "inherit" resolves to right now ("Bearer, from the folder Users").</summary>
    [ObservableProperty] private string _inheritedText = "";

    public event Action? Changed;
    private bool _loading;

    /// <param name="canInherit">False for the collection, which has nothing above it.</param>
    public AuthEditor(bool canInherit)
    {
        var all = new List<Choice>();
        if (canInherit) all.Add(new Choice(AuthTypes.Inherit, Loc.T("auth.inherit")));
        all.Add(new Choice(AuthTypes.None, Loc.T("auth.none")));
        all.Add(new Choice(AuthTypes.Bearer, "Bearer Token"));
        all.Add(new Choice(AuthTypes.Basic, "Basic Auth"));
        all.Add(new Choice(AuthTypes.ApiKey, "API Key"));
        Types = all;
        _type = all[0];
        _location = Locations[0];
    }

    public bool IsBearer => Type.Value == AuthTypes.Bearer;
    public bool IsBasic => Type.Value == AuthTypes.Basic;
    public bool IsApiKey => Type.Value == AuthTypes.ApiKey;
    public bool IsInherit => Type.Value == AuthTypes.Inherit;
    public bool IsNone => Type.Value == AuthTypes.None;
    public string Summary => Type.Value is AuthTypes.Inherit or AuthTypes.None ? "" : Type.Label;

    public void Load(AuthSpec a)
    {
        _loading = true;
        try
        {
            Type = Types.FirstOrDefault(t => t.Value == a.Type) ?? Types[0];
            Token = a.Token;
            Username = a.Username;
            Password = a.Password;
            Key = a.Key;
            Value = a.Value;
            Location = Locations.FirstOrDefault(l => l.Value == a.In) ?? Locations[0];
        }
        finally { _loading = false; }
    }

    public AuthSpec ToModel() => new()
    {
        Type = Type.Value, Token = Token, Username = Username, Password = Password, Key = Key, Value = Value, In = Location.Value,
    };

    partial void OnTypeChanged(Choice value) => Fire();
    partial void OnTokenChanged(string value) => Fire();
    partial void OnUsernameChanged(string value) => Fire();
    partial void OnPasswordChanged(string value) => Fire();
    partial void OnKeyChanged(string value) => Fire();
    partial void OnValueChanged(string value) => Fire();
    partial void OnLocationChanged(Choice value) => Fire();

    private void Fire()
    {
        if (!_loading) Changed?.Invoke();
    }
}
