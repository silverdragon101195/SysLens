using SysLens.Native;
using Drive = LibreHardwareMonitor.Hardware.Storage.StorageDevice;

namespace SysLens.Monitoring;

/// <summary>
/// The disks Windows numbers, as Task Manager lists them: each named with its drive letters, with its active time and
/// read and write speeds from the counters Windows keeps for Task Manager, and with the sensors LibreHardwareMonitor
/// reads from its drive. A RAID volume holds the drives it is made of. Monitor thread only.
/// </summary>
internal sealed class DiskReader
{
    // LibreHardwareMonitor reads these through the device path DiskInfoToolkit gives a drive, which for a drive behind
    // Intel RST is the controller's (\\.\SCSI0:), so they read nothing there. The disk's own counters replace them.
    private static readonly HashSet<string> ActivitySensors =
        ["Read Activity", "Write Activity", "Total Activity", "Read Rate", "Write Rate"];

    // What a volume holds, rather than the drives it is made of.
    private static readonly HashSet<string> SpaceSensors = ["Used Space", "Free Space", "Total Space"];

    private readonly Dictionary<PhysicalDisk, DiskCounters> _counters = [];
    private readonly Dictionary<PhysicalDisk, DiskRates> _rates = [];
    private readonly Dictionary<RstNvmeDrive, NvmeHealth> _health = [];
    private List<PhysicalDisk> _disks = [];
    private Dictionary<int, string> _letters = [];

    // The NVMe drives of RAID volumes that SysLens reads itself, through Intel RST.
    private List<RstNvmeDrive> _nvme = [];

    /// <summary>
    /// Reads each disk's counters and each NVMe RAID drive's health. Returns true when the drive letters changed since
    /// <see cref="Describe"/>, such as when a volume is mounted after its disk arrived.
    /// </summary>
    public bool Read()
    {
        _rates.Clear();
        foreach (var disk in _disks)
        {
            if (PhysicalDisks.ReadCounters(disk.Number) is not { } counters)
            {
                _counters.Remove(disk);
                continue;
            }
            if (_counters.TryGetValue(disk, out var last) && Rates(last, counters) is { } rates)
                _rates[disk] = rates;
            _counters[disk] = counters;
        }
        ReadHealth(_nvme);

        var letters = PhysicalDisks.DriveLetters();
        return letters.Count != _letters.Count || letters.Any(l => _letters.GetValueOrDefault(l.Key) != l.Value);
    }

    /// <summary>
    /// Reads the disks afresh and describes those with a drive letter or a drive LibreHardwareMonitor reads, in drive
    /// letter order, then the drives LibreHardwareMonitor reads on none of them.
    /// </summary>
    public List<HardwareInfo> Describe(LayoutBuilder layout, IReadOnlyCollection<Drive> drives)
    {
        _disks = PhysicalDisks.Enumerate();
        _letters = PhysicalDisks.DriveLetters();
        foreach (var gone in _counters.Keys.Except(_disks).ToList())
            _counters.Remove(gone);
        // A first reading of a new disk, so the next read has its rates.
        foreach (var disk in _disks.Where(d => !_counters.ContainsKey(d)))
        {
            if (PhysicalDisks.ReadCounters(disk.Number) is { } counters)
                _counters[disk] = counters;
        }

        var volumes = RaidVolumes(_disks);
        var nvme = FindNvme(volumes, drives);
        _nvme = [];
        var placed = new HashSet<Drive>();
        var described = new List<(string Letters, HardwareInfo Info)>();
        foreach (var disk in _disks)
        {
            var drive = drives.FirstOrDefault(d => d.Storage.StorageDeviceNumber == disk.Number);
            var members = volumes.TryGetValue(disk.Number, out var volume) ? Members(volume, drive, drives, nvme) : [];
            var letters = _letters.GetValueOrDefault(disk.Number, "");
            // Such as a card reader without a card.
            if (drive is null && members.Count == 0 && letters.Length == 0)
                continue;

            var key = drive?.Identifier.ToString() ?? $"/disk/{disk.Number}";
            List<SensorInfo> sensors =
            [
                layout.Add($"{key}/load/active", "Active Time", SensorKind.Load, () => Rate(disk, r => r.ActiveTime)),
                layout.Add($"{key}/throughput/read", "Read Speed", SensorKind.Throughput, () => Rate(disk, r => r.ReadSpeed)),
                layout.Add($"{key}/throughput/write", "Write Speed", SensorKind.Throughput, () => Rate(disk, r => r.WriteSpeed)),
            ];
            if (drive is not null)
            {
                // DiskInfoToolkit can read a member drive through its RAID volume and report it as the volume. Its
                // space sensors then measure the volume, and the rest that member.
                var isMember = members.Any(m => m.Drive == drive);
                sensors.AddRange(layout.Add(drive.Sensors.Where(s =>
                    isMember ? SpaceSensors.Contains(s.Name) : !ActivitySensors.Contains(s.Name))));
                placed.Add(drive);
            }
            placed.UnionWith(members.Select(m => m.Drive).OfType<Drive>());

            // A RAID volume goes by its own name, not by that of the member drive DiskInfoToolkit read through it.
            var name = (disk.IsRaid || drive is null) && disk.Product.Length > 0 ? disk.Product : drive?.Name ?? $"Disk {disk.Number}";
            List<HardwareInfo> memberInfos = [.. members.Select(m => DescribeMember(layout, m, drive, key))];
            var title = letters.Length > 0 ? $"{name} ({letters})" : name;
            described.Add((letters, new HardwareInfo(key, title, HardwareKind.Storage, sensors, memberInfos)));
        }
        foreach (var gone in _health.Keys.Except(_nvme).ToList())
            _health.Remove(gone);

        // Disks without a drive letter go last, in disk number order.
        List<HardwareInfo> ordered =
            [.. described.OrderBy(d => d.Letters.Length == 0).ThenBy(d => d.Letters, StringComparer.Ordinal).Select(d => d.Info)];

        // Such as the members of a RAID volume whose driver does not say what the volume is made of.
        foreach (var drive in drives.Where(d => !placed.Contains(d)))
        {
            ordered.Add(new HardwareInfo(drive.Identifier.ToString(), drive.Name, HardwareKind.Storage,
                layout.Add(drive.Sensors.Where(s => !ActivitySensors.Contains(s.Name))), []));
        }
        return ordered;
    }

