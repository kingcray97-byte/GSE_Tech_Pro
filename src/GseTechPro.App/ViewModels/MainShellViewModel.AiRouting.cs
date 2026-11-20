using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GseTechPro.App.Services;
using GseTechPro.Core.Ai;
using GseTechPro.Core.Enums;
using Microsoft.UI.Xaml;

namespace GseTechPro.App.ViewModels;

/// <summary>
/// Phase 10A.4 (ADR-013): where chat answers come from. Setup &gt; AI service holds the route preference (any
/// technician) and the administrator settings (allow cloud, share job details; administrator PIN). Tech Chat has a
/// per-job "This tablet only" switch, and every answer carries a route badge.
/// </summary>
public sealed partial class MainShellViewModel
{
    private AiRoutingSettingsService? _aiRoutingSettings;
    private readonly HashSet<string> _tabletOnlyJobs = new(StringComparer.Ordinal);
    private bool _suppressRouteModeSave;

    private AiRoutingSettingsService AiRoutingSettings =>
        _aiRoutingSettings ??= new AiRoutingSettingsService(_dataServices.Settings);

    /// <summary>Shop routing settings as last loaded or saved.</summary>
    public AiRoutingOptions AiRoutingOptions { get; private set; } = AiRoutingOptions.TabletOnly;

    /// <summary>Labels for the route preference picker, in <see cref="AiRoutingMode"/> order.</summary>
    public IReadOnlyList<string> AiRouteModeLabels { get; } = new[]
    {
        "This tablet only",
        "Prefer shop server, then this tablet",
        "Prefer cloud, then shop server, then this tablet",
    };

    [ObservableProperty]
    private int _selectedAiRouteModeIndex;

    [ObservableProperty]
    private bool _hasAdminPin;

    [ObservableProperty]
    private string _adminPinInput = "";

    [ObservableProperty]
    private string _adminNewPinInput = "";

    [ObservableProperty]
    private bool _adminCloudAllowedDraft;

    [ObservableProperty]
    private bool _adminShareJobDetailsDraft;

    [ObservableProperty]
    private string _aiRoutingStatusMessage = "";

    public async Task LoadAiRoutingSettingsAsync()
    {
        var options = await AiRoutingSettings.LoadAsync();
        HasAdminPin = await AiRoutingSettings.HasAdminPinAsync();
        ApplyAiRoutingOptions(options);
    }

    private void ApplyAiRoutingOptions(AiRoutingOptions options)
    {
        AiRoutingOptions = options;
        _suppressRouteModeSave = true;
        SelectedAiRouteModeIndex = (int)options.Mode;
        _suppressRouteModeSave = false;
        AdminCloudAllowedDraft = options.CloudAllowed;
        AdminShareJobDetailsDraft = options.ShareJobDetailsWithCloud;
        NotifyAiRoutingChanged();
    }

    partial void OnSelectedAiRouteModeIndexChanged(int value)
    {
        if (_suppressRouteModeSave || value < 0 || value >= AiRouteModeLabels.Count)
            return;
        var mode = (AiRoutingMode)value;
        if (mode == AiRoutingOptions.Mode)
            return;
        AiRoutingOptions = AiRoutingOptions with { Mode = mode };
        NotifyAiRoutingChanged();
        _ = SaveRouteModeAsync(mode);
    }

