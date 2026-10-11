using System.IO;
using System.Text.Json;
using SysLens.Monitoring;

namespace SysLens;

/// <summary>
/// SysLens.settings.json next to SysLens.exe, and nowhere else: the Hardware Monitor's graphs and the sizes of the
/// panes the user resized. Read when first used and written whole on every change, on the UI thread only.
/// </summary>
internal sealed class AppSettings
{
    public const string FileName = "SysLens.settings.json";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    public static AppSettings Current { get; } = Load();

    public MonitorSettings HardwareMonitor { get; set; } = new();

    /// <summary>
    /// The rows or columns on either side of each splitter, as the user last left them, by
    /// <see cref="Views.PaneSizes"/> key: their lengths in XAML's form, such as "520" or "2*".
    /// </summary>
    public Dictionary<string, string[]> Panes { get; set; } = [];

    /// <summary>Returns false when the file cannot be written; the settings then last until SysLens closes.</summary>
    public bool Save()
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
            // A section set to null by hand holds nothing.
            settings.HardwareMonitor ??= new();
            settings.Panes ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or unreadable: every sensor follows the defaults and every pane has its own size.
            return new();
        }
    }
}
