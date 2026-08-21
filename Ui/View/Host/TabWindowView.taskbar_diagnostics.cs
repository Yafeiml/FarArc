using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Shawn.Utils;

namespace _1RM.View.Host
{
    /// <summary>
    /// Windows 11 25H2 taskbar compatibility for the remote-session window.
    ///
    /// The fix has three deliberately narrow parts:
    /// 1. assign a stable window-level AppUserModelID before the taskbar button is created;
    /// 2. pause 1Remote's periodic protocol-focus correction while Explorer/StartAllBack
    ///    is processing a taskbar click;
    /// 3. complete the expected minimize only when the exact recorded failure sequence
    ///    occurs (WA_INACTIVE over the task list -> immediate WA_ACTIVE, with no native
    ///    SC_MINIMIZE/WM_SIZE(SIZE_MINIMIZED)).
    ///
    /// It does not call ITaskbarList, SetForegroundWindow, Activate, Show/Hide, toggle
    /// ShowInTaskbar, change ownership, or consume native messages.
    /// </summary>
    public partial class TabWindowView
    {
        private const string TaskbarAppUserModelId = "1Remote.RemoteSession";

        private const int TaskbarWmSize = 0x0005;
        private const int TaskbarWmActivate = 0x0006;
        private const int TaskbarWmSysCommand = 0x0112;

        private const int TaskbarWaInactive = 0;
        private const int TaskbarWaActive = 1;
        private const int TaskbarWaClickActive = 2;
        private const int TaskbarSizeMinimized = 1;
        private const int TaskbarScMinimize = 0xF020;

        private const uint TaskbarGaRoot = 2;
        private const int TaskbarCompletionDelayMilliseconds = 260;
        private const int TaskbarFocusSuppressionMilliseconds = 750;

        private static readonly int TaskbarCreatedMessage =
            TaskbarRegisterWindowMessage("TaskbarCreated");

        private static readonly PropertyKey AppUserModelIdKey = new PropertyKey(
            new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            5);

        private HwndSource? _taskbarFixHwndSource;
        private DispatcherTimer? _taskbarFixTimer;
        private IntPtr _taskbarFixHwnd = IntPtr.Zero;
        private bool _taskbarFixDisabled;
        private bool _taskbarClickCandidate;
        private bool _taskbarClickReactivated;
        private DateTime _taskbarClickCandidateStartedUtc = DateTime.MinValue;

        // Read by TabWindowView.xaml_timer.cs from its 100 ms background timer.
        // Interlocked access keeps the cross-thread hand-off atomic.
        private long _taskbarFocusSuppressedUntilUtcTicks;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarFixHwnd = new WindowInteropHelper(this).Handle;
            _myHandle = _taskbarFixHwnd;

            TrySetWindowAppUserModelId(_taskbarFixHwnd, TaskbarAppUserModelId);

            try
            {
                _taskbarFixHwndSource = HwndSource.FromHwnd(_taskbarFixHwnd);
                _taskbarFixHwndSource?.AddHook(TaskbarFixWndProc);

                _taskbarFixTimer = new DispatcherTimer(
                    DispatcherPriority.Background,
                    Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(TaskbarCompletionDelayMilliseconds),
                };
                _taskbarFixTimer.Tick += TaskbarFixTimerOnTick;

                Closed += TaskbarFixOnClosed;

                SimpleLogHelper.DebugInfo(
                    $"Taskbar final fix initialized: hwnd=0x{_taskbarFixHwnd.ToInt64():X}, " +
                    $"AppUserModelID={TaskbarAppUserModelId}");
            }
            catch (Exception ex)
            {
                DisableTaskbarFix("initialization failed", ex);
            }
        }

