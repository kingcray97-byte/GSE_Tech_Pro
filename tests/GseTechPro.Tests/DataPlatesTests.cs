using System.Text.Json.Nodes;
using GseTechPro.Core.Contracts;
using Xunit;

namespace GseTechPro.Tests;

/// <summary>Phase 7.3: deterministic data-plate rules. Values are illustrative test strings, not real plates.</summary>
public sealed class DataPlatesTests
{
    [Fact]
    public void Parse_FindsLabelledValues_SeveralPerLine_AndLabelOnItsOwnLine()
    {
        var found = DataPlates.Parse([
            "ACME GROUND EQUIPMENT",
            "Mfg: Acme Works",
            "MODEL TM-1   SER. NO. 4711-A",
            "P/N: AB-100, PART NO 200-7",
            "MFG DATE 03/2019",
            "S/N",
            "X99 001",
            "Model",
            "SN: 55",       // the label after "Model" has a value, so "Model" takes nothing from it
            "Snow tires",   // "SN" inside a word is not a label
        ]);

        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.Manufacturer, Value: "Acme Works" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.Model, Value: "TM-1" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.SerialNumber, Value: "4711-A" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.PartNumber, Value: "AB-100" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.PartNumber, Value: "200-7" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.ManufactureDate, Value: "03/2019" });
        Assert.DoesNotContain(found, c => c.Kind == DataPlateFieldKind.Manufacturer && c.Value.Contains("DATE"));
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.SerialNumber, Value: "X99 001" });
        Assert.Contains(found, c => c is { Kind: DataPlateFieldKind.SerialNumber, Value: "55" });
        Assert.DoesNotContain(found, c => c.Value.Contains("tires", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, c => c.Kind == DataPlateFieldKind.Model && c.Value.Contains("55"));
        Assert.Equal(3, found.Count(c => c.Kind == DataPlateFieldKind.SerialNumber));
    }

    [Fact]
    public void Parse_KeepsCharactersAsRead_AndHintsFlagLookAlikes()
    {
        var c = Assert.Single(DataPlates.Parse(["S/N: SIM-0O1I7"]));
        Assert.Equal("SIM-0O1I7", c.Value); // never "fixed"

        Assert.Equal("Check look-alike characters on the plate: 0 / O, 1 / I, 5 / S.", DataPlates.LookAlikeHint(c.Value));
        // IDs: any look-alike is flagged (real Windows OCR read "TM-1" as "TM-I" and "AB-100" as "AB-IOO").
        Assert.Equal("Check look-alike characters on the plate: 1 / I.", DataPlates.LookAlikeHint("TM-I"));
        Assert.Contains("0 / O", DataPlates.LookAlikeHint("AB-IOO"));
        Assert.Equal("", DataPlates.LookAlikeHint("TM-7"));
        // Words: only values mixing letters and digits.
        Assert.Equal("", DataPlates.LookAlikeHint("ACME WORKS", idField: false));
        Assert.Contains("0 / O", DataPlates.LookAlikeHint("ACME 2000", idField: false));
        Assert.Equal("", DataPlates.LookAlikeHint(""));

        // Windows OCR often reads the "S/N" label as "SIN".
        Assert.Contains(DataPlates.Parse(["SIN: 4711-A"]), x => x is { Kind: DataPlateFieldKind.SerialNumber, Value: "4711-A" });
        Assert.Contains(DataPlates.Parse(["S1N 9"]), x => x is { Kind: DataPlateFieldKind.SerialNumber, Value: "9" });
    }

    [Fact]
    public void Parse_LabelLettersInsideAHyphenatedValue_DoNotSplitIt()
    {
        // Found in the app check: the simulator plate's "P/N SIM-PN-0001" became two part numbers, "SIM" and "0001".
        var pn = Assert.Single(DataPlates.Parse(["P/N SIM-PN-0001"]));
        Assert.Equal(DataPlateFieldKind.PartNumber, pn.Kind);
        Assert.Equal("SIM-PN-0001", pn.Value);

        var sn = Assert.Single(DataPlates.Parse(["S/N 4711-SN-2"]));
        Assert.Equal("4711-SN-2", sn.Value);

        var model = Assert.Single(DataPlates.Parse(["MODEL TM/MODEL-1"]));
        Assert.Equal("TM/MODEL-1", model.Value);

        // Two labels on one line still split at the space before the second one.
        var both = DataPlates.Parse(["MODEL SIM-100 S/N: SIM-001I7"]);
        Assert.Contains(both, x => x is { Kind: DataPlateFieldKind.Model, Value: "SIM-100" });
        Assert.Contains(both, x => x is { Kind: DataPlateFieldKind.SerialNumber, Value: "SIM-001I7" });
    }

    [Fact]
    public void Region_FromCorners_ClampsAndDropsStrayTaps()
    {
        var r = OcrRegion.FromCorners(0.8, 0.9, 0.2, -0.5)!;
        Assert.Equal(0.2, r.X, 6);
        Assert.Equal(0.0, r.Y, 6);
        Assert.Equal(0.6, r.Width, 6);
        Assert.Equal(0.9, r.Height, 6);
        Assert.Null(OcrRegion.FromCorners(0.5, 0.5, 0.505, 0.9));
    }

    [Fact]
    public void MergeInto_KeepsOtherKeys_RoundTrips_AndRefusesBadJson()
    {
        var reading = new DataPlateReading
        {
            Manufacturer = "  Acme   Works ",
            Model = "TM-1",
            SerialNumber = "4711-A",
            PartNumbers = DataPlates.SplitPartNumbers("AB-100\r\nab-100\n\n200-7, 300"),
            RawText = "MODEL TM-1",
            Engine = "Windows OCR (English)",
            PreviousSerialNumber = "OLD-1",
        };
        Assert.Equal(["AB-100", "200-7", "300"], reading.PartNumbers);

        string json = DataPlates.MergeInto("""{"hydraulics":{"option":"x"}}""", reading);
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("x", (string?)root["hydraulics"]!["option"]);
        Assert.Equal("4711-A", (string?)root["data_plate"]!["serial_number"]);
        Assert.Equal("OLD-1", (string?)root["data_plate"]!["previous_serial_number"]);

        var back = DataPlates.Read(json)!;
        Assert.Equal("Acme Works", back.Manufacturer);
        Assert.Equal(reading.PartNumbers, back.PartNumbers);
        Assert.True(back.TechnicianConfirmed);
        Assert.Equal("Acme Works · Model TM-1 · S/N 4711-A · 3 part numbers", DataPlates.Summary(back));

        // A second reading replaces the first, other keys still kept.
        var again = DataPlates.Read(DataPlates.MergeInto(json, reading with { SerialNumber = "9", IsSimulated = true }))!;
        Assert.Equal("9", again.SerialNumber);
        Assert.StartsWith("SIMULATED ", DataPlates.Summary(again));

        Assert.Throws<FormatException>(() => DataPlates.MergeInto("{not json", reading));
        Assert.Throws<FormatException>(() => DataPlates.MergeInto("[1,2]", reading));
        Assert.Null(DataPlates.Read("{not json"));
        Assert.Null(DataPlates.Read("""{"other":1}"""));
        Assert.Null(DataPlates.Read(null));
    }
}
