using System.Globalization;
using GseTechPro.Core.Enums;

namespace GseTechPro.Devices;

/// <summary>
/// Offline VIN decoder that extracts make, region, model year and engine from a 17-character VIN
/// using published SAE/ISO rules only. No network lookup, no invented mappings.
/// </summary>
public static class VinDecoder
{
    /// <summary>
    /// Decoding result. Unknown or unmapped fields are returned as <c>null</c>.
    /// </summary>
    public sealed record Result(string? Make, VehicleRegion? Region, int? Year, string? Engine);

    // SAE J1042 / ISO 3780 world manufacturer identifier prefixes for common makes.
    // This is intentionally a conservative starter set; unknown WMIs leave Make null.
    private static readonly Dictionary<string, string> WmiToMake = new(StringComparer.Ordinal)
    {
        // Ford
        ["1FA"] = "Ford",
        ["1FB"] = "Ford",
        ["1FD"] = "Ford",
        ["1FM"] = "Ford",
        ["1FT"] = "Ford",
        ["2FM"] = "Ford",
        ["3FA"] = "Ford",
        ["3FT"] = "Ford",

        // General Motors
        ["1G1"] = "Chevrolet",
        ["1G2"] = "Pontiac",
        ["1G3"] = "Oldsmobile",
        ["1G4"] = "Buick",
        ["1G6"] = "Cadillac",
        ["1GC"] = "Chevrolet",
        ["1GT"] = "GMC",
        ["1GY"] = "Cadillac",
        ["2G1"] = "Chevrolet",
        ["2G2"] = "Pontiac",
        ["2G3"] = "Oldsmobile",
        ["2G4"] = "Buick",
        ["2G6"] = "Cadillac",
        ["2GC"] = "Chevrolet",
        ["2GT"] = "GMC",
        ["3G1"] = "Chevrolet",
        ["3GC"] = "Chevrolet",
        ["3GT"] = "GMC",

        // Stellantis / Chrysler
        ["1C3"] = "Chrysler",
        ["1C4"] = "Chrysler",
        ["1C6"] = "Ram",
        ["2C3"] = "Chrysler",
        ["3C6"] = "Ram",

        // Toyota / Lexus
        ["4T1"] = "Toyota",
        ["4T3"] = "Toyota",
        ["5TD"] = "Toyota",
        ["5TF"] = "Toyota",
        ["JT2"] = "Toyota",
        ["JT3"] = "Toyota",
        ["JT4"] = "Toyota",
        ["JT5"] = "Toyota",
        ["JTD"] = "Toyota",
        ["JTE"] = "Toyota",
        ["JTH"] = "Lexus",
        ["JT6"] = "Lexus",
        ["JT8"] = "Lexus",
        ["58A"] = "Lexus",

        // Honda / Acura
        ["1HG"] = "Honda",
        ["2HG"] = "Honda",
        ["3HG"] = "Honda",
        ["JHM"] = "Honda",
        ["19U"] = "Acura",
        ["JH4"] = "Acura",

        // Nissan / Infiniti
        ["1N4"] = "Nissan",
        ["1N6"] = "Nissan",
        ["3N1"] = "Nissan",
        ["JN1"] = "Nissan",
        ["JN6"] = "Nissan",
        ["JNK"] = "Infiniti",
        ["5N3"] = "Infiniti",

        // Hyundai / Kia
        ["KMH"] = "Hyundai",
        ["KM8"] = "Hyundai",
        ["KMJ"] = "Hyundai",
        ["5NM"] = "Hyundai",
        ["5NP"] = "Hyundai",
        ["KNA"] = "Kia",
        ["KNB"] = "Kia",
        ["KNC"] = "Kia",
        ["KND"] = "Kia",
        ["KN3"] = "Kia",
        ["KN4"] = "Kia",

        // Volkswagen Group
        ["1VW"] = "Volkswagen",
        ["3VW"] = "Volkswagen",
        ["WVW"] = "Volkswagen",
        ["WVG"] = "Volkswagen",
        ["WAU"] = "Audi",
        ["WA1"] = "Audi",
        ["TRU"] = "Audi",

        // BMW
        ["WBA"] = "BMW",
        ["WBS"] = "BMW",
        ["WBX"] = "BMW",
        ["5UX"] = "BMW",

        // Mercedes-Benz
        ["WDB"] = "Mercedes-Benz",
        ["WDC"] = "Mercedes-Benz",
        ["WDD"] = "Mercedes-Benz",
        ["4JG"] = "Mercedes-Benz",

        // Tesla
        ["5YJ"] = "Tesla",
        ["7SA"] = "Tesla",
        ["LRW"] = "Tesla",

        // Subaru
        ["JF1"] = "Subaru",
        ["JF2"] = "Subaru",
        ["4S3"] = "Subaru",
        ["4S4"] = "Subaru",

        // Mazda
        ["JM1"] = "Mazda",
        ["JM3"] = "Mazda",
        ["1YV"] = "Mazda",
        ["3MZ"] = "Mazda",

        // Mitsubishi
        ["JA3"] = "Mitsubishi",
        ["JA4"] = "Mitsubishi",
        ["4A3"] = "Mitsubishi",
        ["4A4"] = "Mitsubishi",

        // Volvo
        ["YV1"] = "Volvo",
        ["YV2"] = "Volvo",
        ["YV4"] = "Volvo",
        ["7JR"] = "Volvo",

        // Jaguar / Land Rover
        ["SAJ"] = "Jaguar",
        ["SAD"] = "Jaguar",
        ["SAL"] = "Land Rover",
        ["SAR"] = "Land Rover",
        ["SJA"] = "Land Rover",

        // Porsche
        ["WP0"] = "Porsche",
        ["WP1"] = "Porsche",

        // Heavy-duty
        ["1M1"] = "Mack",
        ["1M2"] = "Mack",
        ["1M3"] = "Mack",
        ["1HT"] = "International",
        ["1HV"] = "International",
        ["1HS"] = "International",
        ["1HW"] = "International",
        ["3HS"] = "International",
        ["1NP"] = "Peterbilt",
        ["1XP"] = "Peterbilt",
        ["1XK"] = "Kenworth",
        ["2XK"] = "Kenworth",
        ["3AK"] = "Kenworth",
        ["1FU"] = "Freightliner",
        ["1FV"] = "Freightliner",
        ["3AL"] = "Freightliner",
        ["4UZ"] = "Freightliner",
        ["4V5"] = "Freightliner",
        ["JA7"] = "Isuzu",
        ["JAA"] = "Isuzu",
        ["JAB"] = "Isuzu",
        ["54B"] = "Isuzu",
        ["MLC"] = "Isuzu",
    };

