using System.Collections.ObjectModel;
using System.Windows.Threading;
using SysLens.Monitoring;

namespace SysLens.ViewModels;

/// <summary>
/// The Hardware Monitor tab: every sensor in a tree, and a graph for each one shown. Monitoring starts with the main
/// window and runs until SysLens closes, so the graphs keep their history while other tabs are open.
/// </summary>
public sealed class MonitorViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly MonitorSettings _settings = AppSettings.Current.HardwareMonitor;

    // Kept when a device goes, so its sensors keep their history and choices if it comes back.
    private readonly Dictionary<string, SensorItem> _sensors = [];

    private HardwareMonitor? _monitor;
    private MonitorLayout? _layout;
    private List<SensorItem> _ordered = [];
    private List<SensorItem> _frameRateItems = [];
    private string _filter = "";
    private string _summary = "";
    private string _status = "";
    private bool _isReading;
    private bool _needsPawnIo;
    private bool _isInstallingPawnIo;
    private bool _disposed;

    /// <summary>Top-level devices, most watched first.</summary>
    public ObservableCollection<HardwareItem> Hardware { get; } = [];

    /// <summary>The sensors with a graph, in the saved order, then default ones never placed in tree order.</summary>
    public ObservableCollection<SensorItem> Shown { get; } = [];

    /// <summary>Words that every visible sensor's name, kind or device contains.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
                ApplyFilter();
        }
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>True from the start until the first readings arrive.</summary>
    public bool IsReading
    {
        get => _isReading;
        private set
        {
            if (Set(ref _isReading, value))
                OnPropertyChanged(nameof(IsEmpty));
        }
    }

    /// <summary>The readings have arrived and no sensor is shown.</summary>
    public bool IsEmpty => !IsReading && Shown.Count == 0;

    public bool NeedsPawnIo
    {
        get => _needsPawnIo;
        private set => Set(ref _needsPawnIo, value);
    }

    public bool IsInstallingPawnIo
    {
        get => _isInstallingPawnIo;
        private set
        {
            if (Set(ref _isInstallingPawnIo, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsInstallingPawnIo;

    public async Task InstallPawnIoAsync()
    {
        if (IsInstallingPawnIo)
            return;

        IsInstallingPawnIo = true;
        Status = "Installing PawnIO with winget…";
        try
        {
            var error = await PawnIoDriver.InstallAsync();
            NeedsPawnIo = !PawnIoDriver.IsInstalled;
            if (error is null)
            {
                Status = "PawnIO is installed; the CPU and motherboard sensors fill in with the next readings.";
                _monitor?.Reopen();
            }
            else
            {
                Status = $"PawnIO was not installed. {error}";
            }
        }
        finally
        {
            IsInstallingPawnIo = false;
        }
    }

    /// <summary>Shows the default sensors only, in their own order, and forgets every choice made here.</summary>
    public void ResetToDefaults()
    {
        _settings.Graphs.Clear();
        _settings.Hidden.Clear();
        SaveSettings();
        foreach (var item in _sensors.Values)
            item.SetShown(item.IsDefault);
        SyncShown();
    }

    /// <summary>Moves a graph next to another one, or to the end when <paramref name="target"/> is null.</summary>
    public void MoveGraph(SensorItem graph, SensorItem? target, bool after)
    {
        if (graph == target || !graph.IsShown)
            return;

        PinOrder();
        _settings.Graphs.Remove(graph.Key);
        var index = target is null ? -1 : _settings.Graphs.IndexOf(target.Key);
        _settings.Graphs.Insert(index < 0 ? _settings.Graphs.Count : index + (after ? 1 : 0), graph.Key);
        SaveSettings();
        SyncShown();
    }

    public void ClearHistory()
    {
        foreach (var item in _sensors.Values)
            item.ClearHistory();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _monitor?.Dispose();
    }

    /// <summary>Starts reading the sensors on a background thread; later calls do nothing.</summary>
    public void Start()
    {
        if (_monitor is not null || _disposed)
            return;

        NeedsPawnIo = !PawnIoDriver.IsInstalled;
        IsReading = true;
        Status = "Reading sensors…";
        _monitor = new HardwareMonitor();
        _monitor.Updated += snapshot => _dispatcher.BeginInvoke(() => Apply(snapshot));
        _monitor.Failed += message => _dispatcher.BeginInvoke(() =>
        {
            IsReading = false;
            Status = $"Could not read the sensors: {message}";
        });
        _monitor.Start();
    }

    private void Apply(MonitorSnapshot snapshot)
    {
        if (_disposed)
            return;

        // A new layout comes whenever devices may have changed, such as on any network address change; the tree,
        // with its scroll position, is rebuilt only when the devices, their names (a disk's drive letters) or the
        // sensors did change.
        if (snapshot.Layout != _layout)
        {
            if (_layout is not null && snapshot.Layout.IsSameTree(_layout))
                _layout = snapshot.Layout;
            else
                BuildTree(snapshot.Layout);
        }

        var sensors = snapshot.Layout.Sensors;
        for (var i = 0; i < sensors.Count; i++)
            _sensors[sensors[i].Key].Add(snapshot.Values[i], snapshot.Time);

        foreach (var item in _frameRateItems)
            item.Source = snapshot.FrameRateApp ?? "No 3D application rendering";

        if (IsReading)
        {
            IsReading = false;
            Status = "Sensors are read every second.";
        }
    }

    private void BuildTree(MonitorLayout layout)
    {
        _layout = layout;
        var ordered = new List<SensorItem>();
        Hardware.Clear();
        foreach (var info in layout.Hardware)
            Hardware.Add(BuildHardware(info, ordered));

        _ordered = ordered;
        _frameRateItems = [.. ordered.Where(i => i.Kind is SensorKind.Framerate or SensorKind.Frametime)];
        SyncShown();
        ApplyFilter();
    }

    private HardwareItem BuildHardware(HardwareInfo info, List<SensorItem> ordered)
    {
        var children = new List<MonitorNode>();
        foreach (var group in info.Sensors.GroupBy(s => s.Kind).OrderBy(g => SensorFormat.SortOrder(g.Key)))
        {
            var items = group.Select(sensor => ItemFor(info, sensor)).ToList();
            ordered.AddRange(items);
            children.Add(Track(new SensorGroupItem($"{info.Key}|{group.Key}", SensorFormat.GroupName(group.Key), items)));
        }

        // The devices it contains follow its own sensors: a RAID volume shows its activity before its drives.
        foreach (var subHardware in info.SubHardware)
            children.Add(BuildHardware(subHardware, ordered));

        return Track(new HardwareItem(info, GlyphOf(info.Kind), children));
    }

    /// <summary>Opens the row if the user left it open, and saves the user opening or closing it.</summary>
    private T Track<T>(T node) where T : MonitorNode
    {
        node.SetExpanded(_settings.Expanded.Contains(node.Key));
        node.ExpandedChanged += OnExpandedChanged;
        return node;
    }

    private void OnExpandedChanged(MonitorNode node)
    {
        if (node.IsExpanded ? _settings.Expanded.Add(node.Key) : _settings.Expanded.Remove(node.Key))
            SaveSettings();
    }

    private SensorItem ItemFor(HardwareInfo hardware, SensorInfo sensor)
    {
        var isDefault = MonitorDefaults.IsDefault(hardware, sensor);
        if (!_sensors.TryGetValue(sensor.Key, out var item))
        {
            item = new SensorItem(sensor.Key, sensor.Name, sensor.Kind);
            item.SetShown(_settings.Graphs.Contains(sensor.Key) || (isDefault && !_settings.Hidden.Contains(sensor.Key)));
            item.ShownChanged += OnShownChanged;
            _sensors[sensor.Key] = item;
        }

        item.Name = sensor.Name;
        item.Title = SensorFormat.Title(sensor.Kind, sensor.Name);
        item.IsDefault = isDefault;
        item.Source = hardware.Name;
        return item;
    }

    /// <summary>A ticked sensor's graph goes last; a cleared one is remembered so a default stays away.</summary>
    private void OnShownChanged(SensorItem item)
    {
        PinOrder();
        _settings.Graphs.Remove(item.Key);
        _settings.Hidden.Remove(item.Key);
        if (item.IsShown)
            _settings.Graphs.Add(item.Key);
        else
            _settings.Hidden.Add(item.Key);
        SaveSettings();
        SyncShown();
    }

    /// <summary>
    /// Saves the order on screen before a change, so default graphs never moved keep their place. Graphs of devices
    /// absent now stay saved, after the others.
    /// </summary>
    private void PinOrder()
    {
        var listed = _ordered.Select(i => i.Key).ToHashSet();
        _settings.Graphs = [.. Shown.Select(i => i.Key), .. _settings.Graphs.Where(key => !listed.Contains(key))];
    }

    private void SaveSettings()
    {
        if (!AppSettings.Current.Save())
            Status = $"Could not write {AppSettings.FilePath}; the graphs stay as they are until SysLens closes.";
    }

    /// <summary>
    /// Brings <see cref="Shown"/> in line with the saved order, then default graphs never placed, in tree order,
    /// moving only the graphs that change.
    /// </summary>
    private void SyncShown()
    {
        var listed = _ordered.ToDictionary(i => i.Key);
        var target = new List<SensorItem>();
        var keep = new HashSet<SensorItem>();
        foreach (var key in _settings.Graphs)
        {
            if (listed.TryGetValue(key, out var item) && item.IsShown && keep.Add(item))
                target.Add(item);
        }
        foreach (var item in _ordered)
        {
            if (item.IsShown && keep.Add(item))
                target.Add(item);
        }

        for (var i = Shown.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Shown[i]))
                Shown.RemoveAt(i);
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Shown.Count && Shown[i] == target[i])
                continue;
            var current = Shown.IndexOf(target[i]);
            if (current >= 0)
                Shown.Move(current, i);
            else
                Shown.Insert(i, target[i]);
        }

        OnPropertyChanged(nameof(IsEmpty));
        Summary = $"{Hardware.Count} devices · {_ordered.Count} sensors · {Shown.Count} shown";
    }

    private void ApplyFilter()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var hardware in Hardware)
            ApplyFilter(hardware, "", words);
    }

    /// <summary>A sensor stays visible when its device, group and name together hold every word.</summary>
    private bool ApplyFilter(HardwareItem hardware, string path, string[] words)
    {
        path = $"{path} {hardware.Name}";
        var anyVisible = false;
        foreach (var child in hardware.Children)
        {
            if (child is HardwareItem subHardware)
            {
                anyVisible |= ApplyFilter(subHardware, path, words);
                continue;
            }

            var group = (SensorGroupItem)child;
            var groupVisible = false;
            foreach (var sensor in group.Sensors)
            {
                var text = $"{path} {group.Name} {sensor.Name}";
                sensor.IsVisible = words.All(word => text.Contains(word, StringComparison.CurrentCultureIgnoreCase));
                groupVisible |= sensor.IsVisible;
            }
            Reveal(group, groupVisible, words);
            anyVisible |= groupVisible;
        }

        Reveal(hardware, anyVisible, words);
        return anyVisible;
    }

    // A search opens the rows that hold a match; once it is cleared, every row is as the user left it.
    private void Reveal(MonitorNode node, bool visible, string[] words)
    {
        node.IsVisible = visible;
        if (words.Length == 0)
            node.SetExpanded(_settings.Expanded.Contains(node.Key));
        else if (visible)
            node.SetExpanded(true);
    }

    /// <summary>Segoe Fluent Icons code points.</summary>
    private static string GlyphOf(HardwareKind kind) => kind switch
    {
        // Processor, graphics card, memory module, hard drive, network, thermometer, lightning bolt, battery, game controller.
        HardwareKind.Cpu => "",
        HardwareKind.GpuNvidia or HardwareKind.GpuAmd or HardwareKind.GpuIntel => "",
        HardwareKind.Memory => "",
        HardwareKind.Storage => "",
        HardwareKind.Network => "",
        HardwareKind.Cooler => "",
        HardwareKind.Psu or HardwareKind.PowerMonitor => "",
        HardwareKind.Battery => "",
        HardwareKind.FrameRate => "",
        // Motherboard, its sensor chip and embedded controller: a chip.
        _ => "",
    };
}
