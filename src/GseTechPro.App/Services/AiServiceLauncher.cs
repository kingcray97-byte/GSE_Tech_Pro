using System.Diagnostics;
using System.Security.Cryptography;
using GseTechPro.Core.Contracts;

namespace GseTechPro.App.Services;

/// <summary>
/// Manages the local AI service endpoint identity (<c>--port</c> and <c>--token</c> per ADR-008)
/// and probes whether the local FastAPI service is already running.
/// <para>
/// Phase 3 stub: does not spawn a bundled Python child process yet (bundled Python + service
/// packaging lands in Phase 8 owned by <c>installer-deploy</c>). If an instance of the service is
/// already running on <see cref="BaseUrl"/>, connects to it; otherwise leaves the app in the
/// graceful <c>Unavailable</c> degraded state.
/// </para>
/// </summary>
public sealed class AiServiceLauncher
{
    public const string StandaloneDevToken = "dev-local-token";
    public const string AllowDevTokenEnvVar = "GSE_AI_ALLOW_DEV_TOKEN";

    public AiServiceLauncher(int? port = null, string? token = null, bool? allowDevTokenFallback = null, AiServiceLaunchOptions? launchOptions = null)
    {
        var envPortText = Environment.GetEnvironmentVariable("GSE_AI_PORT");
        if (port is int explicitPort && explicitPort > 0)
        {
            Port = explicitPort;
        }
        else if (int.TryParse(envPortText, out var envPort) && envPort > 0)
        {
            Port = envPort;
        }
        else
        {
            Port = AiServiceProtocol.DefaultPort;
        }

        var envToken = Environment.GetEnvironmentVariable("GSE_AI_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            Token = token.Trim();
            HasExplicitToken = true;
        }
        else if (!string.IsNullOrWhiteSpace(envToken))
        {
            Token = envToken.Trim();
            HasExplicitToken = true;
        }
        else
        {
            Token = GenerateInstallToken();
            HasExplicitToken = false;
        }

        AllowDevTokenFallback = allowDevTokenFallback ?? IsDevTokenFallbackEnabledByDefault();

        LaunchOptions = launchOptions ?? AiServiceLaunchOptions.ResolveDefault();
    }

    public int Port { get; private set; }

    public string Token { get; private set; }

    public bool HasExplicitToken { get; }

    /// <summary>
    /// True only in <c>DEBUG</c> builds or when <see cref="AllowDevTokenEnvVar"/> is explicitly enabled.
    /// Never enabled by default in Release builds.
    /// </summary>
    public bool AllowDevTokenFallback { get; }

    public string BaseUrl => $"http://{AiServiceProtocol.Host}:{Port}";

    public bool IsServiceReachable { get; private set; }

    public AiServiceLaunchOptions LaunchOptions { get; }

    public bool IsServiceOwnedByApp { get; private set; }

    public int? OwnedServicePid { get; private set; }

    private Process? _ownedProcess;
    private DateTime? _lastSpawnFailureUtc;

    public string StatusNote { get; private set; } = "AI service not yet probed.";

