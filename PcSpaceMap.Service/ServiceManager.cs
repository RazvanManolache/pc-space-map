using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text.Json;
using PcSpaceMap.Services;

namespace PcSpaceMap.Service;

internal static class ServiceManager
{
    public static int Run(string[] args)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        try
        {
            var destination = ServiceProtocol.InstallPath;
            var installedExe = Path.Combine(destination, "PcSpaceMap.Service.exe");
            var operation = args.FirstOrDefault();
            if (operation is not ("install" or "configure" or "start" or "stop" or "upgrade" or "remove" or "authorize-controls")) return 2;
            if (operation is ("configure" or "start" or "stop") && !string.Equals(Environment.ProcessPath, installedExe, StringComparison.OrdinalIgnoreCase))
                return 5;

            using var service = new ServiceController(ServiceProtocol.Name);
            if (operation is "upgrade" or "remove" or "authorize-controls")
            {
                ValidateInstallDirectory(destination);
                using var registration = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceProtocol.Name);
                if (!string.Equals((registration?.GetValue("ImagePath") as string)?.Trim().Trim('"'), installedExe, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The registered service does not point to this installation.");
                if (operation == "authorize-controls")
                {
                    GrantConfiguredControls(destination);
                    return 0;
                }
                var running = service.Status != ServiceControllerStatus.Stopped;
                Stop(service);
                try
                {
                    if (operation == "remove") ConfigureService("delete", ServiceProtocol.Name);
                    else ReplacePayload(destination, () =>
                    {
                        GrantConfiguredControls(destination);
                        if (!running) return;
                        try { Start(service); }
                        catch { Stop(service); throw; }
                    });
                }
                catch
                {
                    if (running) Start(service);
                    throw;
                }
                return 0;
            }
            if (operation is "start" or "stop")
            {
                if (operation == "start") Start(service); else Stop(service);
                return 0;
            }

            if (args.Length < 5 || !bool.TryParse(args[2], out var live) || !bool.TryParse(args[3], out var autoStart))
                return 2;
            var config = Path.Combine(destination, "service.json");
            if (Directory.Exists(destination)) ValidateInstallDirectory(destination);
            var previous = File.Exists(config)
                ? JsonSerializer.Deserialize<ServiceProtocol.Settings>(File.ReadAllText(config))
                : null;
            if (previous is null && (operation != "install" || Directory.Exists(destination)))
                throw new InvalidDataException("The service configuration is missing or invalid.");
            var client = new SecurityIdentifier(previous?.ClientSid ?? args[1]);
            if (!client.IsAccountSid() || client.IsWellKnown(WellKnownSidType.AnonymousSid)) return 2;
            var maxConcurrent = previous?.MaxConcurrentScans ?? 1;
            var firstDrive = 4;
            const string concurrencyOption = "--concurrent-scans=";
            if (args[firstDrive].StartsWith(concurrencyOption, StringComparison.Ordinal))
            {
                if (!int.TryParse(args[firstDrive][concurrencyOption.Length..], out maxConcurrent)
                    || maxConcurrent < 1 || maxConcurrent > ServiceProtocol.MaxScanSlots) return 2;
                firstDrive++;
            }
            if (firstDrive >= args.Length) return 2;
            var volumes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in args.Skip(firstDrive).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!ServiceProtocol.IsDriveRoot(root)) return 2;
                var drive = new DriveInfo(root);
                var fingerprint = WindowsVolume.VolumeFingerprint(root);
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS" || fingerprint is null)
                    return 2;
                volumes.Add(root, fingerprint);
            }
            var settings = new ServiceProtocol.Settings(client.Value, volumes, live, autoStart, maxConcurrent);

