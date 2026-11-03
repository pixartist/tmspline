using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using TmSpline.Server;

namespace TmSpline.Desktop;

/// <summary>GET /api/update: whether the app can update itself and how far an update is.</summary>
internal sealed record UpdateStatus(bool Supported, string State, string? Version, long Received, long Size, string? Error);

/// <summary>
/// Updates the app in place: downloads the current release's zip from TMSpline's service (signed in), checks it against
/// the size and SHA-256 the service lists, unpacks it, and on the user's go closes the app, copies the new files over
/// the old ones (a small PowerShell script, as the running files cannot be replaced) and starts it again. Files the app
/// downloads itself carry no "downloaded from the internet" mark, so Windows' SmartScreen only asks on the first install.
/// </summary>
internal sealed class Updater(DesktopPaths paths, CloudLink cloud, string currentVersion, ILogger<Updater> logger)
{
    private readonly object _lock = new();
    private string _state = "idle";
    private string? _version;
    private long _received, _size;
    private string? _error;

    /// <summary>Closes the app's window (set by Program once the window exists).</summary>
    public static Action? RequestClose { get; set; }

    private string Dir => Path.Combine(paths.Root, "update");
    private string Unpacked => Path.Combine(Dir, "app");

    /// <summary>Not for development builds (they would overwrite bin/), and Windows only (the swap script).</summary>
    public bool Supported =>
#if DEBUG
        false;
#else
        OperatingSystem.IsWindows() && cloud.Enabled;
#endif

    public UpdateStatus Status
    {
        get { lock (_lock) return new UpdateStatus(Supported, _state, _version, _received, _size, _error); }
    }

    private sealed record Latest(string Version, LatestFile? App);
    private sealed record LatestFile(string Name, long Size, string Sha256, string Url);

    public bool Start()
    {
        lock (_lock)
        {
            if (!Supported || _state is "downloading" or "ready") return false;
            (_state, _received, _size, _error) = ("downloading", 0, 0, null);
        }
        _ = Task.Run(Download);
        return true;
    }

    private async Task Download()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            Latest latest;
            using (var response = await cloud.Send(HttpMethod.Get, "app/latest", null, null, cts.Token))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"No release found ({(int)response.StatusCode}).");
                latest = JsonSerializer.Deserialize<Latest>(await response.Content.ReadAsStringAsync(cts.Token), new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidOperationException("Empty release.");
            }
            if (latest.App is not { } file) throw new InvalidOperationException("The release has no app download.");
            if (!file.Url.StartsWith("/download/", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected download address.");
            lock (_lock) (_version, _size) = (latest.Version, file.Size);

            if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
            Directory.CreateDirectory(Dir);
            var zip = Path.Combine(Dir, file.Name);
            using (var response = await cloud.Send(HttpMethod.Get, file.Url, null, null, cts.Token, streamed: true))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"The download failed ({(int)response.StatusCode}).");
                await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
                await using var target = File.Create(zip);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cts.Token)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                    hash.AppendData(buffer, 0, read);
                    long received;
                    lock (_lock) received = _received += read;
                    if (received > file.Size) throw new InvalidDataException("The download is larger than the release says.");
                }
                if (_received != file.Size) throw new InvalidDataException("The download is incomplete.");
                if (Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != file.Sha256)
                    throw new InvalidDataException("The download does not match the release (checksum differs).");
            }

            ZipFile.ExtractToDirectory(zip, Unpacked);
            File.Delete(zip);
            if (!File.Exists(Path.Combine(Unpacked, "TMSpline.exe"))) throw new InvalidDataException("The download holds no TMSpline.exe.");
            logger.LogInformation("Update {Version} downloaded and unpacked", latest.Version);
            lock (_lock) _state = "ready";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Downloading the update failed");
            lock (_lock) (_state, _error) = ("error", e.Message);
        }
    }

    /// <summary>Closes the app and lets the script copy the update over it and start it again.</summary>
    public string? Apply()
    {
        lock (_lock)
            if (_state != "ready") return "No update is ready.";
        var target = AppContext.BaseDirectory.TrimEnd('\\', '/');
        try
        {
            // The app's folder has to be writable (not e.g. Program Files without admin rights).
            var probe = Path.Combine(target, ".update-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"TMSpline cannot write to its folder ({target}). Unzip the new version yourself, or move TMSpline to a folder you own.";
        }

        var script = Path.Combine(Dir, "apply.ps1");
        File.WriteAllText(script, """
            param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log)
            function Log($m) { Add-Content -LiteralPath $Log -Value ("{0:u} {1}" -f (Get-Date), $m) }
            try { Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction Stop } catch { }
            # robocopy retries files that are still in use for a moment; exit codes below 8 mean success.
            robocopy $Source $Target /E /R:30 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -lt 8) { Log "updated $Target"; Remove-Item -LiteralPath $Source -Recurse -Force -ErrorAction SilentlyContinue }
            else { Log "update failed: robocopy exit code $LASTEXITCODE" }
            Start-Process -FilePath (Join-Path $Target 'TMSpline.exe')
            """);
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(), "-Source", Unpacked, "-Target", target,
                     "-Log", Path.Combine(paths.Logs, "update.log"),
                 })
            info.ArgumentList.Add(arg);
        Process.Start(info);
        logger.LogInformation("Updating to {Version}: closing", _version);
        RequestClose?.Invoke();
        return null;
    }

    public void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/update");
        group.AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers["X-TMSpline"] == "1"
                ? await next(context)
                : Results.Problem("Missing X-TMSpline header.", statusCode: StatusCodes.Status400BadRequest));
        group.MapGet("", () => Status);
        group.MapPost("/install", () =>
        {
            Start();
            return Results.Ok(Status);
        });
        group.MapPost("/apply", () => Apply() is { } problem
            ? Results.Problem(problem, statusCode: StatusCodes.Status409Conflict)
            : Results.Ok(Status));
        logger.LogInformation("App {Version}; updating itself: {Supported}", currentVersion, Supported);
    }
}
