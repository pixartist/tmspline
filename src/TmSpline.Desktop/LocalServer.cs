using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TmSpline.Server;

namespace TmSpline.Desktop;

/// <summary>Where the app keeps its files: %LOCALAPPDATA%\TMSpline on Windows, ~/.local/share/TMSpline elsewhere.</summary>
internal sealed record DesktopPaths(string Root)
{
    public string SettingsFile => Path.Combine(Root, "settings.json");
    /// <summary>NadeoImporter, downloaded from Nadeo on the user's request (never shipped with the app).</summary>
    public string Importer => Path.Combine(Root, "NadeoImporter");
    public string Jobs => Path.Combine(Root, "jobs");
    /// <summary>The block library converted from the user's own game files.</summary>
    public string Vanilla => Path.Combine(Root, "vanilla");
    public string GameState => Path.Combine(Root, "game");
    /// <summary>The webview's profile: the editor's local storage (autosaved tracks) is kept here.</summary>
    public string WebView => Path.Combine(Root, "webview");
    public string Logs => Path.Combine(Root, "logs");

    public static DesktopPaths Create()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create), "TMSpline");
        var paths = new DesktopPaths(root);
        foreach (var dir in new[] { root, paths.Jobs, paths.WebView, paths.Logs })
            Directory.CreateDirectory(dir);
        return paths;
    }
}

/// <summary>The user's settings.json (all optional).</summary>
internal sealed class DesktopSettings
{
    /// <summary>The local server's port: the Openplanet plugin's "Server URL" points here (localhost:5080 by default).</summary>
    public int Port { get; set; } = 5080;
    /// <summary>The webview's developer tools and context menu.</summary>
    public bool DevTools { get; set; }

    public static DesktopSettings Load(string file)
    {
        if (!File.Exists(file)) return new DesktopSettings();
        try
        {
            return JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
                ?? new DesktopSettings();
        }
        catch (JsonException)
        {
            return new DesktopSettings();
        }
    }
}

/// <summary>The TMSpline server as the desktop app runs it: localhost only, everything configured in code.</summary>
internal static class LocalServer
{
    public static WebApplication Build(DesktopPaths paths, DesktopSettings settings, string url)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Export:ImporterMode"] = OperatingSystem.IsWindows() ? "Native" : "Wine",
            ["Export:ImporterDir"] = paths.Importer,
            ["Export:ImporterDownload"] = "true",
            ["Export:WorkDir"] = paths.Jobs,
            ["Export:TemplatePath"] = "Templates/Empty48.Map.Gbx",
            // One user on their own machine: no queue limits to speak of.
            ["Export:MaxConcurrentJobs"] = "2",
            ["Export:MaxQueuedJobs"] = "8",
            ["Export:RequestsPerMinute"] = "600",
            ["Vanilla:Dir"] = paths.Vanilla,
            ["Vanilla:Builder"] = VanillaBuilder.SelfBuilder,
            ["Game:MapsDir"] = "auto",
            ["Game:StateDir"] = paths.GameState,
        });
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.Logs, "tmspline.log")));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        ServerApp.AddServices(builder);
        var app = builder.Build();
        ServerApp.Map(app);
        return app;
    }
}

/// <summary>Appends the log to one file (the app has no console); the previous run's log is kept as .1.</summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string file)
    {
        if (File.Exists(file)) File.Move(file, file + ".1", overwrite: true);
        _writer = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose() => _writer.Dispose();

    private void Write(string line)
    {
        lock (_lock) _writer.WriteLine(line);
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {logLevel,-11} {category}: {formatter(state, exception)}";
            if (exception is not null) line += Environment.NewLine + exception;
            provider.Write(line);
        }
    }
}
