using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace PcSpaceMap.Services;

public static class ServiceControlAccess
{
    private const uint Connect = 0x0001;
    private const int LifecycleRights = 0x0004 | 0x0010 | 0x0020; // Query status, start, stop.
    private const uint ReadAndWriteDacl = 0x00020000 | 0x00040000;
    private const uint DaclInformation = 4;

    public static bool CanControl()
    {
        using var manager = OpenSCManager(null, null, Connect);
        if (manager.IsInvalid) return false;
        using var service = OpenService(manager, ServiceProtocol.Name, LifecycleRights);
        return !service.IsInvalid;
    }

    internal static void GrantLifecycleAccess(SecurityIdentifier account)
    {
        using var manager = OpenSCManager(null, null, Connect);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var service = OpenService(manager, ServiceProtocol.Name, ReadAndWriteDacl);
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());

        if (!QueryServiceObjectSecurity(service, DaclInformation, null, 0, out var required)
            && Marshal.GetLastWin32Error() != 122)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var data = new byte[required];
        if (!QueryServiceObjectSecurity(service, DaclInformation, data, required, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var updated = AddLifecycleAccess(data, account);
        if (!SetServiceObjectSecurity(service, DaclInformation, updated))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static byte[] AddLifecycleAccess(byte[] data, SecurityIdentifier account)
    {
        if (!account.IsAccountSid() || account.IsWellKnown(WellKnownSidType.AnonymousSid))
            throw new ArgumentException("Expected the Windows account configured for PC Space Map.", nameof(account));
        if (new RawSecurityDescriptor(data, 0).DiscretionaryAcl is null)
            throw new InvalidOperationException("The service has an unexpected unrestricted access list.");
        var descriptor = new CommonSecurityDescriptor(false, false, data, 0);
        var dacl = descriptor.DiscretionaryAcl ?? throw new InvalidOperationException("The service access list is missing.");
        dacl.AddAccess(AccessControlType.Allow, account, LifecycleRights,
            InheritanceFlags.None, PropagationFlags.None);
        var result = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(result, 0);
        return result;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceObjectSecurity(ServiceHandle service, uint information, [Out] byte[]? descriptor,
        uint size, out uint required);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceObjectSecurity(ServiceHandle service, uint information, byte[] descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
