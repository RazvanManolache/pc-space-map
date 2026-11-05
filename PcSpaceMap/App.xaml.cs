using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PcSpaceMap.Services;

namespace PcSpaceMap;

public partial class App : Application
{
    internal static string AppVersion => typeof(App).Assembly.GetName().Version!.ToString(3);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A small non-UI mode used by automated verification and future scheduled inventories.
        if (e.Args.Length >= 3 && e.Args[0].Equals("--scan-report", StringComparison.OrdinalIgnoreCase))
        {
            var progress = new ReportProgress(e.Args[2] + ".progress.json");
            try
            {
                var scanner = new DiskScanner { PreferNtfs = !e.Args.Contains("--walk"), AutoTuneReaders = !e.Args.Contains("--fixed-readers"),
                    Parallelism = Math.Clamp(Environment.ProcessorCount, 1, 16) };
                var result = await ServiceInventoryClient.ScanAsync(e.Args[1], "scan", progress, CancellationToken.None)
                    ?? await scanner.ScanAsync(e.Args[1], progress, CancellationToken.None);
                var report = new
                {
                    result.Root.Path,
                    result.Root.Size,
                    result.FileCount,
                    result.DirectoryCount,
                    result.SkippedReparsePoints,
                    result.SnapshotId,
                    result.ScanMethod,
                    result.CompletedUtc,
                    result.Timings,
                    result.Work,
                    DurationSeconds = result.Timings.Sum(x => x.Seconds),
                    PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                    result.Root.AllocatedSize,
                    result.Root.UniqueAllocatedSize,
                    TopLevel = result.Root.Children.Select(x => new { x.Path, x.Size }),
                    result.Changes,
                    IssueCount = result.Issues.Count,
                    CleanupPotential = result.Suggestions.Sum(x => x.Size),
                    LargestFiles = result.LargestFiles.Take(20).Select(x => new { x.Path, x.Size })
                };
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                progress.Complete();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(new { Error = ex.ToString() }));
                progress.Finish("Failed", ex.Message);
                Shutdown(1);
            }
            return;
        }

        var window = new MainWindow();
        var trayAvailable = window.InitializeTray();
        SessionEnding += (_, _) => window.AllowSystemExit();
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.AddHook(window.HandleInstallerShutdown);
        };
        if (trayAvailable && e.Args.Any(x => x.Equals(DesktopStartup.BackgroundArgument, StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(window.HideToTray, DispatcherPriority.Loaded);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.WindowState = WindowState.Normal;
            window.ShowInTaskbar = true;
            window.ShowActivated = true;
            window.Loaded += (_, _) =>
            {
                window.Dispatcher.BeginInvoke(() =>
                {
                    if (window.WindowState == WindowState.Minimized)
                        window.WindowState = WindowState.Normal;

                    window.Topmost = true;
                    window.Activate();
                    window.Focus();
                    window.Topmost = false;
                }, DispatcherPriority.ApplicationIdle);
            };
        }
        window.Show();
    }

    private sealed class ReportProgress(string path) : IProgress<PcSpaceMap.Models.ScanProgress>
    {
        private long _lastWrite;
        private string _lastPhase = "";
        public void Report(PcSpaceMap.Models.ScanProgress value)
        {
            var now = Environment.TickCount64;
            if (now - _lastWrite < 1000 && value.Phase == _lastPhase) return;
            _lastWrite = now;
            _lastPhase = value.Phase;
            Write(new { State = "Running", ProcessId = Environment.ProcessId,
                UpdatedUtc = DateTime.UtcNow, value.Phase, value.CurrentPath, value.FileCount, value.DirectoryCount, value.BytesFound });
        }

        public void Complete() => Finish("Completed");

        public void Finish(string state, string? error = null) =>
            Write(new { State = state, ProcessId = Environment.ProcessId, UpdatedUtc = DateTime.UtcNow, Error = error });

        private void Write(object update)
        {
            try
            {
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(update));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A progress reader can briefly prevent replacement on Windows.
                // The scan and final report must succeed independently of progress.
            }
        }
    }
}
