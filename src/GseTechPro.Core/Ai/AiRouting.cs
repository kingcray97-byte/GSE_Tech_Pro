using GseTechPro.Core.Enums;

namespace GseTechPro.Core.Ai;

/// <summary>
/// The routing choice the app sends with each chat (<c>routing</c> on <c>POST /chat</c>, ADR-013).
/// <see cref="CloudAllowed"/> and <see cref="ShareJobDetailsWithCloud"/> are administrator settings
/// (Setup &gt; AI service, administrator PIN); <see cref="Mode"/> can be changed by any technician.
/// </summary>
public sealed record AiRoutingOptions
{
    public AiRoutingMode Mode { get; init; } = AiRoutingMode.TabletOnly;
    /// <summary>Administrator allows the cloud route. The service also needs <c>routes.cloud.enabled</c> in its config.</summary>
    public bool CloudAllowed { get; init; }
    /// <summary>Administrator allows work order / equipment details to go to the cloud model.</summary>
    public bool ShareJobDetailsWithCloud { get; init; }

    /// <summary>The default: this tablet only, cloud not allowed.</summary>
    public static AiRoutingOptions TabletOnly { get; } = new();
}

/// <summary>State of one route on <c>GET /health</c> (<c>routes</c>, ADR-013).</summary>
public sealed record AiRouteStatus
{
    public AiRoute Route { get; init; } = AiRoute.Tablet;
    /// <summary>The route is set up in the AI service config.</summary>
    public bool Configured { get; init; }
    /// <summary>The route passed its last health check.</summary>
    public bool Available { get; init; }
    /// <summary>Model label from the service config (never a secret).</summary>
    public string? Model { get; init; }
    /// <summary>Short human-readable state, e.g. "Ready", "Offline", "Not set up".</summary>
    public string? Message { get; init; }
}

/// <summary>
/// Route order for a chat question (ADR-013). The AI service follows the same table in
/// <c>ai-service/app/routing.py</c> (<c>plan_routes</c>); tests pin both.
/// </summary>
public static class AiRouting
{
    /// <summary>
    /// Routes to try, in order. The tablet is always last. <paramref name="cloudEnabledInService"/> is the
    /// service-side switch (<c>routes.cloud.enabled</c>); the cloud is used only when it and
    /// <see cref="AiRoutingOptions.CloudAllowed"/> are both true.
    /// </summary>
    public static IReadOnlyList<AiRoute> Plan(
        AiRoutingOptions? options,
        bool shopServerConfigured,
        bool cloudEnabledInService)
    {
        var opts = options ?? AiRoutingOptions.TabletOnly;
        var plan = new List<AiRoute>(3);
        if (opts.Mode == AiRoutingMode.PreferCloud && opts.CloudAllowed && cloudEnabledInService)
        {
            plan.Add(AiRoute.Cloud);
        }
        if ((opts.Mode is AiRoutingMode.PreferCloud or AiRoutingMode.PreferShopServer) && shopServerConfigured)
        {
            plan.Add(AiRoute.ShopServer);
        }
        plan.Add(AiRoute.Tablet);
        return plan;
    }

    /// <summary>Badge text for an answer's route; <c>CLOUD</c> is upper-case so nobody mistakes it for an on-tablet answer.</summary>
    public static string BadgeText(AiRoute? route) => route switch
    {
        AiRoute.Cloud => "CLOUD",
        AiRoute.ShopServer => "Shop server",
        AiRoute.Tablet => "This tablet",
        _ => "This tablet",
    };
}
