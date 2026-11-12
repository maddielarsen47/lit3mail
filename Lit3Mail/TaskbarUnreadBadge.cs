using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Lit3Mail;

internal static class TaskbarUnreadBadge
{
    public static readonly uint ButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

    public static void Apply(IntPtr window, bool visible)
    {
        IntPtr icon = IntPtr.Zero;
        ITaskbarList3? taskbar = null;
        try
        {
            taskbar = (ITaskbarList3)new TaskbarList();
            taskbar.HrInit();
            if (visible)
            {
                using var bitmap = new Bitmap(16, 16);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(Brushes.White, 2, 2, 12, 12);
                using var red = new SolidBrush(Color.FromArgb(223, 40, 53));
                graphics.FillEllipse(red, 3, 3, 10, 10);
                icon = bitmap.GetHicon();
            }
            taskbar.SetOverlayIcon(window, icon, visible ? "New unread mail" : null);
        }
        catch (Exception error) when (error is COMException or InvalidCastException)
        {
            // The taskbar may be restarting. The next TaskbarButtonCreated message reapplies the badge.
        }
        finally
        {
            if (icon != IntPtr.Zero)
                DestroyIcon(icon);
            if (taskbar is not null)
                Marshal.ReleaseComObject(taskbar);
        }
    }

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);
        void MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr window, ulong completed, ulong total);
        void SetProgressState(IntPtr window, uint state);
        void RegisterTab(IntPtr tab, IntPtr owner);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr before);
        void SetTabActive(IntPtr tab, IntPtr owner, uint reserved);
        void ThumbBarAddButtons(IntPtr window, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr window, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr window, IntPtr imageList);
        void SetOverlayIcon(IntPtr window, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }
}
