using LibreHardwareMonitor.Hardware;

namespace SysLens.Monitoring;

/// <summary>
/// Collects the sensors of a <see cref="MonitorLayout"/> and how to read each, under keys unique within the layout.
/// </summary>
internal sealed class LayoutBuilder
{
    private readonly List<SensorInfo> _sensors = [];
    private readonly List<Func<float?>> _readers = [];
    private readonly HashSet<string> _keys = [];

    public SensorInfo Add(string key, string name, SensorKind kind, Func<float?> read)
    {
        // LibreHardwareMonitor gives some sensors the same identifier, such as two voltages of one graphics card.
        var unique = key;
        for (var n = 2; !_keys.Add(unique); n++)
            unique = $"{key}#{n}";

        var info = new SensorInfo(unique, name, kind);
        _sensors.Add(info);
        _readers.Add(read);
        return info;
    }

    /// <summary>LibreHardwareMonitor's sensors, leaving out those of a kind SysLens does not show.</summary>
    public List<SensorInfo> Add(IEnumerable<ISensor> sensors)
    {
        var added = new List<SensorInfo>();
        foreach (var sensor in sensors)
        {
            if (!TryMap(sensor.SensorType, out SensorKind kind))
                continue;
            // SysLens keeps its own history; LibreHardwareMonitor's would grow for a day.
            sensor.ValuesTimeWindow = TimeSpan.Zero;
            added.Add(Add(sensor.Identifier.ToString(), sensor.Name, kind, () => sensor.Value));
        }
        return added;
    }

    public MonitorLayout Build(IReadOnlyList<HardwareInfo> hardware) => new(hardware, _sensors, [.. _readers]);

    /// <summary>Matches a LibreHardwareMonitor enum value to SysLens's value of the same name.</summary>
    public static bool TryMap<T>(Enum value, out T mapped) where T : struct, Enum =>
        Enum.TryParse(value.ToString(), out mapped) && Enum.IsDefined(mapped);
}
