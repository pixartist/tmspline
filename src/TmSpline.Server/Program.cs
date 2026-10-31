using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using TmSpline.Export;
using TmSpline.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ExportSettings>(builder.Configuration.GetSection("Export"));
builder.Services.AddSingleton<ExportService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();

var requestsPerMinute = builder.Configuration.GetValue("Export:RequestsPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("export", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Estimates only build meshes (no importer), and the export dialog asks for one each time it opens.
    o.AddPolicy("estimate", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute * 3, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();
app.UseRateLimiter();

// In production the built editor (web/dist) is served from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

// Vanilla block meshes for the editor preview (tools/VanillaLib output). These are the game's own assets, extracted
// locally: only served when a folder is configured, which is the case in appsettings.Development.json only.
if (builder.Configuration["Vanilla:Dir"] is { Length: > 0 } vanillaDir
    && Path.GetFullPath(vanillaDir, builder.Environment.ContentRootPath) is var vanillaPath && Directory.Exists(vanillaPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(vanillaPath),
        RequestPath = "/api/vanilla",
        ServeUnknownFileTypes = true,
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
    });
    app.Logger.LogInformation("Serving vanilla block meshes from {Path}", vanillaPath);
}

// Create the service at startup so configuration errors (missing importer, bad mode) show up immediately.
var exports = app.Services.GetRequiredService<ExportService>();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", importer = exports.ImporterMode }));

app.MapPost("/api/export", async (ExportRequestDto request, ExportService service, CancellationToken ct) =>
{
    try
    {
        var map = await service.ExportAsync(request, ct);
        return Results.File(map.Data, "application/octet-stream", map.FileName);
    }
    catch (ExportRejectedException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid track");
    }
    catch (ServerBusyException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Busy");
    }
    catch (Exception e) when (e is ImporterException or TimeoutException)
    {
        return Results.Problem("Generating the road items failed. Please try again or report the track.",
            statusCode: StatusCodes.Status500InternalServerError, title: "Export failed");
    }
})
.RequireRateLimiting("export")
.DisableAntiforgery();

app.MapPost("/api/estimate", (ExportRequestDto request, ExportService service) =>
{
    try
    {
        return Results.Ok(service.Estimate(request));
    }
    catch (ExportRejectedException e)
    {
        return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid track");
    }
})
.RequireRateLimiting("estimate")
.DisableAntiforgery();

app.Run();
