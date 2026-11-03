using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using TmSpline.Export;

namespace TmSpline.Server;

/// <summary>
/// The TMSpline server's services and endpoints: used by the server itself (Program.cs) and hosted by the desktop app,
/// which runs it on the user's machine.
/// </summary>
public static class ServerApp
{
    public static void AddServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<ExportSettings>(builder.Configuration.GetSection("Export"));
        builder.Services.AddSingleton<ExportService>();
        builder.Services.AddSingleton<GameMaps>();
        builder.Services.AddSingleton<VanillaBuilder>();
        builder.Services.AddSingleton<ImporterSetup>();
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
    }

    public static void Map(WebApplication app)
    {
        app.UseRateLimiter();

        // In production the built editor (web/dist) is served from wwwroot.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Vanilla block meshes for the editor preview (tools/VanillaLib output). These are the game's own assets, extracted
        // locally: only served when a folder is configured, which is the case in appsettings.Development.json only.
        if (app.Configuration["Vanilla:Dir"] is { Length: > 0 } vanillaDir
            && Path.GetFullPath(vanillaDir, app.Environment.ContentRootPath) is var vanillaPath
            // With the builder on, the folder is made here so a library built later (by the plugin) is served without a restart.
            && (Directory.Exists(vanillaPath) || app.Configuration["Vanilla:Builder"] is { Length: > 0 } && Directory.CreateDirectory(vanillaPath).Exists))
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
        var gameMaps = app.Services.GetRequiredService<GameMaps>();

        var importerSetup = app.Services.GetRequiredService<ImporterSetup>();
        app.MapGet("/api/health", () => Results.Ok(new
        {
            status = "ok",
            importer = exports.ImporterMode,
            importerInstalled = importerSetup.Status.Installed,
            exportToGame = gameMaps.MapsDir is not null,
        }));

        // NadeoImporter, and in the desktop app its download from Nadeo (on the user's request).
        app.MapGet("/api/importer", () => Results.Ok(importerSetup.Status));
        if (importerSetup.CanInstall)
            app.MapPost("/api/importer/install", () => importerSetup.Start() ? Results.Accepted("/api/importer") : Results.Ok(importerSetup.Status))
                .DisableAntiforgery();

        app.MapPost("/api/export", (ExportRequestDto request, ExportService service, CancellationToken ct) =>
            Export(request, service, ct, map => Results.File(map.Data, "application/octet-stream", map.FileName)))
        .RequireRateLimiting("export")
        .DisableAntiforgery();

        // Local setups only (Game:MapsDir): the map goes straight into the game's Maps folder, where the TMSplineLive plugin
        // picks it up and reloads the editor.
        if (gameMaps.MapsDir is not null)
        {
            app.MapPost("/api/export/game", (ExportRequestDto request, ExportService service, CancellationToken ct) =>
                Export(request, service, ct, map =>
                {
                    try
                    {
                        return Results.Ok(gameMaps.Add(map.FileName, map.Data, map.Result.ItemFolder));
                    }
                    catch (IOException e)
                    {
                        return Results.Problem($"Could not write the map into the game's folder: {e.Message}",
                            statusCode: StatusCodes.Status500InternalServerError, title: "Export failed");
                    }
                }))
            .RequireRateLimiting("export")
            .DisableAntiforgery();

            app.MapGet("/api/game/latest", () => gameMaps.Poll() is { } latest ? Results.Ok(latest) : Results.NoContent());

            // The plugin saved the open map: merge the waiting export into it.
            app.MapPost("/api/game/apply", (string id) =>
            {
                try
                {
                    return gameMaps.Apply(id) is { } applied ? Results.Ok(applied) : Results.NotFound();
                }
                catch (IOException e)
                {
                    return Results.Problem($"Could not write the map into the game's folder: {e.Message}",
                        statusCode: StatusCodes.Status500InternalServerError, title: "Apply failed");
                }
            })
            .DisableAntiforgery();
        }

        // Local setups only (Vanilla:Builder): the TMSpline Openplanet plugin extracts the game's block files and has them
        // converted into the editor's block library here.
        var vanillaBuilder = app.Services.GetRequiredService<VanillaBuilder>();
        if (vanillaBuilder.Enabled)
        {
            app.MapGet("/api/vanilla/build", () => Results.Ok(vanillaBuilder.Status));
            app.MapPost("/api/vanilla/build", () => vanillaBuilder.Start() ? Results.Accepted("/api/vanilla/build") : Results.Ok(vanillaBuilder.Status))
                .DisableAntiforgery();
        }

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
    }

    private static async Task<IResult> Export(ExportRequestDto request, ExportService service, CancellationToken ct, Func<ExportedMap, IResult> deliver)
    {
        try
        {
            return deliver(await service.ExportAsync(request, ct));
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
    }
}
