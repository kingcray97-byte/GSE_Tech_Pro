using GseTechPro.Core.Contracts;
using GseTechPro.Core.Enums;
using GseTechPro.Devices.Protocols.Obd2;

namespace GseTechPro.Devices.Protocols.Uds;

/// <summary>
/// UDS (ISO 14229-1) diagnostic services implemented for the read/clear subset only (read-only first;
/// no actuation, no routine control). Services:
/// <list type="bullet">
/// <item><b>0x19 ReadDTCInformation</b> — sub-function 0x02 (reportDTCByStatusMask) decode.</item>
/// <item><b>0x14 ClearDiagnosticInformation</b> — request encode.</item>
/// </list>
/// Response services are request + 0x40 (0x59 / 0x54).
/// </summary>
public static class UdsService
{
    public const byte ReadDtcInformation = 0x19;
    public const byte ClearDiagnosticInformation = 0x14;

    public const byte ReadDtcInformationResponse = 0x59;
    public const byte ClearDiagnosticInformationResponse = 0x54;

    /// <summary>Sub-function: report DTCs by status mask.</summary>
    public const byte SubfunctionReportDtcByStatusMask = 0x02;
}

/// <summary>
/// The UDS DTC status byte (ISO 14229-1 Annex D): bit 0 testFailed, bit 1 testFailedThisOperationCycle,
/// bit 2 pendingDTC, bit 3 confirmedDTC, bit 4 testNotCompletedSinceLastClear, bit 5 testFailedSinceLastClear,
/// bit 6 testNotCompletedThisOperationCycle, bit 7 warningIndicatorRequested.
/// Mapping to <see cref="DtcStatus"/>: confirmedDTC → Active, pendingDTC → Pending, otherwise Stored.
/// </summary>
public static class UdsDtcStatus
{
    public const byte TestFailed = 0x01;
    public const byte TestFailedThisOperationCycle = 0x02;
    public const byte PendingDtc = 0x04;
    public const byte ConfirmedDtc = 0x08;
    public const byte WarningIndicatorRequested = 0x80;

    /// <summary>Maps a status byte to the app's Active/Pending/Stored model.</summary>
    public static DtcStatus ToDtcStatus(byte status)
    {
        if ((status & ConfirmedDtc) != 0)
            return DtcStatus.Active;
        if ((status & PendingDtc) != 0)
            return DtcStatus.Pending;
        return DtcStatus.Stored;
    }
}

/// <summary>
/// Codec for the UDS DTC representation. ISO 14229-1 uses a 3-byte DTC (high/middle/low) whose high two
/// bytes are identical to the 2-byte ISO 15031-6 (OBD-II) encoding; the low byte is reserved (0x00).
/// The 2-byte ISO 15031-6 format (DTCFormatIdentifier 0x01) is also handled.
/// </summary>
public static class UdsDtcCodec
{
    /// <summary>DTCFormatIdentifier: ISO 14229-1 (3-byte DTC).</summary>
    public const byte FormatIso14229 = 0x00;

    /// <summary>DTCFormatIdentifier: ISO 15031-6 (2-byte DTC).</summary>
    public const byte FormatIso15031 = 0x01;

    /// <summary>Decodes a 3-byte UDS DTC to a five-character code (high/middle only; low is reserved).</summary>
    public static string Decode3Byte(byte high, byte middle, byte low) => Obd2DtcCodec.Decode(high, middle);
}

/// <summary>UDS read/clear diagnostic subset decode/encode.</summary>
public static class UdsDecoder
{
    /// <summary>Builds a 0x19/0x02 (report DTCs by status mask) request with a status-mask of 0xFF (all).</summary>
    public static byte[] EncodeReadDtcInformation() =>
        new byte[] { UdsService.ReadDtcInformation, UdsService.SubfunctionReportDtcByStatusMask, 0xFF };

    /// <summary>Builds a 0x14 clear request for the "all DTCs" group (0xFFFFFF).</summary>
    public static byte[] EncodeClearDiagnosticInformation() =>
        new byte[] { UdsService.ClearDiagnosticInformation, 0xFF, 0xFF, 0xFF };

    /// <summary>
    /// Decodes a 0x59/0x02 response: <c>[0x59][0x02][statusAvailMask][DTCFormatId][DTC…+status]…</c>.
    /// </summary>
    public static IReadOnlyList<DtcReadout> DecodeReadDtcInformation(ReadOnlySpan<byte> response)
    {
        // [0x59][0x02][statusAvailabilityMask][DTCFormatIdentifier][DTC entries…]
        if (response.Length < 4 ||
            response[0] != UdsService.ReadDtcInformationResponse ||
            response[1] != UdsService.SubfunctionReportDtcByStatusMask)
            return Array.Empty<DtcReadout>();

        var dtcFormat = response[3];
        var dtcWidth = dtcFormat switch
        {
            UdsDtcCodec.FormatIso14229 => 3,
            UdsDtcCodec.FormatIso15031 => 2,
            _ => 0, // SAE J2012-DA (4-byte) and ISO14229-2 formats are not decoded in this round
        };
        if (dtcWidth == 0)
            return Array.Empty<DtcReadout>();

        // Each entry: dtcWidth bytes of DTC + 1 status byte.
        var entrySize = dtcWidth + 1;
        var count = (response.Length - 4) / entrySize;
        var result = new List<DtcReadout>(count);

        for (var i = 0; i < count; i++)
        {
            var offset = 4 + i * entrySize;
            var code = dtcWidth == 3
                ? UdsDtcCodec.Decode3Byte(response[offset], response[offset + 1], response[offset + 2])
                : Obd2DtcCodec.Decode(response[offset], response[offset + 1]);
            var status = response[offset + dtcWidth];

            result.Add(new DtcReadout
            {
                Code = code,
                Protocol = ProtocolType.UDS,
                Spn = null,
                Fmi = null,
                Status = UdsDtcStatus.ToDtcStatus(status),
                IsSimulated = false,
            });
        }

        return result;
    }
}
