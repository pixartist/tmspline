using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using TmSpline.Cloud;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var dataDir = Path.GetFullPath(config["Cloud:DataDir"] ?? "data", builder.Environment.ContentRootPath);
var releasesDir = Path.GetFullPath(config["Cloud:ReleasesDir"] ?? Path.Combine(dataDir, "releases"), builder.Environment.ContentRootPath);
Directory.CreateDirectory(releasesDir);
// Who runs the site (Impressum, privacy page): site.json next to the data folder, kept out of the repository.
var siteFile = Path.GetFullPath(config["Cloud:SiteFile"] ?? Path.Combine(dataDir, "..", "site.json"), builder.Environment.ContentRootPath);
builder.Configuration.AddJsonFile(siteFile, optional: true, reloadOnChange: true);
builder.Services.Configure<SiteOperator>(config.GetSection("Site:Operator"));
var maxTrackBytes = config.GetValue("Cloud:MaxTrackBytes", 5_000_000);
var maxTracks = config.GetValue("Cloud:MaxTracksPerUser", 1000);

builder.Services.AddSingleton(new TrackStore(Path.Combine(dataDir, "tmspline.db")));
builder.Services.AddSingleton(new Releases(releasesDir));
// Login cookies stay valid across restarts.
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys"))).SetApplicationName("TMSpline");
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = maxTrackBytes + 64_000);

// Behind Caddy: the scheme and client address come from its X-Forwarded headers (only trusted from the listed proxies).
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in config.GetSection("Proxy:KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(proxy));
});

// ---- login ---------------------------------------------------------------------------------------------------------
// Website: a cookie, signed in through Authentik (OpenID Connect, authorization code + PKCE).
// Desktop app: Authentik's access token as a bearer token (the app signs in with the same Authentik application).
// Development without Authentik: Auth:DevUser signs every request in as that user.
var devUser = builder.Environment.IsDevelopment() ? config["Auth:DevUser"] : null;
string[] webSchemes, apiSchemes;
var authentication = builder.Services.AddAuthentication(o =>
{
    o.DefaultScheme = devUser is null ? CookieAuthenticationDefaults.AuthenticationScheme : DevAuthentication.SchemeName;
    o.DefaultChallengeScheme = devUser is null ? "oidc" : DevAuthentication.SchemeName;
});
if (devUser is not null)
{
    authentication.AddScheme<AuthenticationSchemeOptions, DevAuthentication>(DevAuthentication.SchemeName, null);
    webSchemes = apiSchemes = [DevAuthentication.SchemeName];
}
else
{
    var authority = config["Auth:Authority"] is { Length: > 0 } a ? a
        : throw new InvalidOperationException("Auth:Authority is not set (Authentik's OpenID issuer URL).");
    var clientId = config["Auth:ClientId"] is { Length: > 0 } c ? c : throw new InvalidOperationException("Auth:ClientId is not set.");
    authentication
        .AddCookie(o =>
        {
            o.Cookie.Name = "tmspline";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.LoginPath = "/login";
            // The API answers 401 instead of sending the app to a login page.
            o.Events.OnRedirectToLogin = c =>
            {
                if (c.Request.Path.StartsWithSegments("/api")) c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else c.Response.Redirect(c.RedirectUri);
                return Task.CompletedTask;
            };
        })
        .AddOpenIdConnect("oidc", o =>
        {
            o.Authority = authority;
            o.ClientId = clientId;
            o.ClientSecret = config["Auth:ClientSecret"];
            o.ResponseType = "code";
            o.UsePkce = true;
            o.Scope.Clear();
            foreach (var scope in new[] { "openid", "profile", "email" }) o.Scope.Add(scope);
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = true;
            o.TokenValidationParameters.NameClaimType = "preferred_username";
            o.Events.OnTokenValidated = c =>
            {
                if (c.Principal is { } user) c.HttpContext.RequestServices.GetRequiredService<TrackStore>().Touch(Users.Id(user), Users.Name(user));
                return Task.CompletedTask;
            };
        })
        .AddJwtBearer(o =>
        {
            o.Authority = authority;
            o.Audience = clientId;
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "preferred_username";
        });
    webSchemes = [CookieAuthenticationDefaults.AuthenticationScheme];
    apiSchemes = [Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerDefaults.AuthenticationScheme, CookieAuthenticationDefaults.AuthenticationScheme];
}
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("web", p => p.AddAuthenticationSchemes(webSchemes).RequireAuthenticatedUser());
    o.AddPolicy("api", p => p.AddAuthenticationSchemes(apiSchemes).RequireAuthenticatedUser());
});

