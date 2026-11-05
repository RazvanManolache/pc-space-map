using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PcSpaceMap.Controls;

internal sealed class WindowsItemMenu : IDisposable
{
    private const uint FirstCommand = 1;
    private const uint LastCommand = 0x7FFF;
    private readonly IContextMenu _contextMenu;
    private readonly IntPtr _menu;

    private WindowsItemMenu(string path)
    {
        var itemId = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, IntPtr.Zero, in itemId, out var item));
        try
        {
            var handlerId = new Guid("3981E225-F559-11D3-8E3A-00C04F6837D5"); // BHID_SFUIObject
            var menuId = typeof(IContextMenu).GUID;
            Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, in handlerId, in menuId, out _contextMenu));
        }
        finally { Marshal.ReleaseComObject(item); }

        try
        {
            _menu = CreatePopupMenu();
            if (_menu == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(_contextMenu.QueryContextMenu(_menu, 0, FirstCommand, LastCommand, 0));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static void Show(HwndSource owner, string path, int x, int y)
    {
        using var menu = new WindowsItemMenu(path);
        HwndSourceHook hook = menu.ForwardMenuMessage;
        owner.AddHook(hook);
        uint selected;
        try
        {
            const uint returnCommandAndAllowRightClick = 0x100 | 0x2;
            selected = TrackPopupMenuEx(menu._menu, returnCommandAndAllowRightClick, x, y, owner.Handle, IntPtr.Zero);
        }
        finally { owner.RemoveHook(hook); }
        if (selected < FirstCommand || selected > LastCommand) return;

        var command = new InvokeInfo
        {
            Size = Marshal.SizeOf<InvokeInfo>(),
            Owner = owner.Handle,
            Verb = (IntPtr)(selected - FirstCommand),
            Show = 1
        };
        Marshal.ThrowExceptionForHR(menu._contextMenu.InvokeCommand(ref command));
    }

    private IntPtr ForwardMenuMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int initPopup = 0x117, drawItem = 0x2B, measureItem = 0x2C, menuChar = 0x120;
        if (message is not (initPopup or drawItem or measureItem or menuChar)) return IntPtr.Zero;
        if ((message is drawItem or measureItem) && wParam != IntPtr.Zero) return IntPtr.Zero;
        if (_contextMenu is IContextMenu3 menu3)
        {
            handled = menu3.HandleMenuMsg2(message, wParam, lParam, out var result) == 0;
            return result;
        }
        if (message != menuChar && _contextMenu is IContextMenu2 menu2)
            handled = menu2.HandleMenuMsg(message, wParam, lParam) == 0;
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_menu != IntPtr.Zero) DestroyMenu(_menu);
        Marshal.ReleaseComObject(_contextMenu);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InvokeInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Owner, Verb, Parameters, Directory;
        public int Show;
        public uint HotKey;
        public IntPtr Icon;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, in Guid handlerId, in Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IContextMenu contextMenu);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] new int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] new int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] new int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
        [PreserveSig] int HandleMenuMsg(int message, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3 : IContextMenu2
    {
        [PreserveSig] new int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] new int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] new int GetCommandString(UIntPtr command, uint type, IntPtr reserved, IntPtr name, uint length);
        [PreserveSig] new int HandleMenuMsg(int message, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(int message, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, in Guid interfaceId, out IShellItem item);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr options);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);
}
