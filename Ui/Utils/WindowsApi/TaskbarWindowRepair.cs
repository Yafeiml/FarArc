using System;
using System.Runtime.InteropServices;
using Shawn.Utils;

namespace _1RM.Utils.WindowsApi
{
    /// <summary>
    /// Repairs the association between a top-level HWND and the Windows taskbar.
    ///
    /// WPF's ShowInTaskbar dependency property can still be true while a replacement
    /// taskbar implementation has lost its internal task item for the HWND. Reassigning
    /// ShowInTaskbar = true is then a no-op, because no dependency-property change occurs.
    /// ITaskbarList.AddTab explicitly asks the shell/taskbar implementation to register
    /// the window again without hiding or recreating the WPF window.
    /// </summary>
    internal static class TaskbarWindowRepair
    {
        private const int GwlExStyle = -20;
        private const uint GwOwner = 4;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;

        public static bool TryRegister(IntPtr hwnd, bool activate, string reason)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                SimpleLogHelper.DebugWarning($"Taskbar repair skipped ({reason}): invalid HWND 0x{hwnd.ToInt64():X}");
                return false;
            }

            bool visible = IsWindowVisible(hwnd);
            IntPtr owner = GetWindow(hwnd, GwOwner);
            long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            bool hasAppWindow = (exStyle & WsExAppWindow) != 0;
            bool hasToolWindow = (exStyle & WsExToolWindow) != 0;

            object? taskbarObject = null;
            int hrInit = unchecked((int)0x80004005);
            int hrAdd = unchecked((int)0x80004005);
            int hrActivate = 0;

            try
            {
                taskbarObject = new CTaskbarList();
                var taskbar = (ITaskbarList)taskbarObject;

                hrInit = taskbar.HrInit();
                if (hrInit >= 0)
                {
                    hrAdd = taskbar.AddTab(hwnd);
                    if (hrAdd >= 0 && activate)
                    {
                        hrActivate = taskbar.ActivateTab(hwnd);
                    }
                }

                SimpleLogHelper.DebugInfo(
                    $"Taskbar repair ({reason}): hwnd=0x{hwnd.ToInt64():X}, visible={visible}, " +
                    $"owner=0x{owner.ToInt64():X}, exStyle=0x{exStyle:X}, " +
                    $"WS_EX_APPWINDOW={hasAppWindow}, WS_EX_TOOLWINDOW={hasToolWindow}, " +
                    $"HrInit={FormatHResult(hrInit)}, AddTab={FormatHResult(hrAdd)}, " +
                    $"ActivateTab={(activate ? FormatHResult(hrActivate) : "not-requested")}");

                return hrInit >= 0 && hrAdd >= 0 && (!activate || hrActivate >= 0);
            }
            catch (Exception ex)
            {
                SimpleLogHelper.DebugWarning(
                    $"Taskbar repair failed ({reason}): hwnd=0x{hwnd.ToInt64():X}, " +
                    $"owner=0x{owner.ToInt64():X}, exStyle=0x{exStyle:X}");
                SimpleLogHelper.Warning(ex);
                return false;
            }
            finally
            {
                if (taskbarObject != null && Marshal.IsComObject(taskbarObject))
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(taskbarObject);
                    }
                    catch
                    {
                        // Best-effort COM cleanup only.
                    }
                }
            }
        }

        private static string FormatHResult(int value)
        {
            return $"0x{unchecked((uint)value):X8}";
        }

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hwnd, index)
                : new IntPtr(GetWindowLong32(hwnd, index));
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [ComImport]
        [Guid("56FDF344-FD6D-11D0-958A-006097C9A090")]
        private class CTaskbarList
        {
        }

        [ComImport]
        [Guid("56FDF342-FD6D-11D0-958A-006097C9A090")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList
        {
            [PreserveSig]
            int HrInit();

            [PreserveSig]
            int AddTab(IntPtr hwnd);

            [PreserveSig]
            int DeleteTab(IntPtr hwnd);

            [PreserveSig]
            int ActivateTab(IntPtr hwnd);

            [PreserveSig]
            int SetActiveAlt(IntPtr hwnd);
        }
    }
}
