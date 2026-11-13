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
        builder.Services.AddSingleton<CloudLink>();
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
            // The brush's dabs while painting: several a second.
            o.AddPolicy("dab", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1) }));
            // The terrain preview: the editor asks for one after generating and after each painted stroke.
            o.AddPolicy("terrain", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = requestsPerMinute * 10, Window = TimeSpan.FromMinutes(1) }));
        });
    }

    public static void Map(WebApplication app)
    {
        app.UseRateLimiter();

        // In production the built editor (web/dist) is served from wwwroot.
        // Vite fingerprints the files in assets/ (a new build gets new names), so those may be cached for good. Everything
        // else (index.html, the preview core in _framework) keeps its name across builds: the browser has to revalidate
        // it, or it keeps an old index.html that loads an old editor against the new core.
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
                c.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        });

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
            // The plugin can have the block library built here; whether one exists yet.
            blockLibraryBuilder = app.Services.GetRequiredService<VanillaBuilder>().Enabled,
            blockLibrary = app.Services.GetRequiredService<VanillaBuilder>().OutDir is { } dir && File.Exists(Path.Combine(dir, "index.json")),
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

            // Polled once a second by the plugin. Each request comes on a new connection and the game keeps the socket for as
            // long as it runs unless the response closes the connection: left open, the machine runs out of ports within hours.
            app.MapGet("/api/game/latest", (HttpContext context) =>
            {
                context.Response.Headers.Connection = "close";
                return gameMaps.Poll() is { } latest ? Results.Ok(latest) : Results.NoContent();
            });

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

        // The map as a 3D model (meshes only, no importer).
        app.MapPost("/api/export/model", (ExportRequestDto request, ExportService service, HttpContext _) =>
        {
            try
            {
                var (data, fileName) = service.Model(request);
                // The editor gets a ticket and fetches the file as a plain download (see ExportService.Keep).
                return Results.Ok(new { ticket = service.Keep(data, fileName), fileName, bytes = data.Length });
            }
            catch (ExportRejectedException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid track");
            }
        })
        .RequireRateLimiting("estimate")
        .DisableAntiforgery();

        app.MapGet("/api/export/model/{ticket}", (string ticket, ExportService service, HttpContext context) =>
        {
            if (service.Kept(ticket) is not { } file) return Results.NotFound();
            // How the download ends goes into the log: the file can be large.
            var aborted = context.RequestAborted;
            context.Response.OnCompleted(() => { service.ModelSent(file.FileName, file.Data.Length, aborted.IsCancellationRequested); return Task.CompletedTask; });
            return Results.File(file.Data, "application/octet-stream", file.FileName);
        })
        .RequireRateLimiting("estimate");

        // A download that failed in the editor (it cannot write the log itself).
        app.MapPost("/api/export/failed", (ExportService.ClientFailureDto report, ExportService service) =>
        {
            service.ClientFailure(report);
            return Results.NoContent();
        })
        .RequireRateLimiting("estimate")
        .DisableAntiforgery();

        // The own terrain's preview mesh, built natively (the editor's WebAssembly core takes many times as long).
        app.MapPost("/api/terrain", (ExportRequestDto request, string? key, ExportService service) =>
        {
            try
            {
                // Development: the last terrain request's track written to this file (TMSPLINE_DUMP_TERRAIN), for MesherLab.
                if (app.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("TMSPLINE_DUMP_TERRAIN") is { Length: > 0 } dump)
                    File.WriteAllText(dump, System.Text.Json.JsonSerializer.Serialize(request.Track, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
                return Results.Bytes(service.TerrainPreview(request, key), "application/octet-stream");
            }
            catch (ExportRejectedException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid track");
            }
        })
        .RequireRateLimiting("terrain")
        .DisableAntiforgery();

        // The brush's dabs, built into the terrain last asked for under the key: only the chunks they reach come back.
        // Development: the seam's front edges the last terrain build left open (the editor draws them with the mesh view).
        if (app.Environment.IsDevelopment()) app.MapGet("/api/terrain/open", () => Results.Ok(TmSpline.Core.Geometry.SeamZipper.LastOpen));

        app.MapPost("/api/terrain/dab", (ExportService.TerrainDabDto request, ExportService service) =>
        {
            try
            {
                var bytes = service.TerrainDab(request.Key ?? "", request.Dabs ?? [], request.Over, request.Smooth);
                return bytes is null ? Results.NotFound() : Results.Bytes(bytes, "application/octet-stream");
            }
            catch (ExportRejectedException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid dabs");
            }
        })
        .RequireRateLimiting("dab")
        .DisableAntiforgery();

        MapCloud(app);
    }

    /// <summary>
    /// The link to TMSpline's hosted service (Cloud:Url, the desktop app): sign-in, and the editor's cloud requests,
    /// forwarded with the user's token. Requests need the header X-TMSpline: 1, which other websites cannot send to
    /// localhost without a CORS preflight this server never allows.
    /// </summary>
    private static void MapCloud(WebApplication app)
    {
        var cloud = app.Services.GetRequiredService<CloudLink>();
        if (!cloud.Enabled) return;

        var group = app.MapGroup("/api/cloud").DisableAntiforgery();
        group.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers["X-TMSpline"] == "1"
                ? await next(context)
                : Results.Problem("Missing X-TMSpline header.", statusCode: StatusCodes.Status400BadRequest));

        group.MapGet("", () => cloud.Status);
        group.MapPost("/login", async (HttpContext c, CancellationToken ct) =>
        {
            try
            {
                await cloud.StartLogin($"http://127.0.0.1:{c.Connection.LocalPort}/callback", ct);
                return Results.Ok(cloud.Status);
            }
            catch (HttpRequestException e)
            {
                return Results.Problem($"TMSpline's server is not reachable: {e.Message}", statusCode: StatusCodes.Status502BadGateway);
            }
        });
        group.MapPost("/logout", () =>
        {
            cloud.SignOut();
            return Results.Ok(cloud.Status);
        });
        group.MapPost("/open", (string path) =>
        {
            cloud.OpenPage(path);
            return Results.NoContent();
        });

        // The service's API, for the paths the editor uses.
        var allowed = new System.Text.RegularExpressions.Regex(@"^(me|app/latest|tracks|tracks/[A-Za-z0-9_-]{8,64}(/share)?|shared/[a-z0-9]{6,32})$");
        group.MapMethods("/api/{**path}", ["GET", "PUT", "POST", "DELETE"], async (string path, HttpContext c, CancellationToken ct) =>
        {
            if (!allowed.IsMatch(path)) return Results.NotFound();
            byte[]? body = null;
            if (c.Request.ContentLength > 0 || c.Request.Headers.TransferEncoding.Count > 0)
            {
                using var buffer = new MemoryStream();
                await c.Request.Body.CopyToAsync(buffer, ct);
                body = buffer.ToArray();
            }
            try
            {
                using var response = await cloud.Send(new HttpMethod(c.Request.Method), path + c.Request.QueryString, body, c.Request.ContentType, ct);
                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                c.Response.StatusCode = (int)response.StatusCode;
                if (bytes.Length > 0)
                {
                    c.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await c.Response.Body.WriteAsync(bytes, ct);
                }
                return Results.Empty;
            }
            catch (HttpRequestException e)
            {
                return Results.Problem($"TMSpline's server is not reachable: {e.Message}", statusCode: StatusCodes.Status502BadGateway, title: "Offline");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return Results.Problem("TMSpline's server did not answer in time.", statusCode: StatusCodes.Status504GatewayTimeout, title: "Offline");
            }
        });

        // Authentik sends the browser back here after the login.
        app.MapGet("/callback", async (string? code, string? state, string? error, CancellationToken ct) =>
        {
            var problem = await cloud.CompleteLogin(code, state, error, ct);
            var (title, text) = problem is null
                ? ("Signed in", "You are signed in to TMSpline. You can close this tab and go back to the app.")
                : ("Not signed in", problem);
            return Results.Content($$"""
                <!doctype html><html lang="en"><head><meta charset="utf-8" /><title>TMSpline · {{title}}</title>
                <style>body{font:16px system-ui,sans-serif;background:#0d1020;color:#eef0f6;display:grid;place-items:center;height:100vh;margin:0}
                main{max-width:480px;padding:28px 32px;border-radius:14px;background:#171a33;border:1px solid #ff8a3d55}h1{margin:0 0 8px;font-size:22px}</style>
                </head><body><main><h1>{{title}}</h1><p>{{System.Net.WebUtility.HtmlEncode(text)}}</p></main></body></html>
                """, "text/html; charset=utf-8");
        });
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