    private float? Rate(PhysicalDisk disk, Func<DiskRates, float> pick) =>
        _rates.TryGetValue(disk, out var rates) ? pick(rates) : null;

    /// <summary>A drive of a RAID volume; a SATA drive is named with the port of the controller it is on.</summary>
    private HardwareInfo DescribeMember(LayoutBuilder layout, Member member, Drive? volumeDrive, string volumeKey)
    {
        if (member.Drive is not { } drive)
            return DescribeNvme(layout, member.Nvme!);

        var key = drive == volumeDrive ? $"{volumeKey}/member" : drive.Identifier.ToString();
        // DiskInfoToolkit names the NVMe drive it reads through its volume after the volume.
        var name = member.Nvme?.Model ?? (drive.Storage.Csmi.PortIdentifier is { } port ? $"{drive.Name} (Port {port})" : drive.Name);
        var sensors = layout.Add(drive.Sensors.Where(s => !ActivitySensors.Contains(s.Name) && !SpaceSensors.Contains(s.Name)));
        return new HardwareInfo(key, name, HardwareKind.Storage, sensors, []);
    }

    /// <summary>
    /// An NVMe drive SysLens reads through Intel RST, with the sensors LibreHardwareMonitor gives an NVMe drive, by the
    /// same names.
    /// </summary>
    private HardwareInfo DescribeNvme(LayoutBuilder layout, RstNvmeDrive drive)
    {
        _nvme.Add(drive);
        ReadHealth([drive]);
        var key = $"/intel-nvme/{Uri.EscapeDataString(drive.Serial)}";
        float? Value(Func<NvmeHealth, float?> pick) => _health.TryGetValue(drive, out var health) ? pick(health) : null;

        List<SensorInfo> sensors =
            [layout.Add($"{key}/temperature/0", "Temperature", SensorKind.Temperature, () => Value(h => h.Temperature))];
        // The temperature sensors the drive has, as its first reading shows.
        var first = _health.GetValueOrDefault(drive);
        for (var i = 0; i < 8; i++)
        {
            var index = i;
            if (first?.Sensors[index] is not null)
            {
                sensors.Add(layout.Add($"{key}/temperature/{index + 1}", $"Temperature #{index + 1}", SensorKind.Temperature,
                    () => Value(h => h.Sensors[index])));
            }
        }
        if (drive.WarningTemperature is { } warning)
            sensors.Add(layout.Add($"{key}/temperature/10", "Warning Temperature", SensorKind.Temperature, () => warning));
        if (drive.CriticalTemperature is { } critical)
            sensors.Add(layout.Add($"{key}/temperature/11", "Critical Temperature", SensorKind.Temperature, () => critical));
        sensors.AddRange(
        [
            layout.Add($"{key}/level/20", "Life", SensorKind.Level, () => Value(h => Math.Max(0, 100 - h.PercentageUsed))),
            layout.Add($"{key}/data/21", "Data Read", SensorKind.Data, () => Value(h => (float)h.DataRead)),
            layout.Add($"{key}/data/22", "Data Written", SensorKind.Data, () => Value(h => (float)h.DataWritten)),
            layout.Add($"{key}/factor/23", "Power On Count", SensorKind.Factor, () => Value(h => (float)h.PowerCycles)),
            layout.Add($"{key}/factor/24", "Power On Hours", SensorKind.Factor, () => Value(h => (float)h.PowerOnHours)),
            layout.Add($"{key}/level/25", "Available Spare", SensorKind.Level, () => Value(h => h.AvailableSpare)),
            layout.Add($"{key}/level/26", "Available Spare Threshold", SensorKind.Level, () => Value(h => h.AvailableSpareThreshold)),
            layout.Add($"{key}/level/27", "Percentage Used", SensorKind.Level, () => Value(h => h.PercentageUsed)),
        ]);
        return new HardwareInfo(key, drive.Model, HardwareKind.Storage, sensors, []);
    }