    private static readonly int[] Weights =
    [
        8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2
    ];

    /// <summary>
    /// Decodes the supplied VIN. Returns <c>null</c> if the VIN is structurally invalid.
    /// </summary>
    public static Result? Decode(string? vin)
    {
        if (string.IsNullOrWhiteSpace(vin))
            return null;

        vin = vin.Trim().ToUpperInvariant();

        if (vin.Length != 17)
            return null;

        // The simulator returns a placeholder VIN that is useful for UI/dev flows but is
        // not a valid 17-character VIN (it contains an 'I'). Decode it predictably.
        if (vin == "SIMULATED00000001")
            return new Result(null, VehicleRegion.European, null, null);

        foreach (char c in vin)
        {
            if (c is 'I' or 'O' or 'Q' || !char.IsLetterOrDigit(c))
                return null;
        }

        VehicleRegion? region = DecodeRegion(vin[0]);
        if (region is null)
            return null;

        // North-American VINs must pass the ISO 3779 check-digit test at position 9.
        if (region == VehicleRegion.Domestic && !IsCheckDigitValid(vin))
            return null;

        string wmi = vin[..3];
        WmiToMake.TryGetValue(wmi, out string? make);

        int? year = DecodeYear(vin[9]);

        // VIN position 8 describes engine/trim; publicly verified mappings are not shipped.
        string? engine = null; // TODO: only where publicly mapped; no verified mapping shipped

        return new Result(make, region, year, engine);
    }

    private static VehicleRegion? DecodeRegion(char first)
    {
        if (first is >= '1' and <= '5' or '7')
            return VehicleRegion.Domestic;

        if (first is >= 'J' and <= 'R')
            return VehicleRegion.Asian;

        if (first is >= 'S' and <= 'Z')
            return VehicleRegion.European;

        return null;
    }

    private static int? DecodeYear(char yearCode)
    {
        int? offset = yearCode switch
        {
            'A' => 0,
            'B' => 1,
            'C' => 2,
            'D' => 3,
            'E' => 4,
            'F' => 5,
            'G' => 6,
            'H' => 7,
            'J' => 8,
            'K' => 9,
            'L' => 10,
            'M' => 11,
            'N' => 12,
            'P' => 13,
            'R' => 14,
            'S' => 15,
            'T' => 16,
            'V' => 17,
            'W' => 18,
            'X' => 19,
            'Y' => 20,
            '1' => 21,
            '2' => 22,
            '3' => 23,
            '4' => 24,
            '5' => 25,
            '6' => 26,
            '7' => 27,
            '8' => 28,
            '9' => 29,
            _ => null,
        };

        if (offset is null)
            return null;

        int baseYear = 1980 + offset.Value;
        int currentYear = DateTime.UtcNow.Year;

        // Move to the most recent 30-year cycle that is not in the future.
        int year = baseYear;
        while (year + 30 <= currentYear)
            year += 30;
        while (year > currentYear)
            year -= 30;

        return year;
    }

    private static bool IsCheckDigitValid(ReadOnlySpan<char> vin)
    {
        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            int value = Transliterate(vin[i]);
            if (value < 0)
                return false;

            sum += value * Weights[i];
        }

        int remainder = sum % 11;
        char expected = remainder == 10 ? 'X' : (char)('0' + remainder);
        return vin[8] == expected;
    }

    private static int Transliterate(char c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';

        return c switch
        {
            'A' => 1,
            'B' => 2,
            'C' => 3,
            'D' => 4,
            'E' => 5,
            'F' => 6,
            'G' => 7,
            'H' => 8,
            'J' => 1,
            'K' => 2,
            'L' => 3,
            'M' => 4,
            'N' => 5,
            'P' => 7,
            'R' => 9,
            'S' => 2,
            'T' => 3,
            'U' => 4,
            'V' => 5,
            'W' => 6,
            'X' => 7,
            'Y' => 8,
            'Z' => 9,
            _ => -1,
        };
    }
}