            if (operation == "install")
            {
                if (ServiceController.GetServices().Any(s => s.ServiceName == ServiceProtocol.Name)) return 3;
                if (Directory.Exists(destination))
                {
                    ReplacePayload(destination, () =>
                    {
                        SaveSettings(config, settings);
                        Register(installedExe, settings);
                    });
                }
                else Install(destination, installedExe, settings, client);
                Start(service);
            }
            else
            {
                var running = service.Status != ServiceControllerStatus.Stopped;
                if (running) Stop(service);
                SaveSettings(config, settings);
                ConfigureService("config", ServiceProtocol.Name, "start=", autoStart ? "auto" : "demand");
                if (running) Start(service);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void GrantConfiguredControls(string directory)
    {
        var settings = JsonSerializer.Deserialize<ServiceProtocol.Settings>(File.ReadAllText(Path.Combine(directory, "service.json")))
            ?? throw new InvalidDataException("The service configuration is missing or invalid.");
        ServiceControlAccess.GrantLifecycleAccess(new SecurityIdentifier(settings.ClientSid));
    }

    private static void SaveSettings(string path, ServiceProtocol.Settings settings)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings));
            SetAdministratorOwner(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void Register(string executable, ServiceProtocol.Settings settings)
    {
        ConfigureService("create", ServiceProtocol.Name, "binPath=", '"' + executable + '"',
            "obj=", "LocalSystem", "start=", settings.AutoStart ? "auto" : "demand", "DisplayName=", "PC Space Map Index");
        try { ServiceControlAccess.GrantLifecycleAccess(new SecurityIdentifier(settings.ClientSid)); }
        catch
        {
            ConfigureService("delete", ServiceProtocol.Name);
            throw;
        }
    }

    private static void ValidateInstallDirectory(string directory)
    {
        foreach (var path in new[] { directory, Path.Combine(directory, "Data") })
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The service installation directory is missing or is a link.");
            ValidateProtectedAcl(info.GetAccessControl());
        }
        foreach (var file in new[] { "service.json", "PcSpaceMap.Service.exe", "e_sqlite3.dll" })
        {
            var info = new FileInfo(Path.Combine(directory, file));
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The service installation contains an unexpected link.");
            ValidateProtectedAcl(info.GetAccessControl());
        }
    }

    private static void ValidateProtectedAcl(FileSystemSecurity acl)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        if (!Equals(acl.GetOwner(typeof(SecurityIdentifier)), administrators)
            || acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(rule => rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & write) != 0
                    && !rule.IdentityReference.Equals(administrators) && !rule.IdentityReference.Equals(system)))
            throw new IOException("The service installation has unexpected ownership or write permissions.");
    }

    private static void ReplacePayload(string destination, Action afterReplace)
    {
        var staged = new List<(string Target, string New, string Backup)>();
        var committed = false;
        try
        {
            foreach (var (name, source) in new[]
            {
                ("PcSpaceMap.Service.exe", Environment.ProcessPath!),
                ("e_sqlite3.dll", Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll"))
            })
            {
                var target = Path.Combine(destination, name);
                var tag = Guid.NewGuid().ToString("N");
                var entry = (Target: target, New: target + "." + tag, Backup: target + ".backup-" + tag);
                staged.Add(entry);
                File.Copy(source, entry.New);
                SetAdministratorOwner(entry.New);
            }
            foreach (var entry in staged)
            {
                File.Move(entry.Target, entry.Backup);
                File.Move(entry.New, entry.Target);
            }
            afterReplace();
            committed = true;
        }
        catch
        {
            foreach (var entry in staged.AsEnumerable().Reverse())
                if (File.Exists(entry.Backup)) File.Move(entry.Backup, entry.Target, overwrite: true);
            throw;
        }
        finally
        {
            foreach (var entry in staged)
            {
                File.Delete(entry.New);
                if (committed) File.Delete(entry.Backup);
            }
        }
    }

    private static void SetAdministratorOwner(string path)
    {
        var info = new FileInfo(path);
        var acl = info.GetAccessControl();
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        info.SetAccessControl(acl);
    }

    private static void Install(string destination, string executable, ServiceProtocol.Settings settings, SecurityIdentifier client)
    {
        var native = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
        if (!File.Exists(native)) throw new IOException("The service package is missing e_sqlite3.dll.");
        var staging = destination + ".setup-" + Guid.NewGuid().ToString("N");
        CreateProtectedDirectory(staging, client);
        try
        {
            CreateProtectedDirectory(Path.Combine(staging, "Data"), client);
            File.Copy(Environment.ProcessPath!, Path.Combine(staging, "PcSpaceMap.Service.exe"));
            File.Copy(native, Path.Combine(staging, "e_sqlite3.dll"));
            SetAdministratorOwner(Path.Combine(staging, "PcSpaceMap.Service.exe"));
            SetAdministratorOwner(Path.Combine(staging, "e_sqlite3.dll"));
            SaveSettings(Path.Combine(staging, "service.json"), settings);
            Directory.Move(staging, destination);
        }
        catch
        {
            RemoveUnregisteredFiles(staging);
            throw;
        }
        try
        {
            Register(executable, settings);
        }
        catch
        {
            if (!ServiceController.GetServices().Any(service => service.ServiceName == ServiceProtocol.Name))
                RemoveUnregisteredFiles(destination);
            throw;
        }
    }

    private static void RemoveUnregisteredFiles(string directory)
    {
        foreach (var name in new[] { "PcSpaceMap.Service.exe", "e_sqlite3.dll", "service.json" })
            File.Delete(Path.Combine(directory, name));
        var data = Path.Combine(directory, "Data");
        if (Directory.Exists(data)) Directory.Delete(data);
        Directory.Delete(directory);
    }

    private static void CreateProtectedDirectory(string path, SecurityIdentifier client)
    {
        var access = new DirectorySecurity();
        access.SetAccessRuleProtection(true, false);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        access.SetOwner(administrators);
        const InheritanceFlags children = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { system, administrators })
            access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, children, PropagationFlags.None, AccessControlType.Allow));
        access.AddAccessRule(new FileSystemAccessRule(client, FileSystemRights.ReadAndExecute, children, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(access);
    }

    private static void Start(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Running) return;
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    private static void Stop(ServiceController service)
    {
        service.Refresh();
        if (service.Status == ServiceControllerStatus.Stopped) return;
        if (service.Status != ServiceControllerStatus.StopPending) service.Stop(stopDependentServices: false);
        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }

    private static void ConfigureService(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { CreateNoWindow = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the Windows service manager.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Windows service manager returned " + process.ExitCode);
    }
}
