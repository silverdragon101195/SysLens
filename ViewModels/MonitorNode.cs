using SysLens.Monitoring;

namespace SysLens.ViewModels;

/// <summary>A row of the Hardware Monitor's sensor tree.</summary>
public abstract class MonitorNode : ObservableObject
{
    private bool _isExpanded;
    private bool _isVisible = true;

    /// <summary>Raised when the user opens or closes the row, but not by <see cref="SetExpanded"/>.</summary>
    public event Action<MonitorNode>? ExpandedChanged;

    /// <summary>Stays the same from run to run for the same row, so the user's choices can be saved against it.</summary>
    public abstract string Key { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                ExpandedChanged?.Invoke(this);
        }
    }

    /// <summary>False while the search hides the row.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>Opens or closes the row for SysLens, such as for a search, rather than for the user.</summary>
    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
            return;
        _isExpanded = expanded;
        OnPropertyChanged(nameof(IsExpanded));
    }
}

/// <summary>A device: its sensors grouped by kind, then the devices it contains.</summary>
public sealed class HardwareItem(HardwareInfo info, string glyph, IReadOnlyList<MonitorNode> children) : MonitorNode
{
    public override string Key => info.Key;

    public string Name => info.Name;

    /// <summary>A Segoe Fluent Icons code point.</summary>
    public string Glyph { get; } = glyph;

    public IReadOnlyList<MonitorNode> Children { get; } = children;
}

/// <summary>The sensors of one kind on one device.</summary>
public sealed class SensorGroupItem(string key, string name, IReadOnlyList<SensorItem> sensors) : MonitorNode
{
    /// <summary>The device's key and the kind.</summary>
    public override string Key { get; } = key;

    public string Name { get; } = name;

    public IReadOnlyList<SensorItem> Sensors { get; } = sensors;
}
