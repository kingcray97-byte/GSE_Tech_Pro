using GseTechPro.Core.Abstractions;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Abstractions;

namespace GseTechPro.Devices;

/// <summary>
/// Default implementation of <see cref="IDiagnosticAdapterFactory"/>. Phase 1 only constructs the
/// simulator; the J2534/RP1210 factory methods are added in Phase 2 when the vendor-DLL wrappers
/// behind the 32-bit device host exist.
/// </summary>
public sealed class DiagnosticAdapterFactory : IDiagnosticAdapterFactory
{
    /// <summary>Default deterministic seed so simulator runs are reproducible.</summary>
    public const int DefaultSeed = 12345;

    /// <inheritdoc />
    public IDiagnosticAdapter CreateSimulator(ProtocolType protocol, int? seed = null) =>
        new SimulatedDiagnosticAdapter(protocol, seed ?? DefaultSeed);

    // Phase 2: real J2534/RP1210 adapter creation. Keep the vendor SDK loading inside the 32-bit
    // host process and return a client that talks to it over named pipes — never load a 32-bit
    // vendor DLL here in the 64-bit app.
}
