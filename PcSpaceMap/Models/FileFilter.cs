using System.IO.Enumeration;

namespace PcSpaceMap.Models;

public sealed record FileFilter(string Name, long MinimumBytes, DateTime? ModifiedBefore, DateTime? ModifiedSince = null)
{
    public bool Matches(ScanNode node) => node.Size >= MinimumBytes
        && (ModifiedBefore is null || node.LastWriteUtc != DateTime.MinValue && node.LastWriteUtc <= ModifiedBefore)
        && (ModifiedSince is null || node.LastWriteUtc != DateTime.MinValue && node.LastWriteUtc >= ModifiedSince)
        && MatchesName(node.Name);

    public bool MatchesName(string name) => Name.Contains('*') || Name.Contains('?')
        ? FileSystemName.MatchesSimpleExpression(Name, name, ignoreCase: true)
        : name.Contains(Name, StringComparison.OrdinalIgnoreCase);
}
