using System.Diagnostics;
using System.Security.Cryptography;
using GseTechPro.Core.Contracts;

namespace GseTechPro.Devices;

/// <summary>
/// Owns the lifecycle of the 32-bit device host process: generates the per-session pipe name and
/// per-launch token (ARCHITECTURE.md §1.1.1), locates <c>GseTechPro.Devices.Host32.exe</c>, launches
/// it with <c>--pipe</c>/<c>--token</c>, and tears it down on dispose.
/// </summary>
public sealed class HostProcess : IAsyncDisposable, IDisposable
{
    private const string HostExecutableName = "GseTechPro.Devices.Host32.exe";

    private readonly object _gate = new();
    private Process? _process;

    private HostProcess(string pipeName, string token)
    {
        PipeName = pipeName;
        Token = token;
    }

    public string PipeName { get; }

    public string Token { get; }

    /// <summary>True once the launched host process has exited (or was never started).</summary>
    public bool HasExited => _process?.HasExited ?? true;

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for the host process to exit; returns true if it
    /// exited within the window (e.g. because the required x86 .NET runtime is not installed).
    /// </summary>
    public bool WaitForExit(int timeoutMs) => _process?.WaitForExit(timeoutMs) ?? true;

    /// <summary>Generates the app-owned session identity: <c>prefix + "-" + 32 hex</c> name and a 64-hex token.</summary>
    public static (string PipeName, string Token) GenerateSessionIdentity()
    {
        var pipeName = $"{HostProtocol.PipeNamePrefix}-{Guid.NewGuid():N}";
        var tokenBytes = RandomNumberGenerator.GetBytes(HostProtocol.TokenBytes);
        var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
        return (pipeName, token);
    }

    /// <summary>Locates the host executable next to the app, or under a <c>host32\</c> subfolder.</summary>
    public static string FindHostExecutable()
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = Path.Combine(baseDir, HostExecutableName);
        if (File.Exists(direct))
            return direct;

        var subdir = Path.Combine(baseDir, "host32", HostExecutableName);
        if (File.Exists(subdir))
            return subdir;

        throw new FileNotFoundException(
            $"Device host executable '{HostExecutableName}' was not found next to the app or under 'host32'.",
            HostExecutableName);
    }

    /// <summary>Generates an identity and launches the host process with it.</summary>
    public static HostProcess Start()
    {
        var (pipeName, token) = GenerateSessionIdentity();
        var executable = FindHostExecutable();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = $"--pipe {pipeName} --token {token}",
        };
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the 32-bit device host.");

        return new HostProcess(pipeName, token) { _process = process };
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
