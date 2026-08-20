using System;
using System.Runtime.InteropServices;
using Shawn.Utils;

namespace _1RM.Utils.WindowsApi
{
    /// <summary>
    /// Re-registers an existing top-level HWND with the taskbar without changing
    /// the window's activation or minimization state.
    ///
    /// This method must not participate in the normal Activated/Deactivated flow:
    /// repeated AddTab/ActivateTab calls during a taskbar click can alter the
    /// taskbar button's native click-to-minimize behaviour. Call it only after a
    /// narrowly detected failed taskbar transition or after the taskbar is rebuilt.
    /// </summary>
    internal static class TaskbarWindowRepair
    {
        private const int GwlExStyle = -20;
        private const uint GwOwner = 4;
        private const long WsExToolWindow = 0x00000080L;
        private const long WsExAppWindow = 0x00040000L;

        public static bool TryRegister(IntPtr hwnd, string reason)
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

            try
            {
                taskbarObject = new CTaskbarList();
                var taskbar = (ITaskbarList)taskbarObject;

                hrInit = taskbar.HrInit();
                if (hrInit >= 0)
                {
                    // Deliberately do not call ActivateTab or SetActiveAlt here.
                    // Those APIs affect the taskbar's active-item bookkeeping and
                    // can break the normal "click active button to minimize" path.
                    hrAdd = taskbar.AddTab(hwnd);
                }

                SimpleLogHelper.DebugInfo(
                    $"Taskbar repair ({reason}): hwnd=0x{hwnd.ToInt64():X}, visible={visible}, " +
                    $"owner=0x{owner.ToInt64():X}, exStyle=0x{exStyle:X}, " +
                    $"WS_EX_APPWINDOW={hasAppWindow}, WS_EX_TOOLWINDOW={hasToolWindow}, " +
                    $"HrInit={FormatHResult(hrInit)}, AddTab={FormatHResult(hrAdd)}");

                return hrInit >= 0 && hrAdd >= 0;
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
