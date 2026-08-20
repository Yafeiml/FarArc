using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using _1RM.Utils.WindowsApi;

namespace _1RM.View.Host
{
    public partial class TabWindowView
    {
        private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        private static readonly TimeSpan MinimumRepairInterval = TimeSpan.FromSeconds(5);

        private const uint TaskbarGaRoot = 2;

        private DispatcherTimer? _taskbarRepairTimer;
        private HwndSource? _taskbarRepairHwndSource;
        private string _taskbarRepairReason = "unknown";
        private bool _taskbarRepairForced;
        private bool _deactivationStartedFromShell;
        private DateTime _lastTaskbarRepairUtc = DateTime.MinValue;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _myHandle = new WindowInteropHelper(this).Handle;
            _taskbarRepairHwndSource = HwndSource.FromHwnd(_myHandle);
            _taskbarRepairHwndSource?.AddHook(TaskbarRepairWndProc);

            _taskbarRepairTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _taskbarRepairTimer.Tick += TaskbarRepairTimerOnTick;

            // Do not repair on every activation or ordinary state change. Repeated
            // ITaskbarList.AddTab/ActivateTab calls in that path interfere with the
            // native "click the active taskbar button to minimize" behaviour.
            Deactivated += TaskbarRepairOnDeactivated;
            StateChanged += TaskbarRepairOnStateChanged;
            IsVisibleChanged += TaskbarRepairOnIsVisibleChanged;
            Closed += TaskbarRepairOnClosed;
        }

        private void TaskbarRepairOnDeactivated(object? sender, EventArgs e)
        {
            // Capture the mouse location synchronously while it is still over the
            // control that caused deactivation. A normal Alt+Tab or click inside
            // another application must never trigger taskbar re-registration.
            _deactivationStartedFromShell = IsShellOwnedWindowAtCursor();
            if (_deactivationStartedFromShell)
            {
                // Give Explorer/StartAllBack ample time to complete the native
                // minimize/restore transaction. If minimization succeeds the timer
                // will observe WindowState.Minimized and do nothing. Repair is only
                // attempted when the window remains visible and the shell itself is
                // still foreground, matching the reported failed transition.
                ScheduleTaskbarRepair("DeactivatedAfterShellClick", 1500, force: false);
            }
        }

        private void TaskbarRepairOnStateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
            {
                _taskbarRepairTimer?.Stop();
                _deactivationStartedFromShell = false;
            }
        }

        private void TaskbarRepairOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
            {
                _taskbarRepairTimer?.Stop();
                _deactivationStartedFromShell = false;
            }
        }

        private void TaskbarRepairOnClosed(object? sender, EventArgs e)
        {
            if (_taskbarRepairTimer != null)
            {
                _taskbarRepairTimer.Stop();
                _taskbarRepairTimer.Tick -= TaskbarRepairTimerOnTick;
                _taskbarRepairTimer = null;
            }

            if (_taskbarRepairHwndSource != null)
            {
                _taskbarRepairHwndSource.RemoveHook(TaskbarRepairWndProc);
                _taskbarRepairHwndSource = null;
            }

            Deactivated -= TaskbarRepairOnDeactivated;
            StateChanged -= TaskbarRepairOnStateChanged;
            IsVisibleChanged -= TaskbarRepairOnIsVisibleChanged;
            Closed -= TaskbarRepairOnClosed;
        }

        private void ScheduleTaskbarRepair(string reason, int delayMilliseconds, bool force)
        {
            if (IsClosing || IsClosed || _taskbarRepairTimer == null)
            {
                return;
            }

            _taskbarRepairReason = reason;
            _taskbarRepairForced = force;
            _taskbarRepairTimer.Stop();
            _taskbarRepairTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, delayMilliseconds));
            _taskbarRepairTimer.Start();
        }

        private void TaskbarRepairTimerOnTick(object? sender, EventArgs e)
        {
            _taskbarRepairTimer?.Stop();

            if (IsClosing || IsClosed || !IsLoaded || !IsVisible ||
                WindowState == WindowState.Minimized || _myHandle == IntPtr.Zero)
            {
                _deactivationStartedFromShell = false;
                return;
            }

            if (!_taskbarRepairForced)
            {
                // A visible, inactive window is repaired only after a shell-originated
                // click that failed to minimize it and left Explorer/the replacement
                // taskbar in the foreground. This avoids touching normal app switches.
                if (!_deactivationStartedFromShell || IsActive || !IsExplorerShellForeground())
                {
                    _deactivationStartedFromShell = false;
                    return;
                }

                if (DateTime.UtcNow - _lastTaskbarRepairUtc < MinimumRepairInterval)
                {
                    _deactivationStartedFromShell = false;
                    return;
                }
            }

            ShowInTaskbar = true;
            if (TaskbarWindowRepair.TryRegister(_myHandle, _taskbarRepairReason))
            {
                _lastTaskbarRepairUtc = DateTime.UtcNow;
            }

            _deactivationStartedFromShell = false;
            _taskbarRepairForced = false;
        }

        private IntPtr TaskbarRepairWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage)
            {
                // Explorer has rebuilt the taskbar, so re-registering once is safe and
                // necessary. AddTab is used without ActivateTab/SetActiveAlt.
                ScheduleTaskbarRepair("TaskbarCreated", 1000, force: true);
            }

            return IntPtr.Zero;
        }

        private static bool IsShellOwnedWindowAtCursor()
        {
            if (!GetCursorPos(out NativePoint point))
            {
                return false;
            }

            IntPtr hwnd = WindowFromPoint(point);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr root = TaskbarGetAncestor(hwnd, TaskbarGaRoot);
            return IsExplorerShellWindow(root != IntPtr.Zero ? root : hwnd);
        }

        private static bool IsExplorerShellForeground()
        {
            IntPtr hwnd = TaskbarGetForegroundWindow();
            return hwnd != IntPtr.Zero && IsExplorerShellWindow(hwnd);
        }

        private static bool IsExplorerShellWindow(IntPtr hwnd)
        {
            string className = GetWindowClassName(hwnd);
            if (className.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("Progman", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("WorkerW", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("StartAllBack", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(unchecked((int)processId));
                return process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string message);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(NativePoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor")]
        private static extern IntPtr TaskbarGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr TaskbarGetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);
    }
}
