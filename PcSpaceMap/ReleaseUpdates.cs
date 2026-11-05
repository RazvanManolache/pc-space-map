using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace PcSpaceMap;

public partial class MainWindow
{
    private const string LatestReleaseApi = "https://api.github.com/repos/RazvanManolache/pc-space-map/releases/latest";
    private const string LatestReleasePage = "https://github.com/RazvanManolache/pc-space-map/releases/latest";
    private CancellationTokenSource? _releaseChecks;

    private void StartReleaseChecks()
    {
        if (_releaseChecks is not null) return;
        _releaseChecks = new CancellationTokenSource();
        _ = CheckReleasesAsync(_releaseChecks.Token);
    }

    private void StopReleaseChecks()
    {
        _releaseChecks?.Cancel();
        _releaseChecks?.Dispose();
        _releaseChecks = null;
    }

    private async Task CheckReleasesAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PCSpaceMap");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        try
        {
            do
            {
                try
                {
                    var release = await client.GetFromJsonAsync<GitHubRelease>(LatestReleaseApi, token);
                    if (!token.IsCancellationRequested) UpdateReleaseBadge(release?.Tag);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
                {
                    // Keep any previously discovered update visible while GitHub is unreachable.
                }
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void UpdateReleaseBadge(string? tag)
    {
        var value = tag is not null && tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag[1..] : tag;
        if (Version.TryParse(value, out var latest) && latest.Build >= 0 && latest > Version.Parse(App.AppVersion))
        {
            ReleaseUpdateButton.Content = $"Update available · {latest}";
            ReleaseUpdateButton.Visibility = Visibility.Visible;
        }
        else ReleaseUpdateButton.Visibility = Visibility.Collapsed;
    }

    private void ReleaseUpdate_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(LatestReleasePage) { UseShellExecute = true }); }
        catch (Win32Exception)
        {
            MessageBox.Show(this, "Windows couldn’t open the release page in your browser.", "PC Space Map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record GitHubRelease([property: JsonPropertyName("tag_name")] string? Tag);
}
