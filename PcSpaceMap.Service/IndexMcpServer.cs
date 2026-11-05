using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcSpaceMap.Models;
using PcSpaceMap.Services;

namespace PcSpaceMap.Service;

internal static class IndexMcpServer
{
    private const string ProtocolVersion = "2025-11-25";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly object PathInput = new { type = "string", description = "Absolute Windows path on a drive managed by the service." };
    private static readonly object LimitInput = new { type = "integer", minimum = 1, maximum = 100, @default = 25 };
    private static readonly object OffsetInput = new { type = "integer", minimum = 0, maximum = 1000000, @default = 0 };
    private static readonly object[] Tools =
    [
        Tool("index_status", "Check which configured drives are indexed, updating, or unavailable.", new { }, []),
        Tool("inventory_summary", "Read the indexed size and counts for a drive, folder, or file.", new { path = PathInput }, ["path"]),
        Tool("list_folder", "List the direct indexed children of a folder, largest first.", new { path = PathInput, limit = LimitInput, offset = OffsetInput }, ["path"]),
        Tool("largest_files", "Find the largest indexed files in a folder and its descendants.", new { path = PathInput, limit = LimitInput }, ["path"]),
        Tool("search_names", "Find indexed files and folders by partial name or wildcard within a folder.",
            new { path = PathInput, query = new { type = "string", minLength = 1, maxLength = 260 }, limit = LimitInput, offset = OffsetInput }, ["path", "query"])
    ];

    private static object Tool(string name, string description, object properties, string[] required) => new
    {
        name, description, inputSchema = new { type = "object", properties, required, additionalProperties = false },
        annotations = new { readOnlyHint = true, destructiveHint = false, idempotentHint = true, openWorldHint = false }
    };