    public static bool IsDevTokenFallbackEnabledByDefault()
    {
        var env = Environment.GetEnvironmentVariable(AllowDevTokenEnvVar);
        if (string.Equals(env, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(env, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(env, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(env, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    internal static string GetDefaultFallbackOrRandomToken()
    {
#if DEBUG
        return StandaloneDevToken;
#else
        return IsDevTokenFallbackEnabledByDefault() ? StandaloneDevToken : GenerateInstallToken();
#endif
    }

    /// <summary>
    /// Generates a 32-byte (64 lowercase hex char) per-install token matching ADR-008 / ADR-007.
    /// </summary>
    public static string GenerateInstallToken()
    {
        Span<byte> bytes = stackalloc byte[AiServiceProtocol.TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the CLI arguments the app passes when spawning the Python AI service (ADR-008).
    /// </summary>
    public string BuildLaunchArguments() => $"--port {Port} --token {Token}";

    /// <summary>
    /// Resets reachability state when a subsequent health/config/chat request fails to connect.
    /// </summary>
    public void MarkUnreachable(string? reason = null)
    {
        IsServiceReachable = false;
        StatusNote = string.IsNullOrWhiteSpace(reason)
            ? $"AI service unreachable at {BaseUrl}."
            : $"AI service unreachable at {BaseUrl} ({reason}).";
    }

    /// <summary>
    /// Probes whether the local AI service is already running on <see cref="BaseUrl"/>.
    /// In Debug builds (or when <see cref="AllowDevTokenFallback"/> is enabled), if the generated
    /// per-install token receives <c>401 Unauthorized</c> against a standalone dev instance and no
    /// explicit token was forced, falls back to <see cref="StandaloneDevToken"/>.
    /// </summary>
    public async Task<bool> ProbeExistingServiceAsync(
        AiServiceClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Configure(BaseUrl, Token);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                await client.HealthAsync(cts.Token).ConfigureAwait(false);
                IsServiceReachable = true;
                StatusNote = $"Connected to running AI service at {BaseUrl}.";
                return true;
            }
            catch (AiServiceException ex) when (ex.IsUnauthorized && !HasExplicitToken && AllowDevTokenFallback)
            {
                // Standalone dev server uses "dev-local-token" from config.yaml when not spawned by the App (DEBUG / dev-opt-in only).
                Token = StandaloneDevToken;
                client.Configure(BaseUrl, Token);
                await client.HealthAsync(cts.Token).ConfigureAwait(false);
                IsServiceReachable = true;
                StatusNote = $"Connected to standalone dev AI service at {BaseUrl}.";
                return true;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiServiceException)
        {
            MarkUnreachable(ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Ensures the local AI service is running: probes first; if unreachable and the bundled
    /// (or env-overridden) layout is present, spawns <c>python -m app.main</c> and waits for
    /// <c>/health</c>. Returns <c>true</c> once the service answers. Spawns at most once per
    /// 60 s on failure (cooldown) to avoid hammering a broken install.
    /// </summary>
    public async Task<bool> EnsureServiceRunningAsync(
        AiServiceClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (IsServiceReachable)
        {
            return true;
        }

        if (await ProbeExistingServiceAsync(client, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!LaunchOptions.Enabled)
        {
            MarkUnreachable("AI service not running and no bundled service layout was found.");
            return false;
        }

        if (_lastSpawnFailureUtc is DateTime lastFailure &&
            DateTime.UtcNow - lastFailure < TimeSpan.FromSeconds(60))
        {
            MarkUnreachable("AI service spawn is in cooldown after a recent failure.");
            return false;
        }

        if (_ownedProcess is null || _ownedProcess.HasExited)
        {
            if (!TrySpawn(out var reason))
            {
                _lastSpawnFailureUtc = DateTime.UtcNow;
                MarkUnreachable(reason);
                return false;
            }
        }

        client.Configure(BaseUrl, Token);

        var deadline = DateTime.UtcNow + LaunchOptions.StartupTimeout;
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_ownedProcess is { HasExited: true })
            {
                _lastSpawnFailureUtc = DateTime.UtcNow;
                MarkUnreachable("Spawned AI service exited before becoming healthy.");
                return false;
            }

            try
            {
                await Task.Delay(LaunchOptions.PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                await client.HealthAsync(cts.Token).ConfigureAwait(false);
                IsServiceReachable = true;
                StatusNote = $"Started local AI service at {BaseUrl}.";
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or AiServiceException)
            {
                // still warming up / not yet listening
            }
        }

        _lastSpawnFailureUtc = DateTime.UtcNow;
        MarkUnreachable($"AI service did not become healthy within {LaunchOptions.StartupTimeout.TotalSeconds:0} s.");
        return false;
    }

    private bool TrySpawn(out string reason)
    {
        reason = string.Empty;

        var python = LaunchOptions.PythonExecutablePath;
        var serviceDir = LaunchOptions.ServiceWorkingDirectory;
        if (string.IsNullOrWhiteSpace(python) || !File.Exists(python) ||
            string.IsNullOrWhiteSpace(serviceDir) || !Directory.Exists(serviceDir))
        {
            reason = "Bundled AI service not found (expected Python\\python.exe next to the app).";
            return false;
        }

        var args = $"-m app.main --port {Port} --token {Token}";
        if (!string.IsNullOrWhiteSpace(LaunchOptions.ConfigPath) && File.Exists(LaunchOptions.ConfigPath))
        {
            args += $" --config \"{LaunchOptions.ConfigPath}\"";
        }

        string? logDir = LaunchOptions.LogDirectory;
        if (string.IsNullOrWhiteSpace(logDir))
        {
            logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GseTechPro", "logs");
        }
        try
        {
            Directory.CreateDirectory(logDir);
        }
        catch
        {
            logDir = Path.GetTempPath();
        }
        var logPath = Path.Combine(logDir, "ai-service.log");

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            WorkingDirectory = serviceDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        Process? proc;
        try
        {
            proc = LaunchOptions.ProcessStarter is null
                ? Process.Start(psi)
                : LaunchOptions.ProcessStarter(psi);
        }
        catch (Exception ex)
        {
            reason = $"Failed to start AI service: {ex.Message}";
            return false;
        }

        if (proc is null)
        {
            reason = "Failed to start AI service: Process.Start returned null.";
            return false;
        }

        proc.EnableRaisingEvents = true;
        _ownedProcess = proc;
        IsServiceOwnedByApp = true;
        OwnedServicePid = proc.Id;

        proc.Exited += (_, _) =>
        {
            IsServiceReachable = false;
            StatusNote = "Spawned AI service exited.";
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await using var writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
                await writer.WriteLineAsync($"===== AI service started {DateTime.UtcNow:O} (pid {proc.Id}) =====");
                var outTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                        {
                            await writer.WriteLineAsync($"[out] {line}").ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                    }
                });
                var errTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                        {
                            await writer.WriteLineAsync($"[err] {line}").ConfigureAwait(false);
                        }
                    }
                    catch
                    {
                    }
                });
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
            }
            catch
            {
            }
        });

        return true;
    }

    /// <summary>
    /// Stops a service process that this app spawned. No-op when the app attached to an
    /// existing service. Kills the whole process tree so no <c>llama-server</c> children survive.
    /// </summary>
    public void StopOwnedService()
    {
        var proc = _ownedProcess;
        if (proc is null || !IsServiceOwnedByApp)
        {
            return;
        }

        try
        {
            if (!proc.HasExited)
            {
                KillProcessTree(proc.Id);
            }
        }
        catch
        {
        }
        finally
        {
            IsServiceOwnedByApp = false;
            OwnedServicePid = null;
            _ownedProcess = null;
            IsServiceReachable = false;
        }
    }

    private static void KillProcessTree(int rootPid)
    {
        // taskkill /T reliably terminates the whole process tree (incl. llama-server
        // grandchildren) on Windows, which Process.Kill(entireProcessTree) sometimes misses.
        try
        {
            using var killer = Process.Start(new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {rootPid} /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            killer?.WaitForExit(10000);
        }
        catch
        {
            // Fall back to direct kill if taskkill is unavailable.
            try
            {
                using var root = Process.GetProcessById(rootPid);
                if (!root.HasExited)
                {
                    root.Kill(entireProcessTree: true);
                    root.WaitForExit(5000);
                }
            }
            catch
            {
            }
        }
    }

    public void Dispose() => StopOwnedService();
}
