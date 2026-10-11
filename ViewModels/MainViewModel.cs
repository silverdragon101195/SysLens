using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using SysLens.Scanning;

namespace SysLens.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _cts;
    private string _status = "Tick the drives to analyse, then press Scan.";
    private bool _isScanning;

    /// <param name="hasBackupPrivilege">Whether SysLens holds the backup privilege, which lets a scan read every folder.</param>
    public MainViewModel(bool hasBackupPrivilege)
    {
        if (!hasBackupPrivilege)
            _status = $"Backup privilege unavailable: some protected folders may be skipped. {_status}";
        RefreshDrives();
    }

    public ObservableCollection<DriveOption> Drives { get; } = [];

    /// <summary>One root row per scanned drive, ordered by drive letter.</summary>
    public ObservableCollection<FolderItem> Results { get; } = [];

    public AssistantViewModel Assistant { get; } = new();

    public UsbViewModel Usb { get; } = new();

    public RgbViewModel Rgb { get; } = new();

    public MonitorViewModel Monitor { get; } = new();

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (Set(ref _isScanning, value))
                OnPropertyChanged(nameof(IsIdle));
        }
    }

    public bool IsIdle => !IsScanning;

    public void RefreshDrives()
    {
        var selected = Drives.Where(d => d.IsSelected).Select(d => d.Name).ToHashSet();
        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                    Drives.Add(new DriveOption(drive) { IsSelected = selected.Contains(drive.Name) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The drive vanished or is locked between enumeration and query; leave it out.
            }
        }
    }

    public void Cancel() => _cts?.Cancel();

    public async Task ScanAsync()
    {
        var selected = Drives.Where(d => d.IsSelected).ToList();
        if (selected.Count == 0)
        {
            Status = "Select at least one drive.";
            return;
        }

        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsScanning = true;
        Results.Clear();

        var scanner = new DiskScanner();
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Status = $"Scanning… {Progress(scanner, clock.Elapsed)}";
        timer.Start();

        var failures = new List<string>();
        try
        {
            await Task.WhenAll(selected.Select(d => ScanDriveAsync(d, scanner, failures, cts.Token)));

            Status = cts.IsCancellationRequested
                ? $"Scan cancelled. {Progress(scanner, clock.Elapsed)}"
                : $"Done. {Progress(scanner, clock.Elapsed)}";
            if (failures.Count > 0)
                Status += $" · Failed: {string.Join("; ", failures)}";
        }
        finally
        {
            timer.Stop();
            _cts = null;
            IsScanning = false;
        }
    }

    private async Task ScanDriveAsync(DriveOption drive, DiskScanner scanner, List<string> failures, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var root = await Task.Run(() => scanner.Scan(drive.Name, ct), ct);
            InsertResult(FolderItem.ForDrive(root, drive, clock.Elapsed));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Reported once by ScanAsync.
        }
        catch (Exception ex)
        {
            failures.Add($"{drive.Name} {ex.Message}");
        }
    }

    private void InsertResult(FolderItem item)
    {
        var index = Results.TakeWhile(r => string.Compare(r.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase) < 0).Count();
        Results.Insert(index, item);
    }

    private static string Progress(DiskScanner scanner, TimeSpan elapsed) =>
        $"{scanner.FolderCount:N0} folders, {scanner.FileCount:N0} files · {elapsed:mm\\:ss}";
}
