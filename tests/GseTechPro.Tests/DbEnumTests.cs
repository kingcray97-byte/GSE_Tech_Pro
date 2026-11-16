using GseTechPro.Core.Enums;
using Xunit;

namespace GseTechPro.Tests;

public class DbEnumTests
{
    [Theory]
    [InlineData(DtcStatus.Active, "active")]
    [InlineData(DtcStatus.Pending, "pending")]
    [InlineData(DtcStatus.Stored, "stored")]
    public void ToCode_DtcStatus_ReturnsCorrectCode(DtcStatus status, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(status));
    }

    [Theory]
    [InlineData("active", DtcStatus.Active)]
    [InlineData("pending", DtcStatus.Pending)]
    [InlineData("stored", DtcStatus.Stored)]
    public void Parse_DtcStatus_ReturnsCorrectValue(string code, DtcStatus expected)
    {
        Assert.Equal(expected, DbEnum.Parse<DtcStatus>(code));
    }

    [Theory]
    [InlineData(ReadingType.HydraulicPressure, "hydraulic_pressure")]
    [InlineData(ReadingType.Flow, "flow")]
    [InlineData(ReadingType.Temperature, "temperature")]
    [InlineData(ReadingType.Voltage, "voltage")]
    public void ToCode_ReadingType_ReturnsCorrectCode(ReadingType type, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(type));
    }

    [Theory]
    [InlineData("hydraulic_pressure", ReadingType.HydraulicPressure)]
    [InlineData("flow", ReadingType.Flow)]
    [InlineData("temperature", ReadingType.Temperature)]
    public void Parse_ReadingType_ReturnsCorrectValue(string code, ReadingType expected)
    {
        Assert.Equal(expected, DbEnum.Parse<ReadingType>(code));
    }

    [Theory]
    [InlineData(OutcomeResult.Fixed, "fixed")]
    [InlineData(OutcomeResult.NotFixed, "not_fixed")]
    [InlineData(OutcomeResult.Unsure, "unsure")]
    public void ToCode_OutcomeResult_ReturnsCorrectCode(OutcomeResult result, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(result));
    }

    [Theory]
    [InlineData("fixed", OutcomeResult.Fixed)]
    [InlineData("not_fixed", OutcomeResult.NotFixed)]
    public void Parse_OutcomeResult_ReturnsCorrectValue(string code, OutcomeResult expected)
    {
        Assert.Equal(expected, DbEnum.Parse<OutcomeResult>(code));
    }

    [Theory]
    [InlineData(PassFail.Pass, "pass")]
    [InlineData(PassFail.Fail, "fail")]
    [InlineData(PassFail.NotApplicable, "na")]
    public void ToCode_PassFail_ReturnsCorrectCode(PassFail result, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(result));
    }

    [Theory]
    [InlineData("pass", PassFail.Pass)]
    [InlineData("fail", PassFail.Fail)]
    [InlineData("na", PassFail.NotApplicable)]
    public void Parse_PassFail_ReturnsCorrectValue(string code, PassFail expected)
    {
        Assert.Equal(expected, DbEnum.Parse<PassFail>(code));
    }

    [Theory]
    [InlineData(ReadingSource.Manual, "manual")]
    [InlineData(ReadingSource.Voice, "voice")]
    [InlineData(ReadingSource.Scope, "scope")]
    [InlineData(ReadingSource.Scan, "scan")]
    public void ToCode_ReadingSource_ReturnsCorrectCode(ReadingSource source, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(source));
    }

    [Theory]
    [InlineData("manual", ReadingSource.Manual)]
    [InlineData("voice", ReadingSource.Voice)]
    public void Parse_ReadingSource_ReturnsCorrectValue(string code, ReadingSource expected)
    {
        Assert.Equal(expected, DbEnum.Parse<ReadingSource>(code));
    }

    [Theory]
    [InlineData(DtcSource.Scan, "scan")]
    [InlineData(DtcSource.Manual, "manual")]
    public void ToCode_DtcSource_ReturnsCorrectCode(DtcSource source, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(source));
    }

    [Theory]
    [InlineData("scan", DtcSource.Scan)]
    [InlineData("manual", DtcSource.Manual)]
    public void Parse_DtcSource_ReturnsCorrectValue(string code, DtcSource expected)
    {
        Assert.Equal(expected, DbEnum.Parse<DtcSource>(code));
    }

    [Theory]
    [InlineData(SymptomCategory.Engine, "engine")]
    [InlineData(SymptomCategory.Hydraulic, "hydraulic")]
    [InlineData(SymptomCategory.Electrical, "electrical")]
    public void ToCode_SymptomCategory_ReturnsCorrectCode(SymptomCategory category, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(category));
    }

    [Theory]
    [InlineData("engine", SymptomCategory.Engine)]
    [InlineData("hydraulic", SymptomCategory.Hydraulic)]
    public void Parse_SymptomCategory_ReturnsCorrectValue(string code, SymptomCategory expected)
    {
        Assert.Equal(expected, DbEnum.Parse<SymptomCategory>(code));
    }

    [Theory]
    [InlineData(RecordedBy.Text, "text")]
    [InlineData(RecordedBy.Voice, "voice")]
    public void ToCode_RecordedBy_ReturnsCorrectCode(RecordedBy by, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(by));
    }

    [Theory]
    [InlineData("text", RecordedBy.Text)]
    [InlineData("voice", RecordedBy.Voice)]
    public void Parse_RecordedBy_ReturnsCorrectValue(string code, RecordedBy expected)
    {
        Assert.Equal(expected, DbEnum.Parse<RecordedBy>(code));
    }

    [Theory]
    [InlineData(NoteScope.WorkOrder, "work_order")]
    [InlineData(NoteScope.Asset, "asset")]
    [InlineData(NoteScope.Technician, "technician")]
    public void ToCode_NoteScope_ReturnsCorrectCode(NoteScope scope, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(scope));
    }

    [Theory]
    [InlineData("work_order", NoteScope.WorkOrder)]
    [InlineData("asset", NoteScope.Asset)]
    public void Parse_NoteScope_ReturnsCorrectValue(string code, NoteScope expected)
    {
        Assert.Equal(expected, DbEnum.Parse<NoteScope>(code));
    }

    [Theory]
    [InlineData(AttachmentKind.Photo, "photo")]
    [InlineData(AttachmentKind.Waveform, "waveform")]
    [InlineData(AttachmentKind.Recording, "recording")]
    public void ToCode_AttachmentKind_ReturnsCorrectCode(AttachmentKind kind, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(kind));
    }

    [Theory]
    [InlineData("photo", AttachmentKind.Photo)]
    [InlineData("waveform", AttachmentKind.Waveform)]
    public void Parse_AttachmentKind_ReturnsCorrectValue(string code, AttachmentKind expected)
    {
        Assert.Equal(expected, DbEnum.Parse<AttachmentKind>(code));
    }

    [Theory]
    [InlineData(EquipmentCategory.Tug, "tug")]
    [InlineData(EquipmentCategory.BeltLoader, "belt_loader")]
    [InlineData(EquipmentCategory.Gpu, "gpu")]
    public void ToCode_EquipmentCategory_ReturnsCorrectCode(EquipmentCategory category, string expected)
    {
        Assert.Equal(expected, DbEnum.ToCode(category));
    }

    [Theory]
    [InlineData("tug", EquipmentCategory.Tug)]
    [InlineData("belt_loader", EquipmentCategory.BeltLoader)]
    public void Parse_EquipmentCategory_ReturnsCorrectValue(string code, EquipmentCategory expected)
    {
        Assert.Equal(expected, DbEnum.Parse<EquipmentCategory>(code));
    }

    [Fact]
    public void Parse_UnknownCode_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DbEnum.Parse<DtcStatus>("bogus"));
        Assert.Contains("DtcStatus", ex.Message);
    }

    [Fact]
    public void Parse_NullCode_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => DbEnum.Parse<DtcStatus>(null));
    }

    [Fact]
    public void RoundTrip_DtcStatus()
    {
        foreach (DtcStatus status in Enum.GetValues<DtcStatus>())
        {
            var code = DbEnum.ToCode(status);
            var parsed = DbEnum.Parse<DtcStatus>(code);
            Assert.Equal(status, parsed);
        }
    }

    [Fact]
    public void RoundTrip_ReadingType()
    {
        foreach (ReadingType type in Enum.GetValues<ReadingType>())
        {
            var code = DbEnum.ToCode(type);
            var parsed = DbEnum.Parse<ReadingType>(code);
            Assert.Equal(type, parsed);
        }
    }

    [Fact]
    public void RoundTrip_OutcomeResult()
    {
        foreach (OutcomeResult result in Enum.GetValues<OutcomeResult>())
        {
            var code = DbEnum.ToCode(result);
            var parsed = DbEnum.Parse<OutcomeResult>(code);
            Assert.Equal(result, parsed);
        }
    }

    [Fact]
    public void RoundTrip_PassFail()
    {
        foreach (PassFail result in Enum.GetValues<PassFail>())
        {
            var code = DbEnum.ToCode(result);
            var parsed = DbEnum.Parse<PassFail>(code);
            Assert.Equal(result, parsed);
        }
    }

    [Fact]
    public void RoundTrip_ReadingSource()
    {
        foreach (ReadingSource source in Enum.GetValues<ReadingSource>())
        {
            var code = DbEnum.ToCode(source);
            var parsed = DbEnum.Parse<ReadingSource>(code);
            Assert.Equal(source, parsed);
        }
    }

    [Fact]
    public void RoundTrip_DtcSource()
    {
        foreach (DtcSource source in Enum.GetValues<DtcSource>())
        {
            var code = DbEnum.ToCode(source);
            var parsed = DbEnum.Parse<DtcSource>(code);
            Assert.Equal(source, parsed);
        }
    }

    [Fact]
    public void RoundTrip_SymptomCategory()
    {
        foreach (SymptomCategory category in Enum.GetValues<SymptomCategory>())
        {
            var code = DbEnum.ToCode(category);
            var parsed = DbEnum.Parse<SymptomCategory>(code);
            Assert.Equal(category, parsed);
        }
    }

    [Fact]
    public void RoundTrip_RecordedBy()
    {
        foreach (RecordedBy by in Enum.GetValues<RecordedBy>())
        {
            var code = DbEnum.ToCode(by);
            var parsed = DbEnum.Parse<RecordedBy>(code);
            Assert.Equal(by, parsed);
        }
    }

    [Fact]
    public void RoundTrip_NoteScope()
    {
        foreach (NoteScope scope in Enum.GetValues<NoteScope>())
        {
            var code = DbEnum.ToCode(scope);
            var parsed = DbEnum.Parse<NoteScope>(code);
            Assert.Equal(scope, parsed);
        }
    }

    [Fact]
    public void RoundTrip_AttachmentKind()
    {
        foreach (AttachmentKind kind in Enum.GetValues<AttachmentKind>())
        {
            var code = DbEnum.ToCode(kind);
            var parsed = DbEnum.Parse<AttachmentKind>(code);
            Assert.Equal(kind, parsed);
        }
    }

    [Fact]
    public void RoundTrip_EquipmentCategory()
    {
        foreach (EquipmentCategory category in Enum.GetValues<EquipmentCategory>())
        {
            var code = DbEnum.ToCode(category);
            var parsed = DbEnum.Parse<EquipmentCategory>(code);
            Assert.Equal(category, parsed);
        }
    }

    [Fact]
    public void Parse_CaseInsensitive_MemberName()
    {
        // Should accept the member name case-insensitively
        Assert.Equal(DtcStatus.Active, DbEnum.Parse<DtcStatus>("Active"));
        Assert.Equal(DtcStatus.Active, DbEnum.Parse<DtcStatus>("ACTIVE"));
    }
}
