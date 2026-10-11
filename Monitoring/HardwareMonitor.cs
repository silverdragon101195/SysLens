using System.Diagnostics;
using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;
using SysLens.Native;
using Drive = LibreHardwareMonitor.Hardware.Storage.StorageDevice;

namespace SysLens.Monitoring;

/// <summary>
/// Reads every sensor LibreHardwareMonitor finds, plus RAM use, each disk's activity and the RTSS frame rate, and
/// raises <see cref="Updated"/> once per <see cref="Interval"/>. LibreHardwareMonitor is opened, updated and closed on
/// one background thread only; it is not thread-safe.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>The highest of a CPU's core clocks, as MSI Afterburner's "CPU clock" reports it.</summary>
    public const string CoreMaxName = "Core Max";

    public const string CoreAverageName = "Core Average";

    public const string FrameRateKey = "/rtss";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    // Most watched first; MSI Afterburner's on-screen display lists CPU, GPUs, RAM, then the frame rate.
    private static readonly HardwareKind[] DisplayOrder =
    [
        HardwareKind.Cpu, HardwareKind.GpuNvidia, HardwareKind.GpuAmd, HardwareKind.GpuIntel, HardwareKind.Memory,
        HardwareKind.FrameRate, HardwareKind.Motherboard, HardwareKind.SuperIO, HardwareKind.EmbeddedController,
        HardwareKind.Storage, HardwareKind.Network, HardwareKind.Cooler, HardwareKind.Psu, HardwareKind.Battery,
        HardwareKind.PowerMonitor,
    ];

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private volatile bool _reopen;
    private volatile bool _layoutChanged;
    private bool _started;

    public HardwareMonitor()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "SysLens hardware monitor" };
    }

    /// <summary>Raised on the monitor thread after every read.</summary>
    public event Action<MonitorSnapshot>? Updated;

    /// <summary>Raised on the monitor thread when LibreHardwareMonitor cannot be opened; <see cref="Reopen"/> tries again.</summary>
    public event Action<string>? Failed;

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        // An adapter going up or down changes which ones are listed.
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        _thread.Start();
    }

    /// <summary>Opens LibreHardwareMonitor afresh, so a driver installed since it was opened is used.</summary>
    public void Reopen()
    {
        _reopen = true;
        _wake.Set();
    }

    public void Dispose()
    {
        if (!_started || _stopping)
            return;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _stopping = true;
        _wake.Set();
        // A background thread: a vendor library stuck in a call cannot keep SysLens running.
        if (_thread.Join(StopTimeout))
            _wake.Dispose();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => _layoutChanged = true;

    private void Run()
    {
        using var rtss = new RtssReader();
        var disks = new DiskReader();
        Computer? computer = null;
        MonitorLayout? layout = null;
        RtssFrame? frame = null;
        MemoryUsage? memory = null;
        var hasFrameRate = false;
        try
        {
            while (!_stopping)
            {
                if (computer is null || _reopen)
                {
                    _reopen = false;
                    Close(computer);
                    computer = null;
                    layout = null;
                    try
                    {
                        computer = Open();
                    }
                    catch (Exception ex)
                    {
                        Failed?.Invoke(ex.Message);
                        _wake.WaitOne();
                        continue;
                    }
                }

                // Timed from here, not from the open, so loads are measured over a full interval.
                var start = Stopwatch.GetTimestamp();
                foreach (var hardware in computer.Hardware)
                    Update(hardware);
                frame = rtss.Read();
                memory = MemoryStatus.Read();
                var lettersChanged = disks.Read();

                if (layout is null || _layoutChanged || lettersChanged || rtss.IsAvailable != hasFrameRate)
                {
                    _layoutChanged = false;
                    hasFrameRate = rtss.IsAvailable;
                    layout = BuildLayout(computer, disks, hasFrameRate, () => frame, () => memory);
                }
                Updated?.Invoke(new MonitorSnapshot(layout, layout.Read(), frame?.App, DateTime.Now));

                var wait = Interval - Stopwatch.GetElapsedTime(start);
                if (wait > TimeSpan.Zero)
                    _wake.WaitOne(wait);
            }
        }
        finally
        {
            Close(computer);
        }
    }

    // LibreHardwareMonitor reaches into vendor libraries and drivers that throw whatever they throw. An exception
    // escaping this thread would end SysLens, so a failure is reported (Open) or skipped until the next read (Update).
    private Computer Open()
    {
        // The memory group stays off: it reads memory module temperatures over SMBus and waits 2 seconds on every
        // transfer while another app (an RGB memory controller, say) holds the SMBus mutex, so opening it can take
        // minutes. SysLens reads RAM use itself; see BuildLayout.
        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsPsuEnabled = true,
            IsBatteryEnabled = true,
            IsPowerMonitorEnabled = true,
        };
        computer.HardwareAdded += _ => _layoutChanged = true;
        computer.HardwareRemoved += _ => _layoutChanged = true;
        try
        {
            computer.Open();
        }
        catch
        {
            Close(computer);
            throw;
        }
        return computer;
    }

    private static void Update(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception)
        {
            // The device keeps its last values; the next read tries again.
        }
        foreach (var subHardware in hardware.SubHardware)
            Update(subHardware);
    }

    private static void Close(Computer? computer)
    {
        try
        {
            computer?.Close();
        }
        catch (Exception)
        {
            // Nothing more to release.
        }
    }

    private static MonitorLayout BuildLayout(Computer computer, DiskReader disks, bool hasFrameRate, Func<RtssFrame?> frame,
        Func<MemoryUsage?> memory)
    {
        var layout = new LayoutBuilder();
        var upInterfaces = UpNetworkInterfaces();

        HardwareInfo? Describe(IHardware hardware)
        {
            if (!LayoutBuilder.TryMap(hardware.HardwareType, out HardwareKind kind)
                || (kind == HardwareKind.Network && !upInterfaces.Contains(InterfaceId(hardware))))
                return null;

            var key = hardware.Identifier.ToString();
            var own = new List<SensorInfo>();
            if (kind == HardwareKind.Cpu)
            {
                var cores = hardware.Sensors.Where(s => s.SensorType == SensorType.Clock && s.Name != "Bus Speed").ToList();
                if (cores.Count > 1)
                {
                    own.Add(layout.Add($"{key}/clock/max", CoreMaxName, SensorKind.Clock, () => cores.Max(s => s.Value)));
                    own.Add(layout.Add($"{key}/clock/average", CoreAverageName, SensorKind.Clock, () => cores.Average(s => s.Value)));
                }
            }
            own.AddRange(layout.Add(hardware.Sensors));

            // A device with nothing to read, such as a motherboard while PawnIO is missing, is left out.
            List<HardwareInfo> subHardware = [.. hardware.SubHardware.Select(Describe).OfType<HardwareInfo>()];
            return own.Count > 0 || subHardware.Count > 0 ? new HardwareInfo(key, hardware.Name, kind, own, subHardware) : null;
        }

        var hardware = computer.Hardware.Where(h => h is not Drive).Select(Describe).OfType<HardwareInfo>().ToList();
        hardware.AddRange(disks.Describe(layout, [.. computer.Hardware.OfType<Drive>()]));

        // RAM use, under the keys LibreHardwareMonitor gives these sensors.
        HardwareInfo MemoryHardware(string key, string name, int loadIndex, int dataIndex, Func<(ulong Total, ulong Available)?> read) =>
            new(key, name, HardwareKind.Memory,
            [
                layout.Add($"{key}/load/{loadIndex}", "Memory", SensorKind.Load,
                    () => read() is { Total: > 0 } r ? 100f * (r.Total - r.Available) / r.Total : null),
                layout.Add($"{key}/data/{dataIndex}", "Memory Used", SensorKind.Data, () => read() is { } r ? r.Total - r.Available : null),
                layout.Add($"{key}/data/{dataIndex + 1}", "Memory Available", SensorKind.Data, () => read() is { } r ? r.Available : null),
            ], []);

        hardware.Add(MemoryHardware("/ram", "Total Memory", 0, 0,
            () => memory() is { } m ? (m.PhysicalTotal, m.PhysicalAvailable) : null));
        hardware.Add(MemoryHardware("/vram", "Virtual Memory", 1, 2,
            () => memory() is { } m ? (m.CommitLimit, m.CommitAvailable) : null));

        if (hasFrameRate)
        {
            hardware.Add(new HardwareInfo(FrameRateKey, "RivaTuner Statistics Server", HardwareKind.FrameRate,
            [
                layout.Add($"{FrameRateKey}/framerate", "Framerate", SensorKind.Framerate, () => frame()?.Framerate),
                layout.Add($"{FrameRateKey}/frametime", "Frametime", SensorKind.Frametime, () => frame()?.FrametimeMs),
            ], []));
        }

        return layout.Build([.. hardware.OrderBy(h => Array.IndexOf(DisplayOrder, h.Kind))]);
    }

    /// <summary>
    /// Adapters that carry traffic: up, with an IP address. Windows also reports as up every filter driver's binding
    /// to an adapter ("Ethernet-QoS Packet Scheduler-0000"), WAN miniports and Hyper-V switch ports, none of which
    /// has an address.
    /// </summary>
    private static HashSet<string> UpNetworkInterfaces()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().UnicastAddresses.Count > 0)
                .Select(n => n.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>LibreHardwareMonitor identifies an adapter as "/nic/" plus its escaped interface id.</summary>
    private static string InterfaceId(IHardware hardware) =>
        Uri.UnescapeDataString(hardware.Identifier.ToString().Split('/', 3)[^1]);
}
