using SysLens.Monitoring;

namespace SysLens.ViewModels;

/// <summary>Where a graph dragged over another one would land.</summary>
public enum DropSide
{
    None,
    Before,
    After,
}

/// <summary>
/// One sensor: a row in the sensor tree and, while <see cref="IsShown"/>, a graph on the monitor. Keeps the last
/// <see cref="HistoryLength"/> readings, and the lowest, average and highest since the history was last cleared.
/// </summary>
public sealed class SensorItem(string key, string name, SensorKind kind) : MonitorNode
{
    /// <summary>Five minutes at one reading per second.</summary>
    public const int HistoryLength = 300;

    private readonly float?[] _values = new float?[HistoryLength];
    private readonly DateTime[] _times = new DateTime[HistoryLength];
    private int _count;
    private int _next;
    private double _sum;
    private int _summed;
    private string _name = name;
    private string _title = name;
    private string _source = "";
    private string _valueText = SensorFormat.Format(kind, null);
    private string _statsText = "";
    private bool _isShown;
    private bool _isDragged;
    private DropSide _dropSide;

    /// <summary>Raised after every reading and when the history is cleared.</summary>
    public event Action? Sampled;

    /// <summary>Raised when the user shows or hides the sensor, but not by <see cref="SetShown"/>.</summary>
    public event Action<SensorItem>? ShownChanged;

    public override string Key { get; } = key;

    public SensorKind Kind { get; } = kind;

    /// <summary>LibreHardwareMonitor's name, shown in the sensor tree under the kind's group.</summary>
    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>The graph's title: the sensor's name and its group, as the sensor tree shows them.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>The device, or for frame rate the application, shown under the graph's title.</summary>
    public string Source
    {
        get => _source;
        set => Set(ref _source, value);
    }

    /// <summary>Whether the sensor is shown when the user has not chosen; see <see cref="MonitorDefaults"/>.</summary>
    public bool IsDefault { get; set; }

    public bool IsShown
    {
        get => _isShown;
        set
        {
            if (Set(ref _isShown, value))
                ShownChanged?.Invoke(this);
        }
    }

    /// <summary>True while the user drags this graph to another place.</summary>
    public bool IsDragged
    {
        get => _isDragged;
        set => Set(ref _isDragged, value);
    }

    /// <summary>The edge of this graph a dragged one would be dropped at, marked with a bar.</summary>
    public DropSide DropSide
    {
        get => _dropSide;
        set => Set(ref _dropSide, value);
    }

    public string ValueText
    {
        get => _valueText;
        private set => Set(ref _valueText, value);
    }

    /// <summary>The lowest, average and highest readings since the history was cleared.</summary>
    public string StatsText
    {
        get => _statsText;
        private set => Set(ref _statsText, value);
    }

    public float? Min { get; private set; }

    public float? Max { get; private set; }

    /// <summary>Readings held, up to <see cref="HistoryLength"/>.</summary>
    public int Count => _count;

    /// <summary>A held reading; 0 is the oldest, <see cref="Count"/> − 1 the latest.</summary>
    public float? ValueAt(int index) => _values[Slot(index)];

    public DateTime TimeAt(int index) => _times[Slot(index)];

    public void SetShown(bool shown)
    {
        if (_isShown == shown)
            return;
        _isShown = shown;
        OnPropertyChanged(nameof(IsShown));
    }

    public void Add(float? value, DateTime time)
    {
        if (value is { } v && !float.IsFinite(v))
            value = null;

        _values[_next] = value;
        _times[_next] = time;
        _next = (_next + 1) % HistoryLength;
        _count = Math.Min(_count + 1, HistoryLength);

        if (value is { } reading)
        {
            Min = Math.Min(Min ?? reading, reading);
            Max = Math.Max(Max ?? reading, reading);
            _sum += reading;
            _summed++;
        }

        ValueText = SensorFormat.Format(Kind, value);
        UpdateStats();
        Sampled?.Invoke();
    }

    public void ClearHistory()
    {
        Array.Clear(_values);
        _count = 0;
        _next = 0;
        _sum = 0;
        _summed = 0;
        Min = null;
        Max = null;
        UpdateStats();
        Sampled?.Invoke();
    }

    private int Slot(int index) => (_next - _count + index + HistoryLength) % HistoryLength;

    private void UpdateStats() =>
        StatsText = _summed == 0
            ? ""
            : $"Min {SensorFormat.Format(Kind, Min)} · Avg {SensorFormat.Format(Kind, _sum / _summed)} · Max {SensorFormat.Format(Kind, Max)}";
}
