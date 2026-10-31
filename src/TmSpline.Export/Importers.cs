using System.Diagnostics;
using System.Text;

namespace TmSpline.Export;

/// <summary>Runs NadeoImporter for item sources below <c>{userDir}\Work</c>.</summary>
public interface IImporter
{
    /// <param name="userDir">Trackmania user folder the importer reads Work\ from and writes Items\ to.</param>
    /// <param name="relativeXmlPattern">Path below Work\, e.g. <c>Items\TMSpline\X\*.Item.xml</c>.</param>
    /// <returns>The importer's console output.</returns>
    Task<string> RunAsync(string userDir, string relativeXmlPattern, CancellationToken ct = default);
}

public sealed class ImporterException(string message, string output) : Exception(message + (output.Length > 0 ? "\n" + output : ""))
{
    public string Output { get; } = output;
}

/// <summary>Helpers shared by the importer implementations.</summary>
internal static class ImporterFiles
{
    /// <summary>The files from the NadeoImporter zip that are needed to run it.</summary>
    public static readonly string[] Required = { "NadeoImporter.exe", "libfbxsdk.dll", "NadeoImporterMaterialLib.txt" };

    public static void Validate(string importerDir)
    {
        foreach (var f in Required)
            if (!File.Exists(Path.Combine(importerDir, f)))
                throw new FileNotFoundException($"NadeoImporter file missing in {importerDir}", f);
    }

    /// <summary>Copies the importer into <paramref name="target"/> with a Nadeo.ini that points at <paramref name="userDirForIni"/>.</summary>
    public static void Prepare(string importerDir, string target, string userDirForIni)
    {
        Directory.CreateDirectory(target);
        foreach (var f in Required)
            File.Copy(Path.Combine(importerDir, f), Path.Combine(target, f), overwrite: true);
        File.WriteAllText(Path.Combine(target, "Nadeo.ini"),
            $"[Trackmania]\r\nWindowTitle=Trackmania\r\nDistro=AZURO\r\nUserDir={userDirForIni}\r\nCommonDir={userDirForIni}\\..\\common\r\n",
            Encoding.ASCII);
    }

    public static async Task<string> RunProcessAsync(ProcessStartInfo psi, TimeSpan timeout, CancellationToken ct)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {psi.FileName}");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"NadeoImporter did not finish within {timeout.TotalSeconds:0} s");
        }

        var output = ((await stdout) + (await stderr)).Trim();
        if (process.ExitCode != 0)
            throw new ImporterException($"NadeoImporter failed (exit code {process.ExitCode})", output);
        return output;
    }
}

/// <summary>
/// Runs NadeoImporter.exe directly (Windows). With <c>isolated</c>, every run gets its own copy of the importer and a
/// Nadeo.ini pointing at the requested user folder; otherwise the importer runs in place and must already be
/// configured for that user folder (the normal case next to Trackmania.exe).
/// </summary>
public sealed class NativeImporter(string importerDir, bool isolated, TimeSpan? timeout = null) : IImporter
{
    public async Task<string> RunAsync(string userDir, string relativeXmlPattern, CancellationToken ct = default)
    {
        ImporterFiles.Validate(importerDir);
        var dir = importerDir;
        if (isolated)
        {
            dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(userDir))!, "importer");
            ImporterFiles.Prepare(importerDir, dir, Path.GetFullPath(userDir));
        }

        var psi = new ProcessStartInfo(Path.Combine(dir, "NadeoImporter.exe")) { WorkingDirectory = dir };
        psi.ArgumentList.Add("Item");
        psi.ArgumentList.Add(relativeXmlPattern);
        return await ImporterFiles.RunProcessAsync(psi, timeout ?? TimeSpan.FromMinutes(5), ct);
    }
}

/// <summary>Runs NadeoImporter.exe through Wine (Linux). Every run gets its own importer copy and Nadeo.ini.</summary>
public sealed class WineImporter(string importerDir, string wineCommand = "wine", TimeSpan? timeout = null) : IImporter
{
    public async Task<string> RunAsync(string userDir, string relativeXmlPattern, CancellationToken ct = default)
    {
        ImporterFiles.Validate(importerDir);
        var full = Path.GetFullPath(userDir);
        var dir = Path.Combine(Path.GetDirectoryName(full)!, "importer");
        // Wine maps the Linux root to drive Z:.
        ImporterFiles.Prepare(importerDir, dir, "Z:" + full.Replace('/', '\\'));

        var psi = new ProcessStartInfo(wineCommand) { WorkingDirectory = dir };
        psi.ArgumentList.Add("NadeoImporter.exe");
        psi.ArgumentList.Add("Item");
        psi.ArgumentList.Add(relativeXmlPattern);
        psi.Environment["WINEDEBUG"] = "-all";
        return await ImporterFiles.RunProcessAsync(psi, timeout ?? TimeSpan.FromMinutes(5), ct);
    }
}

/// <summary>
/// Runs NadeoImporter in the <c>tmspline-importer</c> Docker image (see docker/importer). The user folder must be
/// named "user" inside a per-job folder, which is mounted at /job.
/// </summary>
public sealed class DockerImporter : IImporter
{
    private readonly string _image;
    private readonly string _sharedImporterDir;
    private readonly TimeSpan _timeout;

    public DockerImporter(string importerDir, string workRoot, string image = "tmspline-importer", TimeSpan? timeout = null)
    {
        ImporterFiles.Validate(importerDir);
        _image = image;
        _timeout = timeout ?? TimeSpan.FromMinutes(5);
        // One read-only importer copy for all jobs; inside the container the job is always at /job.
        _sharedImporterDir = Path.Combine(Path.GetFullPath(workRoot), "_importer-docker");
        ImporterFiles.Prepare(importerDir, _sharedImporterDir, @"Z:\job\user");
    }

    public async Task<string> RunAsync(string userDir, string relativeXmlPattern, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(userDir);
        if (!string.Equals(Path.GetFileName(full), "user", StringComparison.Ordinal))
            throw new ArgumentException("Docker importer expects the user folder to be named 'user'", nameof(userDir));
        var jobDir = Path.GetDirectoryName(full)!;

        var psi = new ProcessStartInfo("docker");
        foreach (var a in new[] { "run", "--rm", "--network", "none",
                     "-v", $"{_sharedImporterDir}:/importer:ro", "-v", $"{jobDir}:/job", _image, "Item", relativeXmlPattern })
            psi.ArgumentList.Add(a);
        return await ImporterFiles.RunProcessAsync(psi, _timeout, ct);
    }
}