var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Styles, scripts and the background: public (the pages and everything with content need a login).
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

var pages = Path.Combine(builder.Environment.ContentRootPath, "Pages");
IResult Page(string file) => Results.File(Path.Combine(pages, file), "text/html; charset=utf-8");

// ---- website -------------------------------------------------------------------------------------------------------
app.MapGet("/", () => Page("index.html")).RequireAuthorization("web");
app.MapGet("/s/{share}", (string share) => Page("share.html")).RequireAuthorization("web");
// Public, like the law wants them: reachable without an account.
app.MapGet("/impressum", (IOptionsMonitor<SiteOperator> site) => Results.Content(LegalPages.Impressum(site.CurrentValue), "text/html; charset=utf-8"));
app.MapGet("/datenschutz", (IOptionsMonitor<SiteOperator> site) => Results.Content(LegalPages.Datenschutz(site.CurrentValue), "text/html; charset=utf-8"));
if (!app.Services.GetRequiredService<IOptionsMonitor<SiteOperator>>().CurrentValue.Complete)
    app.Logger.LogWarning("Site:Operator is incomplete ({File}): the Impressum shows no operator", siteFile);
app.MapGet("/login", (string? returnUrl) => Results.Challenge(
    new AuthenticationProperties { RedirectUri = returnUrl is { } r && r.StartsWith('/') && !r.StartsWith("//") ? r : "/" }));
app.MapPost("/logout", async (HttpContext c) =>
{
    await c.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/");
}).RequireAuthorization("web");
app.MapGet("/download/{file}", (string file, Releases releases) =>
    releases.PathOf(file) is { } path ? Results.File(path, "application/octet-stream", file) : Results.NotFound())
    .RequireAuthorization("api");

// ---- API -----------------------------------------------------------------------------------------------------------
// Public: how the desktop app signs in (the same Authentik application as the website).
app.MapGet("/api/client", () => devUser is not null
    ? Results.Ok(new { dev = true })
    : Results.Ok(new { authority = config["Auth:Authority"], clientId = config["Auth:ClientId"] }));

var api = app.MapGroup("/api").RequireAuthorization("api");
// Requests with the login cookie that change something must come from TMSpline's own pages (not another site's form).
api.AddEndpointFilter(async (context, next) =>
{
    var request = context.HttpContext.Request;
    if (!HttpMethods.IsGet(request.Method) && !request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && request.Headers["X-TMSpline"] != "1")
        return Results.Problem("Missing X-TMSpline header.", statusCode: StatusCodes.Status400BadRequest);
    var user = context.HttpContext.User;
    context.HttpContext.RequestServices.GetRequiredService<TrackStore>().Touch(Users.Id(user), Users.Name(user));
    return await next(context);
});

api.MapGet("/me", (ClaimsPrincipal user) => new { id = Users.Id(user), name = Users.Name(user) });
api.MapGet("/app/latest", (Releases releases) => releases.Latest() is { } r ? Results.Ok(r) : Results.NoContent());

api.MapGet("/tracks", (ClaimsPrincipal user, TrackStore store) => store.List(Users.Id(user)));
api.MapGet("/tracks/{id}", (string id, ClaimsPrincipal user, TrackStore store) =>
    store.Get(Users.Id(user), id) is { } t
        ? Results.Text($$"""{"id":{{Json(t.Id)}},"name":{{Json(t.Name)}},"version":{{t.Version}},"updated":{{Json(t.Updated)}},"share":{{Json(t.Share)}},"data":{{t.Data}}}""",
            "application/json")
        : Results.NotFound());
