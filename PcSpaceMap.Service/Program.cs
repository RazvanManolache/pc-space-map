using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.EventLog;
using PcSpaceMap.Service;
using PcSpaceMap.Services;

if (args is ["--mcp"])
{
    await IndexMcpServer.RunAsync();
    return;
}

if (args.FirstOrDefault() == "--manage")
{
    Environment.ExitCode = ServiceManager.Run(args.Skip(1).ToArray());
    return;
}

if (WindowsServiceHelpers.IsWindowsService() && !WindowsIdentity.GetCurrent().IsSystem)
    throw new InvalidOperationException("The registered index service must run as LocalSystem.");

var directory = AppContext.BaseDirectory;
var settings = JsonSerializer.Deserialize<ServiceProtocol.Settings>(File.ReadAllText(Path.Combine(directory, "service.json")))
    ?? throw new InvalidDataException("Missing service configuration.");
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = directory, Args = args });
builder.Services.AddWindowsService(options => options.ServiceName = ServiceProtocol.Name);
builder.Services.Configure<EventLogSettings>(options => options.SourceName = ServiceProtocol.Name);
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<IndexWorker>();
await builder.Build().RunAsync();
