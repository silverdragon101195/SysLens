using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using SysLens.Ai;
using SysLens.Native;
using SysLens.ViewModels;

namespace SysLens;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        // App.OnStartup only opens this window once the process is elevated, so it can hold the backup privilege.
        _viewModel = new MainViewModel(Privileges.TryEnable(Privileges.Backup));
        DataContext = _viewModel;
        // The RGB tab is hidden, so RGB is not started: no Aura host process, and Dynamic Lighting devices are not
        // taken over while SysLens is in front.
        Loaded += async (_, _) =>
        {
            _viewModel.Monitor.Start();
            await _viewModel.Usb.RefreshAsync();
        };
        Closed += (_, _) =>
        {
            // Stops the lighting render thread, and hands any ASUS Aura devices back to Armoury Crate.
            _viewModel.Rgb.Dispose();
            _viewModel.Monitor.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
        DeviceChanges.Watch(this, _viewModel.Usb.ScheduleRefresh);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _viewModel.RefreshDrives();

    private async void Scan_Click(object sender, RoutedEventArgs e) => await _viewModel.ScanAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => _viewModel.Cancel();

    private void OpenInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item)
            return;

        // A drive root ends with a backslash, which would escape a closing quote.
        // A file opens its containing folder with the file selected.
        var argument = item.IsFile ? $"/select,\"{item.FullPath}\""
            : item.FullPath.EndsWith('\\') ? item.FullPath
            : $"\"{item.FullPath}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", argument) { UseShellExecute = true });
    }

    private async void AskAi_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            await _viewModel.Assistant.StartAsync(Prompts.ForItem(item));
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item)
            CopyText(item.FullPath);
    }

    private async void UsbRefresh_Click(object sender, RoutedEventArgs e) => await _viewModel.Usb.RefreshAsync();

    private async void UsbAskAi_Click(object sender, RoutedEventArgs e)
    {
        if (UsbItemOf(sender) is { } item)
            await _viewModel.Assistant.StartAsync(UsbPrompts.ForDevice(item));
    }

    private void CopyInstanceId_Click(object sender, RoutedEventArgs e)
    {
        if (UsbItemOf(sender) is { } item)
            CopyText(item.InstanceId);
    }

    private async void RgbRefresh_Click(object sender, RoutedEventArgs e) => await _viewModel.Rgb.RefreshAsync();

    private async void RgbTakeControl_Click(object sender, RoutedEventArgs e) => await _viewModel.Rgb.TakeControlAsync();

    private async void RgbRelease_Click(object sender, RoutedEventArgs e) => await _viewModel.Rgb.ReleaseAsync();

    private void RgbSyncAll_Click(object sender, RoutedEventArgs e) => _viewModel.Rgb.SyncAll();

    private void RgbUseForAll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RgbTargetItem item })
            _viewModel.Rgb.UseForAll(item);
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            // Another process holds the clipboard open; the user can simply retry.
        }
    }

    private static FolderItem? ItemOf(object sender) =>
        sender is FrameworkElement { DataContext: FolderItem { HasPath: true } item } ? item : null;

    private static UsbDeviceItem? UsbItemOf(object sender) =>
        sender is FrameworkElement { DataContext: UsbDeviceItem item } ? item : null;
}
