using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Windows;
using PcSpaceMap.Services;

namespace PcSpaceMap;

public partial class ServiceWindow : Window
{
    private readonly bool _administrator;
    private bool _installed;
    private bool _busy;
    private bool _supportsParallelScans;
    private List<DriveChoice> _drives = [];
    private static string InstalledExe => ServicePreferences.WorkerPath;
    private static string PackageExe => Path.Combine(AppContext.BaseDirectory, "Service", "PcSpaceMap.Service.exe");

    public ServiceWindow()
    {
        InitializeComponent();
        using var identity = WindowsIdentity.GetCurrent();
        _administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        ConcurrentScansBox.ItemsSource = Enumerable.Range(1, ServiceProtocol.MaxScanSlots);
        LoadOptions();
        if (!_administrator) OperationText.Text = "Install, upgrade, settings, and removal require Windows approval.";
    }

    private void LoadOptions()
    {
        var options = ServicePreferences.ReadSettings();
        ConcurrentScansBox.SelectedItem = Math.Clamp(options?.MaxConcurrentScans ?? 1, 1, ServiceProtocol.MaxScanSlots);

        _drives = DriveInfo.GetDrives().Where(IsIndexable)
            .Select(drive => new DriveChoice(drive.Name, drive.Name + "  " + drive.VolumeLabel,
                options?.Volumes.ContainsKey(drive.Name) == true)).ToList();
        if (options is not null)
        {
            foreach (var path in options.Volumes.Keys.Where(path => !_drives.Any(drive =>
                         string.Equals(drive.Root, path, StringComparison.OrdinalIgnoreCase))))
                _drives.Add(new DriveChoice(path, path + "  (unavailable)", true));
            LiveBox.IsChecked = options.Live;
            AutoStartBox.IsChecked = options.AutoStart;
        }
        DriveChoices.ItemsSource = _drives;
        UpdateStatus();
    }

