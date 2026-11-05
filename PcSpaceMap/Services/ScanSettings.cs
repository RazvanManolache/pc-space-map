using System.IO;
using System.Text.Json;

namespace PcSpaceMap.Services;

public sealed class ScanSettings
{
    public bool PreferNtfs { get; set; } = true;
    public int Parallelism { get; set; } = Math.Clamp(Environment.ProcessorCount, 2, 8);
    public bool AutoTuneReaders { get; set; } = true;
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "scan-settings.json");
    public static ScanSettings Load()
    {
        try { return JsonSerializer.Deserialize<ScanSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
    public DiskScanner Scanner() => new() { PreferNtfs = PreferNtfs, AutoTuneReaders = AutoTuneReaders,
        Parallelism = AutoTuneReaders ? Math.Clamp(Environment.ProcessorCount, 1, 16) : Math.Clamp(Parallelism, 1, 16) };
}