    public static async Task RunAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        while (await Console.In.ReadLineAsync() is { } line)
        {
            object? response;
            try
            {
                using var request = JsonDocument.Parse(line);
                response = await RespondAsync(request.RootElement);
                if (response is not null) await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, Json));
            }
            catch (JsonException)
            {
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(Error(null, -32700, "Invalid JSON."), Json));
            }
        }
    }

    private static async Task<object?> RespondAsync(JsonElement request)
    {
        var id = Get(request, "id");
        var method = Get(request, "method");
        if (Get(request, "jsonrpc") is not { ValueKind: JsonValueKind.String } version || version.GetString() != "2.0"
            || method.ValueKind != JsonValueKind.String
            || id.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Number or JsonValueKind.String))
            return Error(null, -32600, "Invalid request.");
        if (id.ValueKind == JsonValueKind.Undefined) return null;

        var parameters = Get(request, "params");
        var requested = Get(parameters, "protocolVersion");
        object? result = method.GetString() switch
        {
            "initialize" => new
            {
                protocolVersion = requested.ValueKind == JsonValueKind.String
                    && requested.GetString() is "2024-11-05" or "2025-03-26" or "2025-06-18" or ProtocolVersion
                    ? requested.GetString() : ProtocolVersion,
                capabilities = new { tools = new { } },
                serverInfo = new { name = "pc-space-map", version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) }
            },
            "ping" => new { },
            "tools/list" => new { tools = Tools },
            "tools/call" => await CallToolAsync(parameters),
            _ => null
        };
        return result is null ? Error(id, -32601, "Method not found.") : new { jsonrpc = "2.0", id, result };
    }

    private static object Error(JsonElement? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };

    private static async Task<object> CallToolAsync(JsonElement parameters)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var name = RequiredText(parameters, "name", 64);
            var args = Get(parameters, "arguments");
            var value = name switch
            {
                "index_status" => await ReadStatusAsync(timeout.Token),
                "inventory_summary" or "list_folder" or "largest_files" or "search_names"
                    => await ReadInventoryAsync(name, args, timeout.Token),
                _ => throw new ArgumentException("Unknown inventory tool.")
            };
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Json) } }, isError = false };
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException
            or SqliteException or OperationCanceledException or JsonException)
        {
            var message = ex switch
            {
                OperationCanceledException => "The index query timed out. Try a smaller folder.",
                SqliteException => "Could not read the published index. Retry after the service finishes updating.",
                UnauthorizedAccessException => "This Windows account cannot read the service index.",
                _ => ex.Message
            };
            return new { content = new[] { new { type = "text", text = message } }, isError = true };
        }
    }

    private static async Task<object> ReadStatusAsync(CancellationToken token)
    {
        var settingsFile = Path.Combine(ServiceProtocol.InstallPath, "service.json");
        if (!File.Exists(settingsFile)) throw new IOException("Install and configure the PC Space Map Windows service first.");
        var settings = JsonSerializer.Deserialize<ServiceProtocol.Settings>(await File.ReadAllTextAsync(settingsFile, token));
        if (settings?.Volumes is null) throw new IOException("The service drive settings are unavailable.");
        var drives = new List<object>();
        foreach (var drive in settings.Volumes.Keys.Where(ServiceProtocol.IsDriveRoot).Order())
        {
            var status = await GetStatusAsync(drive, token);
            drives.Add(new { drive, status.Managed, status.Snapshot, status.Running, status.RecoveryPending, phase = status.Progress?.Phase, status.Error });
        }
        return new { liveMonitoring = settings.Live, drives };
    }

    private static async Task<object> ReadInventoryAsync(string tool, JsonElement args, CancellationToken token)
    {
        var path = RequiredText(args, "path", 32767);
        if (!ServiceProtocol.TryResolveDriveScope(path, out var drive, out var scope))
            throw new ArgumentException("Use an absolute path on a local drive managed by the service.");
        var status = await GetStatusAsync(drive, token);
        if (!status.Managed || status.Volume is null || status.Snapshot <= 0)
            throw new IOException("The service has no completed index for this drive.");
        if (!ServiceProtocol.VolumeMatches(drive, status.Volume))
            throw new IOException("The configured volume is unavailable or has changed.");

        var database = ServiceProtocol.DatabasePath(ServiceProtocol.InstallPath, drive, status.Volume);
        var store = new InventoryStore(drive, database, legacy: false, readOnly: true);
        var node = store.Find(status.Snapshot, scope) ?? throw new IOException("The path was not found in the published index.");
        if (tool == "inventory_summary") return new { status.Snapshot, entry = Describe(node) };
        if (!node.IsDirectory) throw new ArgumentException("Use a folder or drive for this tool.");

        var limit = Integer(args, "limit", 25, 1, 100);
        var offset = tool == "largest_files" ? 0 : Integer(args, "offset", 0, 0, 1000000);
        IReadOnlyList<ScanNode> rows = tool switch
        {
            "list_folder" => store.Children(status.Snapshot, node, offset, limit + 1, token: token),
            "largest_files" => store.Largest(status.Snapshot, node.Path, limit, token: token),
            _ => store.SearchNames(status.Snapshot, node.Path, new FileFilter(RequiredText(args, "query", 260), 0, null), offset, limit + 1, token)
        };
        return new { status.Snapshot, folder = node.Path, entries = rows.Take(limit).Select(Describe), nextOffset = rows.Count > limit ? (int?)(offset + limit) : null };
    }

    private static async Task<ServiceProtocol.Response> GetStatusAsync(string drive, CancellationToken token) =>
        await ServiceProtocol.CallAsync(new("status", drive), token)
        ?? throw new IOException("The Windows service is stopped or unavailable to this Windows account.");

    private static object Describe(ScanNode node) => new
    {
        node.Name, node.Path, kind = node.IsDirectory ? "folder" : "file", bytes = node.Size,
        node.AllocatedSize, node.UniqueAllocatedSize, node.FileCount, node.DirectoryCount,
        modifiedUtc = node.LastWriteUtc == DateTime.MinValue ? (DateTime?)null : node.LastWriteUtc
    };

    private static JsonElement Get(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : default;

    private static string RequiredText(JsonElement args, string name, int maxLength)
    {
        var value = Get(args, name);
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && text.Length <= maxLength && !string.IsNullOrWhiteSpace(text)) return text;
        throw new ArgumentException($"Provide a nonempty {name} of at most {maxLength} characters.");
    }

    private static int Integer(JsonElement args, string name, int fallback, int min, int max)
    {
        var value = Get(args, name);
        if (value.ValueKind == JsonValueKind.Undefined) return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max) return number;
        throw new ArgumentException($"Provide {name} as an integer from {min} to {max}.");
    }
}
