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

    public AiServiceLauncher(int? port = null, string? token = null, bool? allowDevTokenFallback = null)
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
            MarkUnreachable($"{ex.Message}. Process launch lands in Phase 8");
            return false;
        }
    }
}
