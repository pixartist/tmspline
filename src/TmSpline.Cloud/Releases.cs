using System.Security.Cryptography;
using System.Text.Json;

namespace TmSpline.Cloud;

/// <summary>A downloadable file of the current release.</summary>
public sealed record ReleaseFile(string Name, long Size, string Sha256, string Url);

/// <summary>The current release (GET /api/app/latest): the desktop app checks it for updates, the website links it.</summary>
public sealed record Release(string Version, ReleaseFile? App, ReleaseFile? Plugin, string? Notes, DateTimeOffset? Published);

/// <summary>
/// The releases folder (Cloud:ReleasesDir): the app and plugin files plus latest.json, which names the current ones:
/// <c>{ "version": "0.1.0", "app": "TMSpline-0.1.0-win-x64.zip", "plugin": "TMSpline.op", "notes": "…" }</c>.
/// Hashes are computed once per file change.
/// </summary>
public sealed class Releases(string dir)
{
    private readonly object _lock = new();
    private (DateTime Stamp, Release? Release) _cache = (DateTime.MinValue, null);
    private readonly Dictionary<string, (DateTime Stamp, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Manifest(string? Version, string? App, string? Plugin, string? Notes, DateTimeOffset? Published);

    public string Dir { get; } = Path.GetFullPath(dir);

    public Release? Latest()
    {
        var manifest = Path.Combine(Dir, "latest.json");
        if (!File.Exists(manifest)) return null;
        var stamp = new[] { manifest }.Concat(Directory.EnumerateFiles(Dir)).Max(File.GetLastWriteTimeUtc);
        lock (_lock)
        {
            if (stamp == _cache.Stamp) return _cache.Release;
            var m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var release = m?.Version is { Length: > 0 } version
                ? new Release(version, Describe(m.App), Describe(m.Plugin), m.Notes, m.Published)
                : null;
            _cache = (stamp, release);
            return release;
        }
    }

    /// <summary>The file's full path when it is one of the current release's files (nothing else is served).</summary>
    public string? PathOf(string name)
    {
        var release = Latest();
        if (release is null || (release.App?.Name != name && release.Plugin?.Name != name)) return null;
        var path = Path.Combine(Dir, name);
        return File.Exists(path) ? path : null;
    }

    private ReleaseFile? Describe(string? name)
    {
        if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) return null;
        var path = Path.Combine(Dir, name);
        if (!File.Exists(path)) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        if (!_hashes.TryGetValue(name, out var known) || known.Stamp != stamp)
        {
            using var stream = File.OpenRead(path);
            known = (stamp, Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
            _hashes[name] = known;
        }
        return new ReleaseFile(name, new FileInfo(path).Length, known.Hash, "/download/" + Uri.EscapeDataString(name));
    }
}