    private async Task SaveRouteModeAsync(AiRoutingMode mode)
    {
        try
        {
            await AiRoutingSettings.SaveModeAsync(mode);
            AiRoutingStatusMessage = mode == AiRoutingMode.PreferCloud && !AiRoutingOptions.CloudAllowed
                ? "Saved. The cloud is not allowed by the administrator, so answers come from the shop server or this tablet."
                : "Route preference saved.";
        }
        catch (Exception ex)
        {
            AiRoutingStatusMessage = $"Could not save the route preference: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SaveAdminAiSettingsAsync()
    {
        var pin = AdminPinInput;
        AdminPinInput = "";
        if (!HasAdminPin)
        {
            AiRoutingStatusMessage = "Set an administrator PIN first.";
            return;
        }

        var saved = await AiRoutingSettings.SaveAdminSettingsAsync(pin, AdminCloudAllowedDraft, AdminShareJobDetailsDraft);
        if (!saved)
        {
            AiRoutingStatusMessage = "Wrong administrator PIN. Nothing was changed.";
            AdminCloudAllowedDraft = AiRoutingOptions.CloudAllowed;
            AdminShareJobDetailsDraft = AiRoutingOptions.ShareJobDetailsWithCloud;
            return;
        }

        ApplyAiRoutingOptions(await AiRoutingSettings.LoadAsync());
        AiRoutingStatusMessage = AiRoutingOptions.CloudAllowed
            ? AiRoutingOptions.ShareJobDetailsWithCloud
                ? "Saved. Cloud allowed, with work order and equipment details."
                : "Saved. Cloud allowed. Work order and equipment details stay on this tablet."
            : "Saved. Cloud not allowed.";
    }

    [RelayCommand]
    public async Task SetAdminPinAsync()
    {
        var current = AdminPinInput;
        var next = AdminNewPinInput;
        AdminPinInput = "";
        AdminNewPinInput = "";

        if (!AdminPinHasher.IsValidPin(next))
        {
            AiRoutingStatusMessage =
                $"The new PIN must be {AiRoutingSettingsService.MinPinLength}–{AiRoutingSettingsService.MaxPinLength} digits.";
            return;
        }

        var ok = await AiRoutingSettings.SetAdminPinAsync(current, next);
        if (!ok)
        {
            AiRoutingStatusMessage = "Wrong current administrator PIN. The PIN was not changed.";
            return;
        }

        HasAdminPin = true;
        AiRoutingStatusMessage = "Administrator PIN saved.";
    }

    partial void OnHasAdminPinChanged(bool value)
    {
        OnPropertyChanged(nameof(AdminPinActionLabel));
        OnPropertyChanged(nameof(AdminPinHintText));
    }

    public string AdminPinActionLabel => HasAdminPin ? "Change PIN" : "Set PIN";

    public string AdminPinHintText => HasAdminPin
        ? "Enter the administrator PIN to save the cloud settings or to change the PIN."
        : "No administrator PIN is set on this tablet yet. Set one before allowing the cloud.";

    public Visibility AiRoutingStatusMessageVisibility =>
        string.IsNullOrWhiteSpace(AiRoutingStatusMessage) ? Visibility.Collapsed : Visibility.Visible;

    partial void OnAiRoutingStatusMessageChanged(string value) =>
        OnPropertyChanged(nameof(AiRoutingStatusMessageVisibility));

    partial void OnAdminCloudAllowedDraftChanged(bool value)
    {
        if (!value)
            AdminShareJobDetailsDraft = false;
    }

    // ---------------------------------------------------------------------------------------------
    // Per-job "This tablet only"
    // ---------------------------------------------------------------------------------------------

    /// <summary>The job the chat is about (same id sent as <c>work_order_id</c>).</summary>
    private string? CurrentChatJobId => ActiveWorkOrder?.Id ?? SelectedTab?.Id;

    /// <summary>Any technician can keep a job's chat on this tablet, whatever the shop setting.</summary>
    public bool IsJobTabletOnly
    {
        get => CurrentChatJobId is { } id && _tabletOnlyJobs.Contains(id);
        set
        {
            if (CurrentChatJobId is not { } id || value == IsJobTabletOnly)
                return;
            if (value)
                _tabletOnlyJobs.Add(id);
            else
                _tabletOnlyJobs.Remove(id);
            NotifyAiRoutingChanged();
        }
    }

    /// <summary>The <c>routing</c> sent with the next chat question.</summary>
    public AiRoutingOptions CurrentChatRouting =>
        IsJobTabletOnly ? AiRoutingOptions.TabletOnly : AiRoutingOptions;

    /// <summary>Plain-language summary of where the next answer can come from.</summary>
    public string ChatRouteSummaryText => DescribeRouting(CurrentChatRouting, IsJobTabletOnly);

    public static string DescribeRouting(AiRoutingOptions options, bool jobTabletOnly = false)
    {
        if (jobTabletOnly)
            return "Answers for this job: this tablet only.";
        return options.Mode switch
        {
            AiRoutingMode.PreferCloud when options.CloudAllowed => options.ShareJobDetailsWithCloud
                ? "Answers: cloud when online, else shop server, else this tablet. Job details may go to the cloud."
                : "Answers: cloud when online, else shop server, else this tablet. Job details stay on this tablet.",
            AiRoutingMode.PreferCloud => "Answers: shop server, else this tablet (cloud not allowed by the administrator).",
            AiRoutingMode.PreferShopServer => "Answers: shop server, else this tablet.",
            _ => "Answers: this tablet only.",
        };
    }

    /// <summary>One line per route from <c>GET /health</c> (Setup &gt; AI service).</summary>
    public IReadOnlyList<string> AiRouteStatusLines
    {
        get
        {
            var routes = AiState.Health?.Routes;
            if (routes is null || routes.Count == 0)
                return new[] { "This tablet: " + (AiState.Health is null ? "AI service not reachable" : "Ready") };
            return routes.Select(r =>
            {
                var state = !r.Configured ? "Not set up" : r.Message ?? (r.Available ? "Ready" : "Offline");
                var model = string.IsNullOrWhiteSpace(r.Model) ? "" : $" ({r.Model})";
                return $"{AiRouting.BadgeText(r.Route)}: {state}{model}";
            }).ToArray();
        }
    }

    private void NotifyAiRoutingChanged()
    {
        OnPropertyChanged(nameof(AiRoutingOptions));
        OnPropertyChanged(nameof(IsJobTabletOnly));
        OnPropertyChanged(nameof(CurrentChatRouting));
        OnPropertyChanged(nameof(ChatRouteSummaryText));
    }
}
