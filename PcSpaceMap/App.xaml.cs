using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using PcSpaceMap.Services;

namespace PcSpaceMap;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A small non-UI mode used by automated verification and future scheduled inventories.
        if (e.Args.Length >= 3 && e.Args[0].Equals("--scan-report", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var result = await new DiskScanner().ScanAsync(e.Args[1], null, CancellationToken.None);
                var report = new
                {
                    result.Root.Path,
                    result.Root.Size,
                    result.FileCount,
                    result.DirectoryCount,
                    result.SkippedReparsePoints,
                    IssueCount = result.Issues.Count,
                    CleanupPotential = result.Suggestions.Sum(x => x.Size),
                    LargestFiles = result.LargestFiles.Take(20).Select(x => new { x.Path, x.Size })
                };
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(e.Args[2], JsonSerializer.Serialize(new { Error = ex.ToString() }));
                Shutdown(1);
            }
            return;
        }

        var window = new MainWindow();
        if (e.Args.Any(x => x.Equals("--background", StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowActivated = false;
            window.ShowInTaskbar = false;
            window.WindowState = WindowState.Minimized;
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
}