    private static bool IsIndexable(DriveInfo drive)
    {
        try { return drive.IsReady && drive.DriveType == DriveType.Fixed && drive.DriveFormat == "NTFS"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private void UpdateStatus()
    {
        ServiceControllerStatus? status = null;
        try { using var controller = new ServiceController(ServiceProtocol.Name); status = controller.Status; }
        catch (InvalidOperationException) { }
        _installed = status.HasValue;
        _supportsParallelScans = !_installed || ServicePreferences.ReadWorkerVersion() is { Major: >= 2 };
        ConcurrentScansBox.IsEnabled = !_busy && _supportsParallelScans;
        ConcurrencyHelpText.Text = _supportsParallelScans
            ? "Up to one scan per physical disk. Higher limits use more memory and disk bandwidth."
            : "Upgrade the indexing service to enable simultaneous scans.";
        ServiceStatusText.Text = status is null ? "The service is not installed." : $"Status: {status}  ·  Account: LocalSystem";
        ApplyButton.Content = _installed ? "Apply options" : "Install service";
        ApplyButton.IsEnabled = !_busy && File.Exists(_installed ? InstalledExe : PackageExe);
        var canControl = _installed && ServiceControlAccess.CanControl();
        StartButton.IsEnabled = !_busy && canControl && status == ServiceControllerStatus.Stopped;
        StopButton.IsEnabled = RestartButton.IsEnabled = !_busy && canControl && status == ServiceControllerStatus.Running;
        UpgradeButton.IsEnabled = RemoveButton.IsEnabled = !_busy && _installed && File.Exists(PackageExe);
        EnableControlsButton.Visibility = _installed && !canControl && File.Exists(PackageExe)
            ? Visibility.Visible : Visibility.Collapsed;
        EnableControlsButton.IsEnabled = !_busy;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var selected = _drives.Where(drive => drive.Selected).Select(drive => drive.Root).ToArray();
        if (selected.Length == 0)
        {
            OperationText.Text = "Select at least one fixed NTFS drive.";
            return;
        }
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (sid is null) return;
        var args = new List<string> { sid, (LiveBox.IsChecked == true).ToString(), (AutoStartBox.IsChecked == true).ToString() };
        if (_supportsParallelScans) args.Add($"--concurrent-scans={ConcurrentScansBox.SelectedItem ?? 1}");
        args.AddRange(selected);
        await ManageAsync(_installed ? "configure" : "install", args);
    }

    private async void Start_Click(object sender, RoutedEventArgs e) => await ControlAsync(start: true, stop: false);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await ControlAsync(start: false, stop: true);
    private async void Restart_Click(object sender, RoutedEventArgs e) => await ControlAsync(start: true, stop: true);
    private async void EnableControls_Click(object sender, RoutedEventArgs e) => await ManageAsync("authorize-controls", []);
    private async void Upgrade_Click(object sender, RoutedEventArgs e) => await ManageAsync("upgrade", []);
    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Stop and unregister the Windows service? Its saved indexes will be kept.",
                "Remove service", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) == MessageBoxResult.OK)
            await ManageAsync("remove", []);
    }

    private async Task ControlAsync(bool start, bool stop)
    {
        if (_busy) return;
        _busy = true;
        UpdateStatus();
        OperationText.Text = start ? stop ? "Restarting the indexing service…" : "Starting the indexing service…"
            : "Stopping the indexing service…";
        try
        {
            await Task.Run(() =>
            {
                using var controller = new ServiceController(ServiceProtocol.Name);
                if (stop && controller.Status != ServiceControllerStatus.Stopped)
                {
                    if (controller.Status != ServiceControllerStatus.StopPending)
                        controller.Stop(stopDependentServices: false);
                    controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
                    controller.Refresh();
                }
                if (start && controller.Status != ServiceControllerStatus.Running)
                {
                    if (controller.Status == ServiceControllerStatus.Stopped) controller.Start();
                    controller.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
            });
            OperationText.Text = start ? "The indexing service is running." : "The indexing service is stopped.";
        }
        catch (InvalidOperationException ex) when (ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
        { OperationText.Text = "Use Enable start and stop controls to authorize this Windows account."; }
        catch (Exception ex) { OperationText.Text = ex.Message; }
        finally { _busy = false; UpdateStatus(); }
    }

    private async Task ManageAsync(string operation, IReadOnlyList<string> options)
    {
        if (_busy) return;
        _busy = true;
        UpdateStatus();
        OperationText.Text = "Applying service settings…";
        try
        {
            var executable = operation is "install" or "upgrade" or "remove" or "authorize-controls" ? PackageExe : InstalledExe;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = !_administrator,
                Verb = _administrator ? "" : "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = _administrator,
                RedirectStandardError = _administrator
            };
            start.ArgumentList.Add("--manage");
            start.ArgumentList.Add(operation);
            foreach (var option in options) start.ArgumentList.Add(option);
            using var process = Process.Start(start) ?? throw new IOException("Could not start service management.");
            var error = _administrator ? process.StandardError.ReadToEndAsync() : Task.FromResult("");
            await process.WaitForExitAsync();
            OperationText.Text = process.ExitCode switch
            {
                0 when operation == "authorize-controls" => "Start, stop, and restart access enabled for the configured Windows account.",
                0 => "Service settings applied.",
                2 => "Select available fixed NTFS drives and retry.",
                3 => "The service or its installation directory already exists.",
                5 => "Windows administrator access is required.",
                _ => $"Windows could not complete the service operation (code {process.ExitCode}). " + (await error).Trim()
            };
            if (process.ExitCode == 0) LoadOptions();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { OperationText.Text = "Administrator approval was canceled."; }
        catch (Exception ex) { OperationText.Text = ex.Message; }
        finally { _busy = false; UpdateStatus(); }
    }

    private sealed class DriveChoice(string root, string label, bool selected)
    {
        public string Root { get; } = root;
        public string Label { get; } = label;
        public bool Selected { get; set; } = selected;
    }
}
