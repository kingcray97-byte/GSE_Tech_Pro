using System.Security.Cryptography;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Enums;
using GseTechPro.Data.Repositories;

namespace GseTechPro.App.Services;

/// <summary>
/// Setup &gt; AI service (ADR-013). The route preference can be changed by any technician; "allow cloud" and
/// "share job details with cloud" need the administrator PIN. Stored in <c>app_settings</c>. Defaults: this tablet
/// only, cloud not allowed, job details not shared.
/// </summary>
public sealed class AiRoutingSettingsService(AppSettingsRepository settings)
{
    public const string ModeKey = "ai.routing.mode";
    public const string CloudAllowedKey = "ai.routing.cloud_allowed";
    public const string ShareJobDetailsKey = "ai.routing.share_job_details_with_cloud";
    public const string AdminPinKey = "admin.pin_hash";

    public const int MinPinLength = 4;
    public const int MaxPinLength = 8;

    public async Task<AiRoutingOptions> LoadAsync(CancellationToken cancellationToken = default)
    {
        var modeCode = await settings.GetAsync<string?>(ModeKey, null, cancellationToken).ConfigureAwait(false);
        var mode = DbEnum.TryParse<AiRoutingMode>(modeCode, out var parsed) ? parsed : AiRoutingMode.TabletOnly;
        var cloudAllowed = await settings.GetAsync(CloudAllowedKey, false, cancellationToken).ConfigureAwait(false);
        var share = await settings.GetAsync(ShareJobDetailsKey, false, cancellationToken).ConfigureAwait(false);
        return new AiRoutingOptions
        {
            Mode = mode,
            CloudAllowed = cloudAllowed,
            // Sharing job details only means something while the cloud is allowed.
            ShareJobDetailsWithCloud = cloudAllowed && share,
        };
    }

    /// <summary>Any technician may change the route preference.</summary>
    public Task SaveModeAsync(AiRoutingMode mode, CancellationToken cancellationToken = default) =>
        settings.SetAsync(ModeKey, DbEnum.ToCode(mode), cancellationToken);

    /// <summary>
    /// Administrator settings. Refused (returns false, nothing saved) unless <paramref name="pin"/> matches the stored
    /// administrator PIN.
    /// </summary>
    public async Task<bool> SaveAdminSettingsAsync(
        string pin,
        bool cloudAllowed,
        bool shareJobDetailsWithCloud,
        CancellationToken cancellationToken = default)
    {
        if (!await VerifyAdminPinAsync(pin, cancellationToken).ConfigureAwait(false))
            return false;

        await settings.SetAsync(CloudAllowedKey, cloudAllowed, cancellationToken).ConfigureAwait(false);
        await settings.SetAsync(ShareJobDetailsKey, cloudAllowed && shareJobDetailsWithCloud, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> HasAdminPinAsync(CancellationToken cancellationToken = default) =>
        !string.IsNullOrWhiteSpace(await settings.GetAsync<string?>(AdminPinKey, null, cancellationToken).ConfigureAwait(false));

    public async Task<bool> VerifyAdminPinAsync(string? pin, CancellationToken cancellationToken = default)
    {
        var stored = await settings.GetAsync<string?>(AdminPinKey, null, cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(stored) && AdminPinHasher.Verify(pin, stored);
    }

    /// <summary>
    /// Sets the administrator PIN. When a PIN already exists, <paramref name="currentPin"/> must match it.
    /// The first PIN can be set without one (first run on a new tablet).
    /// </summary>
    public async Task<bool> SetAdminPinAsync(string? currentPin, string newPin, CancellationToken cancellationToken = default)
    {
        if (!AdminPinHasher.IsValidPin(newPin))
            return false;
        if (await HasAdminPinAsync(cancellationToken).ConfigureAwait(false) &&
            !await VerifyAdminPinAsync(currentPin, cancellationToken).ConfigureAwait(false))
            return false;

        await settings.SetAsync(AdminPinKey, AdminPinHasher.Hash(newPin), cancellationToken).ConfigureAwait(false);
        return true;
    }
}

/// <summary>PBKDF2-SHA256 hash for the administrator PIN. Format: <c>pbkdf2-sha256$iterations$salt$hash</c> (base64).</summary>
public static class AdminPinHasher
{
    private const string Scheme = "pbkdf2-sha256";
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= AiRoutingSettingsService.MinPinLength and <= AiRoutingSettingsService.MaxPinLength } &&
        pin.All(char.IsAsciiDigit);

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string? pin, string stored)
    {
        if (string.IsNullOrEmpty(pin))
            return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], out var iterations) || iterations < 1)
            return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(pin, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
