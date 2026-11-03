using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Photino.NET;
using TmSpline.Server;

namespace TmSpline.Desktop;

/// <summary>
/// Starts the TMSpline server on localhost (the Openplanet plugin talks to it there) and shows the editor it serves in a
/// window. Everything the app keeps lives in <see cref="DesktopPaths.Root"/>.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The block library builder runs VanillaLib in a child process of this executable (see VanillaBuilder).
        if (args.Length > 0 && args[0] == VanillaBuilder.SelfSwitch)
            return RunVanillaLib(args[1..]);

        var paths = DesktopPaths.Create();
        var settings = DesktopSettings.Load(paths.SettingsFile);
        var url = $"http://localhost:{settings.Port}";

        if (!PortFree(settings.Port))
        {
            ShowError($"Port {settings.Port} is already in use, so TMSpline cannot start its local server. Close the other "
                + "program (or another TMSpline) and start TMSpline again, or set another \"port\" in "
                + $"{paths.SettingsFile} (the Openplanet plugin's server URL then has to match).");
            return 1;
        }

        WebApplication app;
        try
        {
            app = LocalServer.Build(paths, settings, url);
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            ShowError($"TMSpline's local server did not start: {e.Message}");
            return 1;
        }

        var window = new PhotinoWindow()
            .SetTitle("TMSpline")
            .SetUseOsDefaultSize(false)
            .SetSize(1600, 1000)
            .Center()
            .SetResizable(true)
            .SetTemporaryFilesPath(paths.WebView)
            .SetDevToolsEnabled(settings.DevTools)
            .SetContextMenuEnabled(settings.DevTools)
            .Load(new Uri(url + "/"));
        window.WaitForClose();

        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            app.StopAsync(stop.Token).GetAwaiter().GetResult();
        return 0;
    }

    private static bool PortFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void ShowError(string message)
    {
        // A window just for the message: the app has no console.
        new PhotinoWindow().SetTitle("TMSpline").ShowMessage("TMSpline", message, PhotinoDialogButtons.Ok, PhotinoDialogIcon.Error);
    }

    /// <summary>VanillaLib's own Main (a top-level program), for the block library builder.</summary>
    private static int RunVanillaLib(string[] args)
    {
        var entry = Assembly.Load("VanillaLib").EntryPoint ?? throw new InvalidOperationException("VanillaLib has no entry point");
        try
        {
            var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : [args]);
            if (result is Task task) task.GetAwaiter().GetResult();
            return result is int code ? code : result is Task<int> t ? t.Result : 0;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            Console.Error.WriteLine(e.InnerException);
            return 1;
        }
    }
}