        private IntPtr TaskbarFixWndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarFixDisabled)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage)
                {
                    // Explorer has rebuilt its taskbar. Re-setting the same window
                    // property asks the shell to rebuild identity from the HWND
                    // without manipulating taskbar tabs or activation state.
                    Dispatcher.BeginInvoke(
                        DispatcherPriority.Background,
                        new Action(() =>
                        {
                            if (!IsClosed && _taskbarFixHwnd != IntPtr.Zero)
                            {
                                TrySetWindowAppUserModelId(
                                    _taskbarFixHwnd,
                                    TaskbarAppUserModelId);
                            }
                        }));

                    return IntPtr.Zero;
                }

                switch (msg)
                {
                    case TaskbarWmActivate:
                    {
                        int activationState = TaskbarLowWord(wParam);
                        bool minimized = TaskbarHighWord(wParam) != 0;

                        if (activationState == TaskbarWaInactive)
                        {
                            TryArmTaskbarClickCandidate(hwnd, minimized, lParam);
                        }
                        else if ((activationState == TaskbarWaActive ||
                                  activationState == TaskbarWaClickActive) &&
                                 _taskbarClickCandidate &&
                                 !minimized &&
                                 !TaskbarIsIconic(hwnd))
                        {
                            _taskbarClickReactivated = true;
                        }

                        break;
                    }

                    case TaskbarWmSysCommand:
                    {
                        int command = unchecked((int)(wParam.ToInt64() & 0xFFF0L));
                        if (command == TaskbarScMinimize)
                        {
                            ClearTaskbarClickCandidate();
                        }

                        break;
                    }

                    case TaskbarWmSize:
                    {
                        if (unchecked((int)wParam.ToInt64()) == TaskbarSizeMinimized)
                        {
                            ClearTaskbarClickCandidate();
                        }

                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                // A native window hook must never be allowed to tear down the WPF
                // dispatcher. Fail closed and leave normal 1Remote behaviour intact.
                DisableTaskbarFix($"WndProc failed for message 0x{msg:X4}", ex);
            }

            // Observation/compatibility only. Never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarClickCandidate(
            IntPtr hwnd,
            bool minimized,
            IntPtr otherWindow)
        {
            if (_taskbarFixTimer == null ||
                hwnd == IntPtr.Zero ||
                minimized ||
                otherWindow != IntPtr.Zero ||
                !IsLoaded ||
                !IsVisible ||
                !IsActive ||
                WindowState != WindowState.Maximized ||
                !TaskbarIsWindowVisible(hwnd) ||
                TaskbarIsIconic(hwnd) ||
                !TaskbarIsZoomed(hwnd) ||
                !IsTaskListAtCursor())
            {
                ClearTaskbarClickCandidate();
                return;
            }

            Interlocked.Exchange(
                ref _taskbarFocusSuppressedUntilUtcTicks,
                DateTime.UtcNow
                    .AddMilliseconds(TaskbarFocusSuppressionMilliseconds)
                    .Ticks);

            _taskbarClickCandidate = true;
            _taskbarClickReactivated = false;
            _taskbarClickCandidateStartedUtc = DateTime.UtcNow;

            _taskbarFixTimer.Stop();
            _taskbarFixTimer.Interval =
                TimeSpan.FromMilliseconds(TaskbarCompletionDelayMilliseconds);
            _taskbarFixTimer.Start();
        }

        private void TaskbarFixTimerOnTick(object? sender, EventArgs e)
        {
            _taskbarFixTimer?.Stop();

            if (_taskbarFixDisabled || !_taskbarClickCandidate)
            {
                return;
            }

            try
            {
                double elapsedMilliseconds =
                    (DateTime.UtcNow - _taskbarClickCandidateStartedUtc)
                    .TotalMilliseconds;

                bool mustCompleteMissedMinimize =
                    _taskbarClickReactivated &&
                    elapsedMilliseconds >= TaskbarCompletionDelayMilliseconds - 50 &&
                    elapsedMilliseconds < 1500 &&
                    _taskbarFixHwnd != IntPtr.Zero &&
                    IsLoaded &&
                    IsVisible &&
                    IsActive &&
                    WindowState == WindowState.Maximized &&
                    TaskbarIsWindowVisible(_taskbarFixHwnd) &&
                    !TaskbarIsIconic(_taskbarFixHwnd) &&
                    TaskbarIsZoomed(_taskbarFixHwnd);

                ClearTaskbarClickCandidate();

                if (!mustCompleteMissedMinimize)
                {
                    return;
                }

                SimpleLogHelper.DebugInfo(
                    "Taskbar final fix: completing missed native minimize after " +
                    "shell click reactivated the maximized session window.");

                // This is the same WPF state transition used by 1Remote's own
                // title-bar minimize button. It runs only after the shell failed
                // to emit either SC_MINIMIZE or SIZE_MINIMIZED.
                WindowState = WindowState.Minimized;
            }
            catch (Exception ex)
            {
                DisableTaskbarFix("completion callback failed", ex);
            }
        }

        private void ClearTaskbarClickCandidate()
        {
            try
            {
                _taskbarFixTimer?.Stop();
            }
            catch
            {
                // Best-effort cleanup only.
            }

            _taskbarClickCandidate = false;
            _taskbarClickReactivated = false;
            _taskbarClickCandidateStartedUtc = DateTime.MinValue;
        }

        private bool IsTaskListAtCursor()
        {
            if (!TaskbarGetCursorPos(out TaskbarPoint point))
            {
                return false;
            }

            IntPtr child = TaskbarWindowFromPoint(point);
            if (child == IntPtr.Zero)
            {
                return false;
            }

            IntPtr root = TaskbarGetAncestor(child, TaskbarGaRoot);
            if (root == IntPtr.Zero)
            {
                root = child;
            }

            string childClass = GetTaskbarWindowClassName(child);
            string rootClass = GetTaskbarWindowClassName(root);

            bool isTaskList =
                childClass.Equals(
                    "MSTaskListWClass",
                    StringComparison.OrdinalIgnoreCase) ||
                childClass.Contains(
                    "TaskList",
                    StringComparison.OrdinalIgnoreCase) ||
                childClass.Contains(
                    "Taskbar",
                    StringComparison.OrdinalIgnoreCase);

            bool isTaskbarRoot =
                rootClass.Equals(
                    "Shell_TrayWnd",
                    StringComparison.OrdinalIgnoreCase) ||
                rootClass.Equals(
                    "Shell_SecondaryTrayWnd",
                    StringComparison.OrdinalIgnoreCase);

            return isTaskList && isTaskbarRoot;
        }

        private static string GetTaskbarWindowClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return TaskbarGetClassName(hwnd, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private void DisableTaskbarFix(string context, Exception exception)
        {
            _taskbarFixDisabled = true;
            ClearTaskbarClickCandidate();

            SimpleLogHelper.DebugWarning($"Taskbar final fix disabled: {context}");
            SimpleLogHelper.Warning(exception);
        }

        private void TaskbarFixOnClosed(object? sender, EventArgs e)
        {
            ClearTaskbarClickCandidate();

            try
            {
                if (_taskbarFixTimer != null)
                {
                    _taskbarFixTimer.Tick -= TaskbarFixTimerOnTick;
                    _taskbarFixTimer = null;
                }

                if (_taskbarFixHwndSource != null)
                {
                    _taskbarFixHwndSource.RemoveHook(TaskbarFixWndProc);
                    _taskbarFixHwndSource = null;
                }

                // Microsoft documents clearing window properties before the HWND is
                // destroyed. VT_EMPTY removes the window-level AppUserModelID.
                if (_taskbarFixHwnd != IntPtr.Zero)
                {
                    TrySetWindowAppUserModelId(_taskbarFixHwnd, null);
                }
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
            }
            finally
            {
                Closed -= TaskbarFixOnClosed;
                _taskbarFixHwnd = IntPtr.Zero;
            }
        }

        private static bool TrySetWindowAppUserModelId(
            IntPtr hwnd,
            string? appUserModelId)
        {
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            IPropertyStore? propertyStore = null;
            PropVariant value = appUserModelId == null
                ? PropVariant.Empty()
                : PropVariant.FromString(appUserModelId);

            try
            {
                Guid propertyStoreInterfaceId =
                    typeof(IPropertyStore).GUID;

                int getStoreResult = TaskbarGetPropertyStoreForWindow(
                    hwnd,
                    ref propertyStoreInterfaceId,
                    out propertyStore);

                if (getStoreResult < 0 || propertyStore == null)
                {
                    return false;
                }

                PropertyKey key = AppUserModelIdKey;
                int setResult = propertyStore.SetValue(ref key, ref value);
                return setResult >= 0;
            }
            catch (Exception ex)
            {
                SimpleLogHelper.DebugWarning(
                    $"Unable to set taskbar AppUserModelID on hwnd " +
                    $"0x{hwnd.ToInt64():X}");
                SimpleLogHelper.Warning(ex);
                return false;
            }
            finally
            {
                value.Dispose();

                if (propertyStore != null &&
                    Marshal.IsComObject(propertyStore))
                {
                    try
                    {
                        Marshal.FinalReleaseComObject(propertyStore);
                    }
                    catch
                    {
                        // Best-effort COM cleanup only.
                    }
                }
            }
        }

        private static int TaskbarLowWord(IntPtr value) =>
            unchecked((ushort)(value.ToInt64() & 0xFFFFL));

        private static int TaskbarHighWord(IntPtr value) =>
            unchecked((ushort)((value.ToInt64() >> 16) & 0xFFFFL));

        [StructLayout(LayoutKind.Sequential)]
        private struct TaskbarPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid FormatId;
            public uint PropertyId;

            public PropertyKey(Guid formatId, uint propertyId)
            {
                FormatId = formatId;
                PropertyId = propertyId;
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct PropVariant : IDisposable
        {
            private const ushort VtEmpty = 0;
            private const ushort VtLpwstr = 31;

            [FieldOffset(0)]
            private ushort _variantType;

            [FieldOffset(8)]
            private IntPtr _pointerValue;

            public static PropVariant Empty() =>
                new PropVariant
                {
                    _variantType = VtEmpty,
                    _pointerValue = IntPtr.Zero,
                };

            public static PropVariant FromString(string value) =>
                new PropVariant
                {
                    _variantType = VtLpwstr,
                    _pointerValue = Marshal.StringToCoTaskMemUni(value),
                };

            public void Dispose()
            {
                TaskbarPropVariantClear(ref this);
            }
        }

        [ComImport]
        [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            [PreserveSig]
            int GetCount(out uint propertyCount);

            [PreserveSig]
            int GetAt(uint propertyIndex, out PropertyKey key);

            [PreserveSig]
            int GetValue(ref PropertyKey key, out PropVariant value);

            [PreserveSig]
            int SetValue(ref PropertyKey key, ref PropVariant value);

            [PreserveSig]
            int Commit();
        }

        [DllImport(
            "user32.dll",
            EntryPoint = "RegisterWindowMessageW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern int TaskbarRegisterWindowMessage(string message);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetCursorPos",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TaskbarGetCursorPos(out TaskbarPoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "WindowFromPoint",
            ExactSpelling = true)]
        private static extern IntPtr TaskbarWindowFromPoint(TaskbarPoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetAncestor",
            ExactSpelling = true)]
        private static extern IntPtr TaskbarGetAncestor(IntPtr hwnd, uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern int TaskbarGetClassName(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowVisible",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TaskbarIsWindowVisible(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsIconic",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TaskbarIsIconic(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsZoomed",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TaskbarIsZoomed(IntPtr hwnd);

        [DllImport(
            "shell32.dll",
            EntryPoint = "SHGetPropertyStoreForWindow",
            ExactSpelling = true)]
        [PreserveSig]
        private static extern int TaskbarGetPropertyStoreForWindow(
            IntPtr hwnd,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.Interface)] out IPropertyStore propertyStore);

        [DllImport(
            "ole32.dll",
            EntryPoint = "PropVariantClear",
            ExactSpelling = true)]
        [PreserveSig]
        private static extern int TaskbarPropVariantClear(
            ref PropVariant value);
    }
}
