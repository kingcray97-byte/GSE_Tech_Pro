using GseTechPro.Devices.Host32.J2534;
using GseTechPro.Devices.Host32.Rp1210;
using GseTechPro.Devices.Ipc;

namespace GseTechPro.Devices.Host32;

/// <summary>
/// GSE Tech Pro 32-bit device host entry point. The app is the orchestrator: it generates the pipe
/// name and per-launch token and launches this process with <c>--pipe &lt;name&gt; --token &lt;token&gt;</c>
/// (ARCHITECTURE.md §1.1.1, ADR-007). The host no longer invents its own pipe name.
///
/// Round 1 serves the simulator through <see cref="HostServer"/>. Round 2 loads the x86 J2534/RP1210
/// vendor DLLs here (the only place they are ever loaded) and injects a vendor-backed
/// <see cref="IHostDeviceProvider"/> before running the server.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var pipeName = ReadArg(args, "--pipe");
        var token = ReadArg(args, "--token");

        if (string.IsNullOrWhiteSpace(pipeName) || string.IsNullOrWhiteSpace(token))
        {
            Console.Error.WriteLine("Usage: GseTechPro.Devices.Host32.exe --pipe <name> --token <token>");
            Console.Error.WriteLine("The app supplies both values at launch; the host does not invent its own pipe name.");
            return 2;
        }

        Console.WriteLine("[Host32] GSE Tech Pro 32-bit device host starting (x86).");

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        // Round 2: discover J2534 (HKLM PassThruSupport.04.04/.05.00) and RP1210 (RP121032.ini)
        // devices and merge them with the simulator behind one IHostDeviceProvider.
        IHostDeviceProvider devices;
        try
        {
            devices = new CompositeDeviceProvider(
                new SimulatorDeviceProvider(),
                J2534DeviceProvider.Discover(),
                Rp1210DeviceProvider.Discover());
        }
        catch (Exception ex)
        {
            // Discovery must never prevent the host from serving the simulator.
            Console.WriteLine($"[Host32] Device discovery error (continuing with simulator only): {ex.Message}");
            devices = new SimulatorDeviceProvider();
        }

        var server = new HostServer(pipeName, token, devices);
        await server.RunAsync(shutdown.Token).ConfigureAwait(false);

        Console.WriteLine("[Host32] Shutdown complete.");
        return 0;
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
