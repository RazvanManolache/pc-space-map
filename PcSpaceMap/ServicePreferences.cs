using System.Diagnostics;
using System.IO;
using System.ComponentModel;
using System.ServiceProcess;
using System.Text.Json;
using PcSpaceMap.Services;

namespace PcSpaceMap;

internal static class ServicePreferences
{
    internal static string WorkerPath => Path.Combine(ServiceProtocol.InstallPath, "PcSpaceMap.Service.exe");

    internal static Version? ReadWorkerVersion()
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(WorkerPath);
            return new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    internal static ServiceProtocol.Settings? ReadSettings()
    {
        try
        {
            return JsonSerializer.Deserialize<ServiceProtocol.Settings>(
                File.ReadAllText(Path.Combine(ServiceProtocol.InstallPath, "service.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static bool IsInstalled()
    {
        try
        {
            using var service = new ServiceController(ServiceProtocol.Name);
            _ = service.Status;
            return true;
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 1060 })
        { return false; }
    }
}
