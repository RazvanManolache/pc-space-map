using System.IO;
using Microsoft.Win32;

namespace PcSpaceMap;

public enum DesktopStartupMode { Off, Tray, Window }

internal static class DesktopStartup
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string BackgroundArgument = "--background";
    private const string ValueName = "PC Space Map";

    internal static DesktopStartupMode ReadConfiguredMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return Read(key);
    }

    internal static void SetConfiguredMode(DesktopStartupMode mode)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Could not locate PC Space Map.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        Write(key, mode, executable);
    }

    internal static DesktopStartupMode Read(RegistryKey? key) =>
        key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command)
            ? command.EndsWith(" " + BackgroundArgument, StringComparison.OrdinalIgnoreCase)
                ? DesktopStartupMode.Tray : DesktopStartupMode.Window
            : DesktopStartupMode.Off;

    internal static void Write(RegistryKey key, DesktopStartupMode mode, string executable)
    {
        if (mode == DesktopStartupMode.Off)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var command = $"\"{executable}\"" + (mode == DesktopStartupMode.Tray ? " " + BackgroundArgument : "");
        if (command.Length > 260)
            throw new IOException("The app path is too long for Windows startup. Use a shorter installation path.");
        key.SetValue(ValueName, command, RegistryValueKind.String);
    }
}
