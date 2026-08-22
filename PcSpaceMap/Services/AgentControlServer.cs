using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PcSpaceMap.Services;

/// <summary>
/// A per-run, loopback-only control surface. It lets an assistant inspect and navigate the app
/// without foreground desktop automation. All inventory endpoints require a random bearer token.
/// </summary>
internal sealed class AgentControlServer : IAsyncDisposable
{
    private readonly MainWindow _window;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private WebApplication? _app;

    public AgentControlServer(MainWindow window)
    {
        _window = window;
        SessionFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PCSpaceMap",
            "agent-session.json");
    }

    public string BaseUrl { get; private set; } = "";
    public string SessionFilePath { get; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var options = new WebApplicationOptions
        {
            ApplicationName = typeof(AgentControlServer).Assembly.FullName,
            EnvironmentName = Environments.Production
        };
        var builder = WebApplication.CreateBuilder(options);
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(IPAddress.Loopback, 0);
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api") && !HasValidToken(context))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Missing or invalid session token.",
                    hint = "Read the token from the PC Space Map agent-session.json file."
                });
                return;
            }
            await next();
        });

        app.MapGet("/", () => Results.Text(HelpPage, "text/html; charset=utf-8"));
        app.MapGet("/health", () => Results.Json(new
        {
            app = "PC Space Map",
            version = "0.3.0",
            processId = Environment.ProcessId,
            loopbackOnly = true,
            authenticatedInventoryApi = true
        }));

        app.MapGet("/api/help", () => Results.Json(new
        {
            purpose = "Inspect and navigate PC Space Map without controlling the Windows desktop.",
            authentication = "Authorization: Bearer <token> (or ?token=<token> for local image viewing)",
            endpoints = new[]
            {
                "GET /api/status",
                "GET /api/report",
                "GET /api/tree?path=<optional>&depth=2&limit=100",
                "GET /api/largest?under=<optional>&limit=100",
                "GET /api/suggestions",
                "GET /api/issues",
                "GET /api/screenshot",
                "POST /api/scan { path }",
                "POST /api/navigate { path?, tab?, selectOnly? }",
                "POST /api/shutdown"
            }
        }));

        app.MapGet("/api/status", async () => Results.Json(await OnUiAsync(_window.BuildAgentStatus)));
        app.MapGet("/api/report", async () => Results.Json(await OnUiAsync(_window.BuildAgentReport)));
        app.MapGet("/api/tree", async (HttpRequest request) =>
        {
            var path = request.Query["path"].FirstOrDefault();
            var depth = ParseBoundedInt(request.Query["depth"].FirstOrDefault(), 2, 0, 5);
            var limit = ParseBoundedInt(request.Query["limit"].FirstOrDefault(), 100, 1, 500);
            return Results.Json(await OnUiAsync(() => _window.BuildAgentTree(path, depth, limit)));
        });
        app.MapGet("/api/largest", async (HttpRequest request) =>
        {
            var under = request.Query["under"].FirstOrDefault();
            var limit = ParseBoundedInt(request.Query["limit"].FirstOrDefault(), 100, 1, 1000);
            return Results.Json(await OnUiAsync(() => _window.BuildAgentLargestFiles(under, limit)));
        });
        app.MapGet("/api/suggestions", async () => Results.Json(await OnUiAsync(_window.BuildAgentSuggestions)));
        app.MapGet("/api/issues", async () => Results.Json(await OnUiAsync(_window.BuildAgentIssues)));
        app.MapGet("/api/screenshot", async () =>
        {
            var png = await OnUiAsync(_window.CaptureAgentScreenshot);
            return Results.File(png, "image/png", "pc-space-map.png", enableRangeProcessing: false);
        });

        app.MapPost("/api/scan", async (AgentScanRequest request) =>
        {
            var response = await OnUiAsync(() => _window.BeginAgentScan(request.Path));
            return Results.Json(response, statusCode: response.Accepted ? StatusCodes.Status202Accepted : StatusCodes.Status409Conflict);
        });
        app.MapPost("/api/navigate", async (AgentNavigateRequest request) =>
        {
            var response = await OnUiAsync(() => _window.ApplyAgentNavigation(request));
            return Results.Json(response, statusCode: response.Success ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        });
        app.MapPost("/api/shutdown", () =>
        {
            _window.Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(200);
                _window.Close();
            });
            return Results.Accepted(value: new { accepted = true, message = "PC Space Map is closing." });
        });

        _app = app;
        await app.StartAsync(cancellationToken);

        var server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses.SingleOrDefault()
            ?? throw new InvalidOperationException("The local control address was not assigned.");
        BaseUrl = address.TrimEnd('/');
        await WriteSessionFileAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _app.StopAsync(timeout.Token).ConfigureAwait(false);
                await _app.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // Process exit is already closing the loopback listener.
        }

        RemoveSessionFile();
    }

    public void RemoveSessionFile()
    {
        try
        {
            if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
        }
        catch
        {
            // A stale file includes the PID, so clients can still detect that the session ended.
        }
    }

    private bool HasValidToken(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) &&
            CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(authorization[7..].Trim()),
                System.Text.Encoding.UTF8.GetBytes(_token)))
            return true;

        // Query authentication is intentionally limited to GET so a browser can display a screenshot.
        return HttpMethods.IsGet(context.Request.Method) &&
               string.Equals(context.Request.Query["token"].FirstOrDefault(), _token, StringComparison.Ordinal);
    }

    private Task<T> OnUiAsync<T>(Func<T> operation) =>
        _window.Dispatcher.InvokeAsync(operation).Task;

    private async Task WriteSessionFileAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
        var session = new
        {
            app = "PC Space Map",
            version = "0.3.0",
            baseUrl = BaseUrl,
            token = _token,
            processId = Environment.ProcessId,
            startedUtc = DateTime.UtcNow,
            scope = "127.0.0.1 only",
            note = "Use this short-lived token only while this process is running."
        };
        await File.WriteAllTextAsync(SessionFilePath,
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static int ParseBoundedInt(string? value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;

    private const string HelpPage = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width">
        <title>PC Space Map local control</title>
        <style>body{font:16px system-ui;background:#09111f;color:#e8f1fb;max-width:780px;margin:60px auto;padding:0 24px}code{background:#142238;padding:2px 6px;border-radius:4px;color:#67e8f9}.card{background:#111c2e;border:1px solid #263b57;border-radius:12px;padding:24px}h1{margin-top:0}li{margin:8px 0}.safe{color:#86efac}</style></head>
        <body><div class="card"><h1>PC Space Map</h1><p class="safe">Local control channel is running.</p>
        <p>This listener accepts connections from <strong>this PC only</strong>. Inventory data and actions require the random token stored in the current session file.</p>
        <ul><li>Structured inventory navigation</li><li>App-rendered screenshots without mouse control</li><li>Semantic scan, zoom, tab, and shutdown actions</li></ul>
        <p>Machine-readable help: <code>/api/help</code></p></div></body></html>
        """;
}

internal sealed class AgentScanRequest
{
    public string Path { get; init; } = "";
}

internal sealed class AgentNavigateRequest
{
    public string? Path { get; init; }
    public string? Tab { get; init; }
    public bool SelectOnly { get; init; }
}

internal sealed record AgentScanResponse(bool Accepted, string Message);
internal sealed record AgentNavigationResponse(bool Success, string Message);
