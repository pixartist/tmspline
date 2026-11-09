using System.Diagnostics;

namespace TmSpline.Server;

/// <summary>Where a block library conversion stands (GET /api/vanilla/build).</summary>
/// <param name="Missing">Files the conversion needs but did not find, as game paths ("GameData/..."): the plugin extracts them.</param>
public sealed record VanillaBuildStatus(bool Running, string? Progress, string? Error, int Blocks, IReadOnlyList<string> Missing,
    DateTimeOffset? FinishedAt);

/// <summary>
/// Converts the game files the TMSpline Openplanet plugin extracted (OpenplanetNext\Extract) into the editor's block
/// library (Vanilla:Dir) by running tools/VanillaLib, on the plugin's request. Local setups only: it needs the game's files
/// on this machine and either the repository (Vanilla:Builder = the VanillaLib project, run with dotnet) or the desktop
/// app, which carries VanillaLib inside (Vanilla:Builder = "self": the app's own executable with --vanilla-lib).
/// </summary>
public sealed class VanillaBuilder
{
    private readonly object _lock = new();
    private readonly ILogger<VanillaBuilder> _logger;
    private VanillaBuildStatus _status = new(false, null, null, 0, [], null);

    public VanillaBuilder(IConfiguration configuration, IWebHostEnvironment environment, ILogger<VanillaBuilder> logger)
    {
        _logger = logger;
        if (configuration["Vanilla:Dir"] is not { Length: > 0 } dir || configuration["Vanilla:Builder"] is not { Length: > 0 } builder)
            return;
        var project = builder == SelfBuilder ? SelfBuilder : Path.GetFullPath(builder, environment.ContentRootPath);
        if (project != SelfBuilder && !Directory.Exists(project) && !File.Exists(project))
        {
            logger.LogWarning("Block library builder {Project} not found; building the library from the plugin is off", project);
            return;
        }
        Project = project;
        OutDir = Path.GetFullPath(dir, environment.ContentRootPath);
        ExtractDir = configuration["Vanilla:ExtractDir"] is { Length: > 0 } extract
            ? Path.GetFullPath(extract, environment.ContentRootPath)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenplanetNext", "Extract");
    }

    /// <summary>Vanilla:Builder value for the desktop app: VanillaLib runs in its own executable.</summary>
    public const string SelfBuilder = "self";
    /// <summary>The command-line switch the desktop app runs VanillaLib with.</summary>
    public const string SelfSwitch = "--vanilla-lib";

    public string? Project { get; }
    public string? OutDir { get; }
    public string? ExtractDir { get; }
    public bool Enabled => Project is not null;

    public VanillaBuildStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Starts a conversion unless one runs; false when one already runs.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (!Enabled) throw new InvalidOperationException("Building the block library is not configured.");
            if (_status.Running) return false;
            _status = _status with { Running = true, Progress = "starting", Error = null };
        }
        _ = Task.Run(Run);
        return true;
    }

    private async Task Run()
    {
        var started = DateTime.UtcNow;
        try
        {
            Directory.CreateDirectory(OutDir!);
            var self = Project == SelfBuilder;
            var info = new ProcessStartInfo(self ? Environment.ProcessPath! : "dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in self ? [SelfSwitch, ExtractDir!, OutDir!] : new[] { "run", "-c", "Release", "--project", Project!, "--", ExtractDir!, OutDir! })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start dotnet");
            var errors = new List<string>();
            var stderr = Task.Run(async () =>
            {
                while (await process.StandardError.ReadLineAsync() is { } line) lock (errors) errors.Add(line);
            });
            string? last = null, summary = null;
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                last = line;
                if (line.StartsWith("Wrote ", StringComparison.Ordinal)) summary = line;
                lock (_lock) _status = _status with { Progress = line.Trim() };
                _logger.LogDebug("VanillaLib: {Line}", line);
            }
            await process.WaitForExitAsync();
            await stderr;
            if (process.ExitCode != 0)
            {
                // A crash prints its cause and the crashing frames first; what follows is the thread pool's own frames.
                _logger.LogError("VanillaLib exited with 0x{Code:X8} after \"{Last}\":{NewLine}{Errors}", process.ExitCode, last, Environment.NewLine, string.Join(Environment.NewLine, errors));
                throw new InvalidOperationException($"VanillaLib exited with 0x{process.ExitCode:X8} after \"{last?.Trim()}\":\n{string.Join("\n", errors.Select(e => e.Trim()).Where(e => !e.StartsWith("---", StringComparison.Ordinal) && !e.StartsWith("Repeat ", StringComparison.Ordinal)).Take(6))}");
            }

            var missing = ReadMissing();
            var blocks = CountBlocks(summary);
            _logger.LogInformation("Block library converted in {Seconds:0} s: {Blocks} blocks, {Missing} files missing",
                (DateTime.UtcNow - started).TotalSeconds, blocks, missing.Count);
            lock (_lock) _status = new VanillaBuildStatus(false, null, null, blocks, missing, DateTimeOffset.UtcNow);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Block library conversion failed");
            lock (_lock) _status = new VanillaBuildStatus(false, null, e.Message, 0, [], DateTimeOffset.UtcNow);
        }
    }

    /// <summary>VanillaLib's list of referenced files it did not find, as game paths below the extract folder.</summary>
    private List<string> ReadMissing()
    {
        var file = Path.Combine(OutDir!, "missing-files.txt");
        if (!File.Exists(file)) return [];
        return File.ReadAllLines(file).Where(l => l.Length > 0)
            .Select(l => Path.GetRelativePath(ExtractDir!, l).Replace('\\', '/'))
            .Where(l => !l.StartsWith("../", StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>The block count from VanillaLib's summary line ("Wrote â€¦: N blocks, â€¦").</summary>
    private static int CountBlocks(string? summary)
    {
        if (summary is null) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(summary, @"(\d+) blocks");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }
}
