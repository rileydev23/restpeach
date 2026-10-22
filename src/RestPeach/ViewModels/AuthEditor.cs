using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RestPeach.Core;
using RestPeach.Core.Http;
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
    public IReadOnlyList<Choice> Grants { get; } = new[]
    {
        new Choice(OAuthGrants.ClientCredentials, "Client credentials"),
        new Choice(OAuthGrants.Password, Loc.T("oauth.grant.password")),
        new Choice(OAuthGrants.AuthorizationCode, Loc.T("oauth.grant.code")),
    };
    public IReadOnlyList<Choice> ClientAuths { get; } = new[] { new Choice("basic", Loc.T("oauth.clientAuth.basic")), new Choice("body", Loc.T("oauth.clientAuth.body")) };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBearer), nameof(IsBasic), nameof(IsApiKey), nameof(IsInherit), nameof(IsNone), nameof(Summary),
        nameof(IsDigest), nameof(IsAws), nameof(IsOAuth2), nameof(HasUserPassword), nameof(IsPasswordGrant), nameof(IsCodeGrant))]
    private Choice _type;
    [ObservableProperty] private string _token = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _key = "";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private Choice _location;
    [ObservableProperty] private string _accessKey = "";
    [ObservableProperty] private string _secretKey = "";
    [ObservableProperty] private string _region = "";
    [ObservableProperty] private string _service = "";
    [ObservableProperty] private string _sessionToken = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUserPassword), nameof(IsPasswordGrant), nameof(IsCodeGrant))]
    private Choice _grant;
    [ObservableProperty] private string _tokenUrl = "";
    [ObservableProperty] private string _authUrl = "";
    [ObservableProperty] private string _clientId = "";
    [ObservableProperty] private string _clientSecret = "";
    [ObservableProperty] private string _scope = "";
    [ObservableProperty] private string _redirectUri = "";
    [ObservableProperty] private Choice _clientAuth;
    /// <summary>What "inherit" resolves to right now ("Bearer, from the folder Users").</summary>
    [ObservableProperty] private string _inheritedText = "";
    /// <summary>OAuth 2.0: whether a token is in memory and until when.</summary>
    [ObservableProperty] private string _tokenStatus = "";
    [ObservableProperty] private bool _isFetchingToken;

    public event Action? Changed;
    private bool _loading;

    /// <summary>Set by the tab that shows this editor: expands the auth with its variables, and where tokens live.</summary>
    private Func<AuthSpec, AuthSpec>? _expand;
    private Func<HttpOptions>? _options;
    private HttpEngine? _engine;
    private Action<string>? _notify;

    /// <param name="canInherit">False for the collection, which has nothing above it.</param>
    public AuthEditor(bool canInherit)
    {
        var all = new List<Choice>();
        if (canInherit) all.Add(new Choice(AuthTypes.Inherit, Loc.T("auth.inherit")));
        all.Add(new Choice(AuthTypes.None, Loc.T("auth.none")));
        all.Add(new Choice(AuthTypes.Bearer, "Bearer Token"));
        all.Add(new Choice(AuthTypes.Basic, "Basic Auth"));
        all.Add(new Choice(AuthTypes.ApiKey, "API Key"));
        all.Add(new Choice(AuthTypes.OAuth2, "OAuth 2.0"));
        all.Add(new Choice(AuthTypes.Digest, "Digest Auth"));
        all.Add(new Choice(AuthTypes.AwsSigV4, "AWS Signature V4"));
        Types = all;
        _type = all[0];
        _location = Locations[0];
        _grant = Grants[0];
        _clientAuth = ClientAuths[0];
    }

    public void ConnectOAuth(HttpEngine engine, Func<AuthSpec, AuthSpec> expand, Func<HttpOptions> options, Action<string> notify)
    {
        _engine = engine;
        _expand = expand;
        _options = options;
        _notify = notify;
        RefreshTokenStatus();
    }

    public bool IsBearer => Type.Value == AuthTypes.Bearer;
    public bool IsBasic => Type.Value == AuthTypes.Basic;
    public bool IsApiKey => Type.Value == AuthTypes.ApiKey;
    public bool IsDigest => Type.Value == AuthTypes.Digest;
    public bool IsAws => Type.Value == AuthTypes.AwsSigV4;
    public bool IsOAuth2 => Type.Value == AuthTypes.OAuth2;
    public bool IsInherit => Type.Value == AuthTypes.Inherit;
    public bool IsNone => Type.Value == AuthTypes.None;
    public bool IsPasswordGrant => IsOAuth2 && Grant.Value == OAuthGrants.Password;
    public bool IsCodeGrant => IsOAuth2 && Grant.Value == OAuthGrants.AuthorizationCode;
    /// <summary>Basic, Digest and the OAuth password grant ask for a user and a password.</summary>
    public bool HasUserPassword => IsBasic || IsDigest || IsPasswordGrant;
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
            AccessKey = a.AccessKey;
            SecretKey = a.SecretKey;
            Region = a.Region;
            Service = a.Service;
            SessionToken = a.SessionToken;
            Grant = Grants.FirstOrDefault(g => g.Value == a.Grant) ?? Grants[0];
            TokenUrl = a.TokenUrl;
            AuthUrl = a.AuthUrl;
            ClientId = a.ClientId;
            ClientSecret = a.ClientSecret;
            Scope = a.Scope;
            RedirectUri = a.RedirectUri;
            ClientAuth = ClientAuths.FirstOrDefault(c => c.Value == a.ClientAuth) ?? ClientAuths[0];
        }
        finally { _loading = false; }
        RefreshTokenStatus();
    }

    public AuthSpec ToModel() => new()
    {
        Type = Type.Value, Token = Token, Username = Username, Password = Password, Key = Key, Value = Value, In = Location.Value,
        AccessKey = AccessKey, SecretKey = SecretKey, Region = Region, Service = Service, SessionToken = SessionToken,
        Grant = Grant.Value, TokenUrl = TokenUrl, AuthUrl = AuthUrl, ClientId = ClientId, ClientSecret = ClientSecret, Scope = Scope,
        RedirectUri = RedirectUri, ClientAuth = ClientAuth.Value,
    };

    partial void OnTypeChanged(Choice value) => Fire();
    partial void OnTokenChanged(string value) => Fire();
    partial void OnUsernameChanged(string value) => Fire();
    partial void OnPasswordChanged(string value) => Fire();
    partial void OnKeyChanged(string value) => Fire();
    partial void OnValueChanged(string value) => Fire();
    partial void OnLocationChanged(Choice value) => Fire();
    partial void OnAccessKeyChanged(string value) => Fire();
    partial void OnSecretKeyChanged(string value) => Fire();
    partial void OnRegionChanged(string value) => Fire();
    partial void OnServiceChanged(string value) => Fire();
    partial void OnSessionTokenChanged(string value) => Fire();
    partial void OnGrantChanged(Choice value) => Fire();
    partial void OnTokenUrlChanged(string value) => Fire();
    partial void OnAuthUrlChanged(string value) => Fire();
    partial void OnClientIdChanged(string value) => Fire();
    partial void OnClientSecretChanged(string value) => Fire();
    partial void OnScopeChanged(string value) => Fire();
    partial void OnRedirectUriChanged(string value) => Fire();
    partial void OnClientAuthChanged(Choice value) => Fire();

    private void Fire()
    {
        if (_loading) return;
        Changed?.Invoke();
        if (IsOAuth2) RefreshTokenStatus();
    }

    // ------------------------------------------------------------ OAuth 2.0

    private AuthSpec? Expanded() => _expand is null || !IsOAuth2 ? null : _expand(ToModel());

    public void RefreshTokenStatus()
    {
        if (Expanded() is not { } a || _engine is null) { TokenStatus = ""; return; }
        var t = _engine.CachedToken(a);
        TokenStatus = t is null ? Loc.T("oauth.status.none")
            : t.IsExpired(DateTimeOffset.Now) ? Loc.T(t.RefreshToken is null ? "oauth.status.expired" : "oauth.status.refresh")
            : t.Expires is { } e ? Loc.F("oauth.status.valid", Preview(t.AccessToken), Remaining(e - DateTimeOffset.Now))
            : Loc.F("oauth.status.validForever", Preview(t.AccessToken));
    }

    private static string Remaining(TimeSpan t) =>
        t.TotalMinutes < 1 ? $"{Math.Max(0, (int)t.TotalSeconds)} s" : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min" : $"{(int)t.TotalHours} h {t.Minutes} min";

    private static string Preview(string token) => token.Length <= 14 ? token : token[..10] + "…";

    /// <summary>Asks for a new token now (a login in the browser for the authorization code).</summary>
    [RelayCommand]
    private async Task GetTokenAsync()
    {
        if (Expanded() is not { } a || _engine is null || IsFetchingToken) return;
        IsFetchingToken = true;
        TokenStatus = Loc.T(a.Grant == OAuthGrants.AuthorizationCode ? "oauth.status.browser" : "oauth.status.asking");
        try
        {
            var t = await _engine.OAuthTokenAsync(a, _options?.Invoke() ?? new HttpOptions(), fresh: true, CancellationToken.None);
            _notify?.Invoke(Loc.F("oauth.got", t.Scope is { Length: > 0 } s ? s : "—"));
            RefreshTokenStatus();
        }
        catch (Exception e) when (e is OAuthException or HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            TokenStatus = e.Message;
            _notify?.Invoke(e.Message);
        }
        finally { IsFetchingToken = false; }
    }

    [RelayCommand]
    private void ForgetToken()
    {
        if (Expanded() is not { } a || _engine is null) return;
        _engine.ForgetToken(a);
        RefreshTokenStatus();
    }
}