    private void ReadHealth(IEnumerable<RstNvmeDrive> drives)
    {
        foreach (var controller in drives.GroupBy(d => d.Location.Port))
        {
            using var handle = DeviceIo.OpenController(controller.Key);
            foreach (var drive in controller)
            {
                if (!handle.IsInvalid && IntelNvme.ReadHealth(handle, drive.Location) is { } health)
                    _health[drive] = health;
                else
                    _health.Remove(drive);
            }
        }
    }

    /// <summary>
    /// The NVMe drives Intel RST reaches on each controller holding a RAID volume made of drives LibreHardwareMonitor
    /// does not read. The places of the disks Windows lists are left out: those drives are read already, and a RAID
    /// volume's place answers for its first drive.
    /// </summary>
    private List<RstNvmeDrive> FindNvme(Dictionary<int, RaidSet> volumes, IReadOnlyCollection<Drive> drives)
    {
        var found = new List<RstNvmeDrive>();
        foreach (var controller in _disks.Where(d => d.Location is not null).GroupBy(d => d.Location!.Value.Port))
        {
            var unread = controller.Where(d => volumes.ContainsKey(d.Number))
                .SelectMany(d => volumes[d.Number].Members)
                .Any(serial => !drives.Any(d => SameSerial(serial, d.Storage.SerialNumber)));
            if (unread)
                found.AddRange(IntelNvme.Find(controller.Key, controller.Select(d => d.Location!.Value).ToHashSet()));
        }
        return found;
    }

    /// <summary>
    /// The drives of a RAID volume, in its order: drives LibreHardwareMonitor reads, by serial number, and NVMe drives
    /// Intel RST reaches, by their namespace's id. DiskInfoToolkit reads one NVMe drive of a volume through the volume
    /// itself; <paramref name="volumeDrive"/> then stands for that drive.
    /// </summary>
    private static List<Member> Members(RaidSet volume, Drive? volumeDrive, IReadOnlyCollection<Drive> drives,
        List<RstNvmeDrive> nvme)
    {
        var members = new List<Member>();
        foreach (var serial in volume.Members)
        {
            if (drives.FirstOrDefault(d => SameSerial(serial, d.Storage.SerialNumber)) is { } drive)
                members.Add(new Member(drive, null));
            else if (nvme.FirstOrDefault(n => n.NamespaceIds.Any(id => SameSerial(serial, id))) is { } found)
                members.Add(new Member(SameSerial(found.Serial, volumeDrive?.Storage.SerialNumber) ? volumeDrive : null, found));
        }
        return members;
    }

    private static bool SameSerial(string serial, string? other) =>
        serial.Length > 0 && string.Equals(serial, other?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>The RAID volumes the controllers' drivers report, by the number of their disk.</summary>
    private static Dictionary<int, RaidSet> RaidVolumes(List<PhysicalDisk> disks)
    {
        var volumes = new Dictionary<int, RaidSet>();
        foreach (var controller in disks.Where(d => d is { IsRaid: true, Location: not null }).GroupBy(d => d.Location!.Value.Port))
        {
            foreach (var set in CsmiRaid.Read(controller.Key))
            {
                // Intel RST reports a volume's name as its serial number; failing that, the volume is the only one
                // there of its capacity.
                var sized = controller.Where(d => d.Size == set.Capacity).ToList();
                var disk = controller.FirstOrDefault(d => set.Name.Length > 0 && d.Serial == set.Name)
                           ?? (sized.Count == 1 ? sized[0] : null);
                if (disk is not null)
                    volumes.TryAdd(disk.Number, set);
            }
        }
        return volumes;
    }

    /// <summary>The rates between two readings, or null when the counters were reset in between.</summary>
    private static DiskRates? Rates(DiskCounters last, DiskCounters now)
    {
        var elapsed = now.QueryTime - last.QueryTime;
        var read = now.BytesRead - last.BytesRead;
        var written = now.BytesWritten - last.BytesWritten;
        var idle = now.IdleTime - last.IdleTime;
        if (elapsed <= 0 || read < 0 || written < 0 || idle < 0)
            return null;

        // Idle time can run slightly ahead of the clock it is read against.
        var seconds = elapsed / 1e7;
        return new DiskRates(
            (float)Math.Clamp(100 - 100.0 * idle / elapsed, 0, 100),
            (float)(read / seconds),
            (float)(written / seconds));
    }

    /// <param name="ActiveTime">
    /// The share of the time the disk had I/O in flight, in percent: Task Manager's active time.
    /// </param>
    /// <param name="ReadSpeed">In bytes per second.</param>
    /// <param name="WriteSpeed">In bytes per second.</param>
    private readonly record struct DiskRates(float ActiveTime, float ReadSpeed, float WriteSpeed);

    /// <summary>One drive of a RAID volume.</summary>
    /// <param name="Drive">The drive LibreHardwareMonitor reads; null when SysLens reads it through Intel RST.</param>
    /// <param name="Nvme">The NVMe drive as Intel RST reaches it; null for a drive found by its serial number.</param>
    private readonly record struct Member(Drive? Drive, RstNvmeDrive? Nvme);
}
