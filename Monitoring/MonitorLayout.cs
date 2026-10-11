namespace SysLens.Monitoring;

/// <summary>
/// One sensor. <paramref name="Key"/> stays the same from run to run for the same hardware, so the
/// user's choices can be saved against it.
/// </summary>
public sealed record SensorInfo(string Key, string Name, SensorKind Kind);

/// <summary>One device, its sensors in display order, and the devices it contains.</summary>
public sealed record HardwareInfo(
    string Key, string Name, HardwareKind Kind, IReadOnlyList<SensorInfo> Sensors, IReadOnlyList<HardwareInfo> SubHardware);

/// <summary>
/// The devices and sensors the monitor reads. A new layout replaces this one whenever hardware comes or goes.
/// </summary>
public sealed class MonitorLayout(IReadOnlyList<HardwareInfo> hardware, IReadOnlyList<SensorInfo> sensors, Func<float?>[] readers)
{
    /// <summary>Top-level devices, most watched first.</summary>
    public IReadOnlyList<HardwareInfo> Hardware { get; } = hardware;

    /// <summary>Every sensor of every device, in the order of <see cref="MonitorSnapshot.Values"/>.</summary>
    public IReadOnlyList<SensorInfo> Sensors { get; } = sensors;

    /// <summary>Whether <paramref name="other"/> lists the same devices by the same names, with the same sensors.</summary>
    public bool IsSameTree(MonitorLayout other) =>
        Sensors.Select(s => s.Key).SequenceEqual(other.Sensors.Select(s => s.Key))
        && Devices(Hardware).SequenceEqual(Devices(other.Hardware));

    /// <summary>Reads each sensor's latest value; monitor thread only.</summary>
    internal float?[] Read() => [.. readers.Select(read => read())];

    private static IEnumerable<(string Key, string Name)> Devices(IEnumerable<HardwareInfo> hardware) =>
        hardware.SelectMany(h => Devices(h.SubHardware).Prepend((h.Key, h.Name)));
}

/// <param name="Values">One per <see cref="MonitorLayout.Sensors"/>; null where the sensor has no reading.</param>
/// <param name="FrameRateApp">The application RTSS measured, or null when no 3D application is rendering.</param>
public sealed record MonitorSnapshot(MonitorLayout Layout, float?[] Values, string? FrameRateApp, DateTime Time);
