using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace PcSpaceMap;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _trayImage;
    private TrayOptions _trayOptions = new();
    private WindowState _restoreState = WindowState.Normal;
    private bool _exitRequested;
    private bool _allowClose;
    private bool _readingStartupChoice;

    internal bool InitializeTray()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("/PC Space Map;component/Assets/PcSpaceMap.ico", UriKind.Relative));
            if (resource is null) return false;
            using (var stream = resource.Stream)
            using (var icon = new Icon(stream))
                _trayImage = (Icon)icon.Clone();

            _trayOptions = TrayOptions.Load();
            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.Items.Add("Open PC Space Map", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
            _trayMenu.Items.Add("Windows Service…", null, (_, _) => Dispatcher.BeginInvoke(() =>
            {
                RestoreFromTray();
                OpenServiceWindow();
            }));
            var hide = _trayMenu.Items.Add("Hide to tray", null, (_, _) => Dispatcher.BeginInvoke(HideToTray));
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            AddTrayPreference("Close button hides to tray", _trayOptions.CloseToTray, value => _trayOptions.CloseToTray = value);
            AddTrayPreference("Minimize to tray", _trayOptions.MinimizeToTray, value => _trayOptions.MinimizeToTray = value);
            AddStartupMenu();
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add("Exit PC Space Map", null, (_, _) => Dispatcher.BeginInvoke(async () => await ExitAsync()));
            _trayMenu.Opening += (_, _) => hide.Enabled = IsVisible && WindowState != WindowState.Minimized;
            _trayIcon = new Forms.NotifyIcon { Icon = _trayImage, ContextMenuStrip = _trayMenu, Text = "PC Space Map" };
            _trayIcon.MouseDoubleClick += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(RestoreFromTray);
            };
            _trayIcon.Visible = true;
            StateChanged += TrayWindow_StateChanged;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            DisposeTray();
            return false;
        }
    }

    private void AddStartupMenu()
    {
        var menu = new Forms.ToolStripMenuItem("Start with Windows");
        var options = new (DesktopStartupMode Mode, Forms.ToolStripMenuItem Item)[]
        {
            (DesktopStartupMode.Off, new("Off")),
            (DesktopStartupMode.Tray, new("Start in tray")),
            (DesktopStartupMode.Window, new("Open main window"))
        };
        void UpdateSelection(DesktopStartupMode selected)
        {
            foreach (var (mode, item) in options) item.Checked = mode == selected;
        }

        menu.DropDownOpening += (_, _) =>
        {
            try
            {
                UpdateSelection(DesktopStartup.ReadConfiguredMode());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                foreach (var (_, item) in options) item.Checked = false;
            }
        };
        foreach (var (mode, item) in options)
        {
            item.Click += (_, _) =>
            {
                if (ChangeStartupMode(mode)) UpdateSelection(mode);
            };
            menu.DropDownItems.Add(item);
        }
        _trayMenu!.Items.Add(menu);
    }

    private void RefreshStartupChoice()
    {
        _readingStartupChoice = true;
        try
        {
            var selected = DesktopStartup.ReadConfiguredMode();
            DesktopStartupBox.SelectedItem = DesktopStartupBox.Items.OfType<ComboBoxItem>()
                .First(item => Equals(item.Tag, selected));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DesktopStartupBox.SelectedItem = null;
            DesktopStartupBox.ToolTip = "Could not read Windows startup. " + ex.Message;
        }
        finally { _readingStartupChoice = false; }
    }

    private void DesktopStartupBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_readingStartupChoice && DesktopStartupBox.SelectedItem is ComboBoxItem { Tag: DesktopStartupMode mode })
            ChangeStartupMode(mode);
    }

    private bool ChangeStartupMode(DesktopStartupMode mode)
    {
        try
        {
            DesktopStartup.SetConfiguredMode(mode);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show("Could not change Windows startup. " + ex.Message, "PC Space Map",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { RefreshStartupChoice(); }
    }

    private void AddTrayPreference(string name, bool selected, Action<bool> set)
    {
        var item = new Forms.ToolStripMenuItem(name) { CheckOnClick = true, Checked = selected };
        item.Click += (_, _) =>
        {
            set(item.Checked);
            _trayOptions.Save();
        };
        _trayMenu!.Items.Add(item);
    }

    private void TrayWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        else if (_trayOptions.MinimizeToTray && IsVisible && !_exitRequested) HideToTray();
    }

    internal void HideToTray()
    {
        if (_trayIcon is null || _exitRequested) return;
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        if (_exitRequested) return;
        ShowInTaskbar = true;
        WindowState = _restoreState;
        Show();
        Activate();
    }

    internal void AllowSystemExit()
    {
        _allowClose = true;
        foreach (var workspace in GetWorkspaces()) workspace.CloseWorkspace();
    }

    internal IntPtr HandleInstallerShutdown(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int endSession = 0x0016;
        const long closeForUpdate = 0x00000001;
        if (message == endSession && wParam != IntPtr.Zero && (lParam.ToInt64() & closeForUpdate) != 0)
        {
            handled = true;
            _ = Dispatcher.BeginInvoke(async () => await ExitAsync());
        }
        return IntPtr.Zero;
    }

    internal async Task ExitAsync()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        foreach (var workspace in GetWorkspaces()) workspace.CloseWorkspace();
        var deadline = Environment.TickCount64 + 8000;
        while (AnyWorkspaceScanning() && Environment.TickCount64 < deadline)
            await Task.Delay(100);
        _allowClose = true;
        Close();
    }

    private void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _trayImage?.Dispose();
        _trayIcon = null;
        _trayMenu = null;
        _trayImage = null;
    }

    private sealed class TrayOptions
    {
        private static string SettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCSpaceMap", "tray-settings.json");

        public bool CloseToTray { get; set; } = true;
        public bool MinimizeToTray { get; set; } = true;

        internal static TrayOptions Load()
        {
            try { return JsonSerializer.Deserialize<TrayOptions>(File.ReadAllText(SettingsPath)) ?? new(); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        }

        internal void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the change for this session if the profile is read-only.
            }
        }
    }
}
