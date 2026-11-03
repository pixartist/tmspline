using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TmSpline.Export;

namespace TmSpline.Server;

/// <summary>Whether NadeoImporter is there (GET /api/importer) and, in the desktop app, how far its download is.</summary>
/// <param name="CanInstall">The server may download it (Export:ImporterDownload, the desktop app).</param>
/// <param name="Size">Size of the download, bytes.</param>
public sealed record ImporterStatus(bool Installed, bool CanInstall, bool Installing, long Received, long Size, string? Error,
    string Source);

/// <summary>
/// Downloads NadeoImporter from Nadeo into Export:ImporterDir when the user asks for it (desktop app only, where the
/// importer runs on the user's own machine). Nadeo's zip is fetched from their CDN, checked against the known hash and
/// unpacked; nothing of it is ever bundled with TMSpline.
/// </summary>
public sealed class ImporterSetup
{
    /// <summary>Nadeo's download, as linked from doc.trackmania.com (the version the exporter is built against).</summary>
    public const string DownloadUrl = "https://nadeo-download.cdn.ubi.com/trackmania/NadeoImporter_2022_07_12.zip";
    private const string Sha256 = "fb2aebc15c4c055e7d2da1f5c2b8595eebbcd1680389d07319ea078d2fd157fa";
    private const long DownloadSize = 6_525_624;

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly ILogger<ImporterSetup> _logger;
    private bool _installing;
    private long _received;
    private string? _error;

    public ImporterSetup(IOptions<ExportSettings> options, ILogger<ImporterSetup> logger)
    {
        _dir = options.Value.ImporterDir;
        CanInstall = options.Value.ImporterDownload;
        _logger = logger;
    }

    public bool CanInstall { get; }

    public ImporterStatus Status
    {
        get
        {
            lock (_lock)
                return new ImporterStatus(ImporterFiles.IsPresent(_dir), CanInstall, _installing, _received, DownloadSize, _error,
                    new Uri(DownloadUrl).Host);
        }
    }

    /// <summary>Starts the download unless it runs or the importer is there; false when nothing was started.</summary>
    public bool Start()
    {
        lock (_lock)
        {
            if (!CanInstall) throw new InvalidOperationException("Downloading NadeoImporter is not enabled.");
            if (_installing || ImporterFiles.IsPresent(_dir)) return false;
            _installing = true;
            _received = 0;
            _error = null;
        }
        _ = Task.Run(Install);
        return true;
    }

    private async Task Install()
    {
        var staging = _dir.TrimEnd('\\', '/') + ".download";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "NadeoImporter.zip");

            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync();
                await using var target = File.Create(zip);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read));
                    lock (_lock) _received += read;
                    if (_received > DownloadSize * 2) throw new InvalidDataException("The download is larger than expected.");
                }
            }

            await using (var check = File.OpenRead(zip))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check)).ToLowerInvariant();
                if (hash != Sha256) throw new InvalidDataException("The download does not match the known NadeoImporter (hash differs).");
            }

            // Only the files the exporter runs, from the zip's single folder (NadeoImporter_2022_07_12/...).
            using (var archive = ZipFile.OpenRead(zip))
            {
                var unpacked = Path.Combine(staging, "files");
                Directory.CreateDirectory(unpacked);
                foreach (var name in ImporterFiles.Required)
                {
                    var entry = archive.Entries.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{name} is missing in the download.");
                    entry.ExtractToFile(Path.Combine(unpacked, name), overwrite: true);
                }
                Directory.CreateDirectory(_dir);
                foreach (var name in ImporterFiles.Required)
                    File.Move(Path.Combine(unpacked, name), Path.Combine(_dir, name), overwrite: true);
            }
            _logger.LogInformation("NadeoImporter installed into {Dir}", _dir);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Installing NadeoImporter failed");
            lock (_lock) _error = e.Message;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
            catch (IOException)
            {
                // left for the next attempt, which deletes it first
            }
            lock (_lock) _installing = false;
        }
    }
}
