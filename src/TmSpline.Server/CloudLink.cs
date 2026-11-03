using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TmSpline.Server;

/// <summary>What the editor learns about the cloud link (GET /api/cloud).</summary>
public sealed record CloudStatus(bool Available, bool SignedIn, string? User, string? Url, string? AppVersion, bool LoggingIn, string? Error);

/// <summary>
/// The desktop app's link to TMSpline's hosted service (Cloud:Url). It signs the user in through Authentik in their own
/// browser (authorization code + PKCE, back to http://127.0.0.1:{port}/callback on this server), keeps the refresh token
/// on this machine (encrypted with the user's Windows account where available), and forwards the editor's requests to
/// the service's API with the access token. The editor never sees a token.
/// </summary>
public sealed class CloudLink
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ILogger<CloudLink> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    /// <summary>For large downloads (app updates): no overall timeout, the caller cancels.</summary>
    private readonly HttpClient _downloads = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _lock = new();
    private readonly string? _tokenFile;
    private Client? _client;
    private Tokens? _tokens;
    private Pending? _pending;
    private string? _error;

    /// <summary>How to sign in, from the service's /api/client and Authentik's discovery document.</summary>
    private sealed record Client(bool Dev, string? ClientId, string? AuthorizeEndpoint, string? TokenEndpoint);

    private sealed record Pending(string State, string Verifier, string RedirectUri, DateTimeOffset Started);

    private sealed class Tokens
    {
        public string? Access { get; set; }
        public DateTimeOffset AccessExpires { get; set; }
        public string? Refresh { get; set; }
        public string? User { get; set; }
    }

    public CloudLink(IConfiguration configuration, IWebHostEnvironment environment, ILogger<CloudLink> logger)
    {
        _logger = logger;
        if (configuration["Cloud:Url"] is not { Length: > 0 } url) return;
        Url = url.TrimEnd('/');
        AppVersion = configuration["App:Version"];
        _tokenFile = Path.GetFullPath(configuration["Cloud:TokenFile"] ?? "cloud-token.dat", environment.ContentRootPath);
        _tokens = LoadTokens();
    }

    public string? Url { get; }
    public string? AppVersion { get; }
    public bool Enabled => Url is not null;

    public CloudStatus Status
    {
        get
        {
            lock (_lock)
                return new CloudStatus(Enabled, _tokens?.Refresh is not null || _tokens?.Access is not null, _tokens?.User, Url, AppVersion,
                    _pending is not null && DateTimeOffset.UtcNow - _pending.Started < TimeSpan.FromMinutes(10), _error);
        }
    }

    // ---- sign-in ---------------------------------------------------------------------------------------------------

    private async Task<Client> GetClient(CancellationToken ct)
    {
        if (_client is { } known) return known;
        using var info = JsonDocument.Parse(await _http.GetStringAsync($"{Url}/api/client", ct));
        var root = info.RootElement;
        if (root.TryGetProperty("dev", out var dev) && dev.GetBoolean())
            return _client = new Client(true, null, null, null);
        var authority = root.GetProperty("authority").GetString()!.TrimEnd('/');
        using var discovery = JsonDocument.Parse(await _http.GetStringAsync($"{authority}/.well-known/openid-configuration", ct));
        return _client = new Client(false, root.GetProperty("clientId").GetString(),
            discovery.RootElement.GetProperty("authorization_endpoint").GetString(),
            discovery.RootElement.GetProperty("token_endpoint").GetString());
    }

    /// <summary>Starts signing in: opens Authentik's login in the user's browser, which comes back to <paramref name="redirectUri"/>.</summary>
    public async Task<string?> StartLogin(string redirectUri, CancellationToken ct)
    {
        var client = await GetClient(ct);
        if (client.Dev)
        {
            // The service runs in development (no Authentik): any token is accepted there.
            lock (_lock) _tokens = new Tokens { Access = "dev", AccessExpires = DateTimeOffset.MaxValue };
            await FetchUser(ct);
            SaveTokens();
            return null;
        }
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        lock (_lock)
        {
            _pending = new Pending(state, verifier, redirectUri, DateTimeOffset.UtcNow);
            _error = null;
        }
        var url = $"{client.AuthorizeEndpoint}?response_type=code&client_id={Uri.EscapeDataString(client.ClientId!)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}&scope={Uri.EscapeDataString("openid profile email offline_access")}"
            + $"&state={state}&code_challenge={challenge}&code_challenge_method=S256";
        OpenBrowser(url);
        return url;
    }

    /// <summary>Authentik sent the browser back (GET /callback): trades the code for tokens. Returns an error message or null.</summary>
    public async Task<string?> CompleteLogin(string? code, string? state, string? error, CancellationToken ct)
    {
        Pending? pending;
        lock (_lock) pending = _pending;
        if (pending is null || state != pending.State) return "This sign-in is not the one TMSpline started. Please start it again from the app.";
        if (error is not null || code is null)
        {
            lock (_lock) (_pending, _error) = (null, error == "access_denied" ? "Your account may not use TMSpline." : error ?? "No code.");
            return _error;
        }
        try
        {
            var client = await GetClient(ct);
            var tokens = await TokenRequest(client, new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = pending.RedirectUri,
                ["client_id"] = client.ClientId!,
                ["code_verifier"] = pending.Verifier,
            }, ct);
            lock (_lock) (_tokens, _pending, _error) = (tokens, null, null);
            await FetchUser(ct);
            SaveTokens();
            _logger.LogInformation("Signed in to {Url} as {User}", Url, _tokens?.User);
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(e, "Signing in failed");
            lock (_lock) (_pending, _error) = (null, "Signing in failed: " + e.Message);
            return _error;
        }
    }

    public void SignOut()
    {
        lock (_lock) (_tokens, _pending, _error) = (null, null, null);
        if (_tokenFile is not null && File.Exists(_tokenFile)) File.Delete(_tokenFile);
    }

    private async Task<Tokens> TokenRequest(Client client, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync(client.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"token endpoint answered {(int)response.StatusCode}: {body}");
        var answer = JsonSerializer.Deserialize<TokenAnswer>(body, Json) ?? throw new JsonException("empty token answer");
        return new Tokens
        {
            Access = answer.AccessToken,
            // A little early, so a request never leaves with a token that expires on the way.
            AccessExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, answer.ExpiresIn - 30)),
            Refresh = answer.RefreshToken ?? (form["grant_type"] == "refresh_token" ? form["refresh_token"] : null),
            User = _tokens?.User,
        };
    }

    private sealed record TokenAnswer(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);

    private async Task FetchUser(CancellationToken ct)
    {
        using var response = await Send(HttpMethod.Get, "me", null, null, ct);
        if (!response.IsSuccessStatusCode) return;
        using var me = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        lock (_lock)
            if (_tokens is not null) _tokens.User = me.RootElement.GetProperty("name").GetString();
    }

    /// <summary>A valid access token, refreshed when needed; null when signed out (or the refresh token was refused).</summary>
    private async Task<string?> AccessToken(bool forceRefresh, CancellationToken ct)
    {
        Tokens? tokens;
        lock (_lock) tokens = _tokens;
        if (tokens is null) return null;
        if (!forceRefresh && tokens.Access is not null && tokens.AccessExpires > DateTimeOffset.UtcNow) return tokens.Access;
        if (tokens.Refresh is null) return forceRefresh ? null : tokens.Access;
        await _refresh.WaitAsync(ct);
        try
        {
            lock (_lock)
                if (_tokens != tokens) return _tokens?.Access; // refreshed (or signed out) meanwhile
            var client = await GetClient(ct);
            try
            {
                var fresh = await TokenRequest(client, new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = tokens.Refresh,
                    ["client_id"] = client.ClientId!,
                }, ct);
                lock (_lock) _tokens = fresh;
                SaveTokens();
                return fresh.Access;
            }
            catch (InvalidOperationException e)
            {
                // Refused (expired, revoked, user removed): signed out.
                _logger.LogInformation("Refreshing the cloud login failed, signing out: {Message}", e.Message);
                lock (_lock) (_tokens, _error) = (null, "Your sign-in expired. Please sign in again.");
                if (_tokenFile is not null && File.Exists(_tokenFile)) File.Delete(_tokenFile);
                return null;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    // ---- requests --------------------------------------------------------------------------------------------------

    /// <summary>
    /// Sends a request to the service with the access token, refreshing it once on 401. <paramref name="path"/> is below
    /// /api/, or from the service's root when it starts with "/" (downloads). With <paramref name="streamed"/> the
    /// answer's body is read as it arrives (large files).
    /// </summary>
    public async Task<HttpResponseMessage> Send(HttpMethod method, string path, byte[]? body, string? contentType, CancellationToken ct,
        bool streamed = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await AccessToken(attempt > 0, ct);
            if (token is null) return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
            var request = new HttpRequestMessage(method, path.StartsWith('/') ? Url + path : $"{Url}/api/{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType ?? "application/json");
            }
            var response = await (streamed ? _downloads : _http).SendAsync(request,
                streamed ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || attempt > 0) return response;
            response.Dispose();
        }
    }

    /// <summary>Opens a page of the service (path from its root) in the user's browser.</summary>
    public void OpenPage(string path)
    {
        if (!path.StartsWith('/') || path.StartsWith("//")) throw new ArgumentException("Not a path on the service.", nameof(path));
        OpenBrowser(Url + path);
    }

    private static void OpenBrowser(string url)
    {
        if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS()) Process.Start("open", url);
        else Process.Start("xdg-open", url);
    }

    // ---- token file ------------------------------------------------------------------------------------------------

    private Tokens? LoadTokens()
    {
        try
        {
            if (_tokenFile is null || !File.Exists(_tokenFile)) return null;
            var bytes = File.ReadAllBytes(_tokenFile);
            if (OperatingSystem.IsWindows()) bytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Tokens>(bytes, Json);
        }
        catch (Exception e) when (e is IOException or CryptographicException or JsonException)
        {
            _logger.LogWarning(e, "Ignoring the saved cloud login");
            return null;
        }
    }

    private void SaveTokens()
    {
        if (_tokenFile is null) return;
        Tokens? tokens;
        lock (_lock) tokens = _tokens;
        if (tokens is null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(tokens, Json);
        if (OperatingSystem.IsWindows()) bytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
        File.WriteAllBytes(_tokenFile, bytes);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
