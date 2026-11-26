using GseTechPro.Core.Contracts;

namespace GseTechPro.App.Services;

/// <summary>One row in a "Vehicle interfaces" dropdown.</summary>
/// <param name="Selection">Device id, display name and kind; this is what gets saved when picked.</param>
/// <param name="IsMissing">True for the saved device that discovery did not find (kept so the saved value is not lost).</param>
public sealed record VciDeviceChoice(DefaultVciSelection Selection, bool IsMissing)
{
    public string Id => Selection.Id;

    public string Label => IsMissing
        ? $"{(string.IsNullOrWhiteSpace(Selection.Name) ? Selection.Id : Selection.Name)} (not found)"
        : Selection.Name;

    public override string ToString() => Label;
}

/// <summary>The rows for one dropdown plus which one is selected (-1 = nothing saved).</summary>
public sealed record VciDeviceChoiceList(IReadOnlyList<VciDeviceChoice> Choices, int SelectedIndex, bool SavedDeviceMissing);

/// <summary>
/// Builds the dropdown rows for the Vehicle interfaces card from the discovered devices. Pure logic (no UI, no I/O).
/// </summary>
public static class VciDeviceChoices
{
    /// <summary>
    /// The simulator (always selectable) plus every discovered device of <paramref name="kind"/>
    /// (<see cref="DeviceKinds.J2534"/> or <see cref="DeviceKinds.Rp1210"/>). When <paramref name="saved"/> is not
    /// among them it is appended as a "not found" row and stays selected, so the saved value is never silently changed.
    /// </summary>
    public static VciDeviceChoiceList Build(IEnumerable<DeviceInfo> discovered, string kind, DefaultVciSelection? saved)
    {
        var rows = new List<VciDeviceChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(DeviceInfo device)
        {
            if (string.IsNullOrWhiteSpace(device.Id) || !seen.Add(device.Id))
                return;
            rows.Add(new VciDeviceChoice(
                new DefaultVciSelection { Id = device.Id, Name = device.Name, Kind = device.Kind },
                IsMissing: false));
        }

        var all = discovered.ToList();
        // Simulator first, then the real adapters of this kind.
        foreach (var device in all.Where(d => IsKind(d, DeviceKinds.Simulator)))
            Add(device);
        foreach (var device in all.Where(d => IsKind(d, kind)))
            Add(device);

        var selected = -1;
        var missing = false;
        if (saved is not null && !string.IsNullOrWhiteSpace(saved.Id))
        {
            selected = rows.FindIndex(r => string.Equals(r.Id, saved.Id, StringComparison.Ordinal));
            if (selected < 0)
            {
                rows.Add(new VciDeviceChoice(saved, IsMissing: true));
                selected = rows.Count - 1;
                missing = true;
            }
        }

        return new VciDeviceChoiceList(rows, selected, missing);
    }

    private static bool IsKind(DeviceInfo device, string kind) =>
        string.Equals(device.Kind, kind, StringComparison.OrdinalIgnoreCase);
}