api.MapPut("/tracks/{id}", (string id, SaveTrackRequest body, ClaimsPrincipal user, TrackStore store) =>
{
    if (!TrackIds.IsValid(id)) return Results.Problem("Invalid track id.", statusCode: StatusCodes.Status400BadRequest);
    if (body.Data.ValueKind != JsonValueKind.Object) return Results.Problem("data must be a track file (a JSON object).", statusCode: StatusCodes.Status400BadRequest);
    var data = body.Data.GetRawText();
    if (data.Length > maxTrackBytes) return Results.Problem("The track is too large.", statusCode: StatusCodes.Status413PayloadTooLarge);
    var name = (body.Name ?? "").Trim();
    if (name.Length is 0 or > 120) return Results.Problem("The track needs a name (at most 120 characters).", statusCode: StatusCodes.Status400BadRequest);
    var owner = Users.Id(user);
    if (body.BaseVersion is null && store.Get(owner, id) is null && store.Count(owner) >= maxTracks)
        return Results.Problem($"At most {maxTracks} tracks per user.", statusCode: StatusCodes.Status409Conflict);
    return store.Save(owner, id, name, data, body.BaseVersion) switch
    {
        null => Results.NotFound(),
        { Saved: true } saved => Results.Ok(saved.Track),
        // Saved elsewhere since the client loaded it: the client decides which version to keep.
        var conflict => Results.Conflict(conflict.Track),
    };
});
api.MapDelete("/tracks/{id}", (string id, ClaimsPrincipal user, TrackStore store) =>
    store.Delete(Users.Id(user), id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/tracks/{id}/share", (string id, ClaimsPrincipal user, TrackStore store, HttpRequest request) =>
    store.Share(Users.Id(user), id) is { } share
        ? Results.Ok(new { share, url = $"{request.Scheme}://{request.Host}/s/{share}" })
        : Results.NotFound());
api.MapDelete("/tracks/{id}/share", (string id, ClaimsPrincipal user, TrackStore store) =>
    store.Unshare(Users.Id(user), id) ? Results.NoContent() : Results.NotFound());
api.MapGet("/shared/{share}", (string share, TrackStore store) =>
    store.Shared(share) is { } t
        ? Results.Text($$"""{"share":{{Json(t.Share)}},"name":{{Json(t.Name)}},"owner":{{Json(t.Owner)}},"updated":{{Json(t.Updated)}},"data":{{t.Data}}}""",
            "application/json")
        : Results.NotFound());

app.Run();

static string Json<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

/// <summary>PUT /api/tracks/{id}: the track file, its name and the version the client last loaded (null: new or overwrite).</summary>
internal sealed record SaveTrackRequest(string? Name, JsonElement Data, long? BaseVersion);

internal static partial class TrackIds
{
    /// <summary>Ids are made by the client (UUIDs), so a track can be saved before it was ever online.</summary>
    public static bool IsValid(string id) => Pattern().IsMatch(id);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,64}$")]
    private static partial Regex Pattern();
}

internal static class Users
{
    /// <summary>Authentik's subject id: stable for the account.</summary>
    public static string Id(ClaimsPrincipal user) => user.FindFirstValue("sub") ?? throw new InvalidOperationException("No sub claim.");

    public static string Name(ClaimsPrincipal user) =>
        user.FindFirstValue("preferred_username") ?? user.FindFirstValue("name") ?? user.FindFirstValue("nickname") ?? Id(user);
}

/// <summary>Development only (Auth:DevUser): every request is signed in as that user, no Authentik needed.</summary>
internal sealed class DevAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "dev";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var name = configuration["Auth:DevUser"]!;
        // X-Dev-User switches users (to try sharing between two accounts).
        if (Request.Headers["X-Dev-User"] is { Count: > 0 } other) name = other.ToString();
        var identity = new ClaimsIdentity([new Claim("sub", "dev-" + name), new Claim("preferred_username", name)], SchemeName, "preferred_username", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
