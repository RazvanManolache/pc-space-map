using System.Security.AccessControl;
using System.Security.Principal;
using PcSpaceMap.Services;

internal static class ServiceControlAccessChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var account = new SecurityIdentifier("S-1-5-21-100-200-300-1001");
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:(A;;0xF01FF;;;SY)(A;;0x00020004;;;AU)");
        var source = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(source, 0);
        var granted = ServiceControlAccess.AddLifecycleAccess(source, account);
        var updated = new RawSecurityDescriptor(granted, 0);
        var entries = updated.DiscretionaryAcl!.OfType<CommonAce>().ToArray();
        var existing = descriptor.DiscretionaryAcl!.OfType<CommonAce>().ToArray();
        var added = entries.Single(ace => ace.SecurityIdentifier == account);
        check(added.AceQualifier == AceQualifier.AccessAllowed && added.AccessMask == 0x34
            && existing.All(ace => entries.Any(x => x.Equals(ace))),
            "service access grants only query, start and stop to the configured user and preserves existing entries");
        check(granted.SequenceEqual(ServiceControlAccess.AddLifecycleAccess(granted, account)),
            "granting the same service permissions twice is idempotent");
        try
        {
            ServiceControlAccess.AddLifecycleAccess(source, new SecurityIdentifier(WellKnownSidType.WorldSid, null));
            check(false, "service control does not accept a broad account group");
        }
        catch (ArgumentException) { check(true, "service control does not accept a broad account group"); }
    }
}
