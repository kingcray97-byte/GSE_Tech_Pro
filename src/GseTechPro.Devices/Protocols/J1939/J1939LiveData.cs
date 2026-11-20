namespace GseTechPro.Devices.Protocols.J1939;

/// <summary>One decoded live-data parameter from a J1939 message.</summary>
public readonly record struct J1939LiveDataValue(string Name, double Value, string Unit, int Spn, bool Known)
{
    /// <summary>Shorthand for a raw/unknown SPN (no invented name or unit).</summary>
    public static J1939LiveDataValue Raw(int spn, double rawValue) => new($"SPN {spn} (raw)", rawValue, "raw", spn, Known: false);
}

/// <summary>
/// Extracts published SPN values from the common engine PGNs. SPN definitions (start byte/bit,
/// width, resolution, offset, unit) are from <b>SAE J1939-71</b> (Vehicle Application Layer) Appendix B
/// (individual SPN definitions) together with the message section (§5.3.x) for the byte placement.
///
/// Scope: only <em>byte-aligned</em> parameters (start bit 1, length a multiple of 8, little-endian)
/// are extracted. Bit-level fields (e.g. the EEC1 torque-mode nibble) are not decoded here — they are
/// returned as unknown rather than invented. A PGN/SPN with no entry is returned as a raw value so the
/// UI can display it without a fabricated description (per <c>data/dtc/README.md</c>).
/// </summary>
public static class J1939LiveData
{
    /// <summary>True when the payload is a PGN we know how to decode into at least one SPN.</summary>
    public static bool IsKnownPgn(uint pgn) => Definitions.ContainsKey(pgn);

    /// <summary>Extracts all known SPNs from a message payload. Empty when the PGN is unknown.</summary>
    public static IReadOnlyList<J1939LiveDataValue> Extract(uint pgn, ReadOnlySpan<byte> payload)
    {
        if (!Definitions.TryGetValue(pgn, out var spns))
            return Array.Empty<J1939LiveDataValue>();

        var results = new List<J1939LiveDataValue>(spns.Count);
        foreach (var def in spns)
        {
            if (!TryRead(payload, def, out var value))
                continue;
            results.Add(new J1939LiveDataValue(def.Name, value, def.Unit, def.Spn, Known: true));
        }
        return results;
    }

    private static bool TryRead(ReadOnlySpan<byte> payload, SpnDefinition def, out double value)
    {
        value = 0;
        var byteCount = def.LengthBits / 8;
        if (def.LengthBits % 8 != 0)
            return false; // bit-level fields are not decoded in this round
        var firstIndex = def.StartByte - 1;
        if (firstIndex < 0 || firstIndex + byteCount > payload.Length)
            return false;

        ulong raw = 0;
        for (var i = 0; i < byteCount; i++)
            raw |= (ulong)payload[firstIndex + i] << (8 * i);

        if (!IsValidRaw(raw, byteCount))
            return false; // error indicator or "not available": no value, never a made-up number

        value = raw * def.Resolution + def.Offset;
        return true;
    }

    /// <summary>
    /// SAE J1939-71 parameter ranges: the top of each field's range is reserved for indicators — for 1 byte, values
    /// above 0xFA (0xFE = error, 0xFF = not available); for 2 bytes, above 0xFAFF; for 4 bytes, above 0xFAFFFFFF.
    /// </summary>
    public static bool IsValidRaw(ulong raw, int byteCount) => byteCount switch
    {
        1 => raw <= 0xFA,
        2 => raw <= 0xFAFF,
        4 => raw <= 0xFAFF_FFFF,
        _ => true,
    };

    private sealed record SpnDefinition(int Spn, string Name, int StartByte, int LengthBits, double Resolution, double Offset, string Unit);

    // Keyed by PGN. Byte positions are 1-based; all entries start at bit 1 (LSB) and are little-endian.
    private static readonly IReadOnlyDictionary<uint, IReadOnlyList<SpnDefinition>> Definitions =
        new Dictionary<uint, IReadOnlyList<SpnDefinition>>
        {
            // EEC1 — Electronic Engine Controller 1 (J1939-71 §5.3.1)
            [J1939Pgn.ElectronicEngineController1] = new[]
            {
                new SpnDefinition(512, "Driver's Demand Engine - Percent Torque", 2, 8, 1.0, -125.0, "%"),
                new SpnDefinition(513, "Actual Engine - Percent Torque", 3, 8, 1.0, -125.0, "%"),
                new SpnDefinition(190, "Engine Speed", 4, 16, 0.125, 0.0, "rpm"),
            },

            // EEC2 — Electronic Engine Controller 2 (J1939-71 §5.3.2)
            [J1939Pgn.ElectronicEngineController2] = new[]
            {
                new SpnDefinition(91, "Accelerator Pedal Position 1", 2, 8, 0.4, 0.0, "%"),
                new SpnDefinition(92, "Engine Percent Load at Current Speed", 3, 8, 1.0, 0.0, "%"),
            },

            // ET1 — Engine Temperature 1 (J1939-71 §5.3.x)
            [J1939Pgn.EngineTemperature1] = new[]
            {
                new SpnDefinition(110, "Engine Coolant Temperature", 1, 8, 1.0, -40.0, "°C"),
                new SpnDefinition(174, "Fuel Temperature", 2, 8, 1.0, -40.0, "°C"),
                new SpnDefinition(175, "Engine Oil Temperature", 3, 16, 0.03125, -273.0, "°C"),
            },

            // EFL/P1 — Engine Fluid Level/Pressure 1 (J1939-71 §5.3.x)
            [J1939Pgn.EngineFluidLevelPressure1] = new[]
            {
                new SpnDefinition(95, "Engine Fuel Filter Differential Pressure", 1, 8, 2.0, 0.0, "kPa"),
                new SpnDefinition(94, "Fuel Delivery Pressure", 2, 8, 4.0, 0.0, "kPa"),
                new SpnDefinition(98, "Engine Oil Level", 4, 8, 0.4, 0.0, "%"),
                new SpnDefinition(100, "Engine Oil Pressure", 5, 8, 4.0, 0.0, "kPa"),
            },

            // CCVS — Cruise Control / Vehicle Speed (J1939-71 §5.3.x)
            [J1939Pgn.CruiseControlVehicleSpeed] = new[]
            {
                new SpnDefinition(84, "Wheel-Based Vehicle Speed", 2, 16, 1.0 / 256.0, 0.0, "km/h"),
            },

            // HOURS — Engine Hours, Revolutions (J1939-71 §5.3.x)
            [J1939Pgn.EngineHoursRevolutions] = new[]
            {
                new SpnDefinition(247, "Engine Total Hours of Operation", 1, 32, 0.05, 0.0, "h"),
            },

            // Ambient Conditions (J1939-71 §5.3.x)
            [J1939Pgn.AmbientConditions] = new[]
            {
                new SpnDefinition(108, "Barometric Pressure", 4, 16, 0.5, 0.0, "kPa"),
                new SpnDefinition(171, "Ambient Air Temperature", 8, 8, 0.5, -40.0, "°C"),
            },

            // Vehicle Electrical Power (J1939-71 §5.3.x)
            [J1939Pgn.VehicleElectricalPower] = new[]
            {
                new SpnDefinition(168, "Battery Potential (Voltage)", 7, 16, 0.05, 0.0, "V"),
            },

            // Inlet/Exhaust Conditions (J1939-71 §5.3.x)
            [J1939Pgn.InletExhaustConditions] = new[]
            {
                new SpnDefinition(105, "Intake Manifold 1 Temperature", 4, 8, 1.0, -40.0, "°C"),
                new SpnDefinition(106, "Air Inlet Pressure", 5, 16, 2.0, 0.0, "kPa"),
                new SpnDefinition(102, "Boost Pressure", 8, 8, 2.0, 0.0, "kPa"),
            },

            // Fuel Economy (J1939-71 §5.3.x)
            [J1939Pgn.FuelEconomy] = new[]
            {
                new SpnDefinition(183, "Engine Fuel Rate", 1, 16, 0.05, 0.0, "L/h"),
            },
        };
}
