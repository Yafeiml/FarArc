using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using _1RM.Service;

namespace _1RM.View.Host
{
    /// <summary>
    /// Narrow compatibility guard for the Windows 11 25H2 + StartAllBack failure
    /// captured in TaskbarDiagnostics.
    ///
    /// The failing sequence is:
    ///   active maximized window -> WA_INACTIVE while the cursor is over
    ///   MSTaskListWClass -> immediate WA_ACTIVE -> no SC_MINIMIZE follows.
    ///
    /// Normal taskbar clicks deliver SC_MINIMIZE within roughly 40-60 ms. This guard
    /// waits 220 ms and minimizes only when the exact failed sequence remains true.
    /// It never calls ITaskbarList, ActivateTab, SetForegroundWindow, ShowInTaskbar,
    /// changes window styles/owners, or marks a Win32 message handled.
    /// </summary>
    public partial class TabWindowView
    {
        private const int GuardWmSize = 0x0005;
        private const int GuardWmActivate = 0x0006;
        private const int GuardWmSysCommand = 0x0112;

        private const int GuardWaInactive = 0;
        private const int GuardWaActive = 1;
        private const int GuardWaClickActive = 2;
        private const int GuardSizeMinimized = 1;
        private const int GuardScMinimize = 0xF020;
        private const uint GuardGaRoot = 2;
        private const int GuardDelayMilliseconds = 220;

        private readonly object _taskbarGuardLogLock = new object();
        private HwndSource? _taskbarGuardHwndSource;
        private DispatcherTimer? _taskbarGuardTimer;
        private IntPtr _taskbarGuardHwnd = IntPtr.Zero;
        private bool _taskbarGuardCandidateArmed;
        private bool _taskbarGuardReactivated;
        private DateTime _taskbarGuardCandidateStartedUtc = DateTime.MinValue;
        private long _taskbarGuardCandidateId;
        private string _taskbarGuardChildClass = string.Empty;
        private string _taskbarGuardRootClass = string.Empty;
        private string _taskbarGuardLogPath = string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarGuardHwnd = new WindowInteropHelper(this).Handle;
            _taskbarGuardHwndSource = HwndSource.FromHwnd(_taskbarGuardHwnd);
            _taskbarGuardHwndSource?.AddHook(TaskbarMinimizeGuardWndProc);

            _taskbarGuardTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(GuardDelayMilliseconds),
            };
            _taskbarGuardTimer.Tick += TaskbarMinimizeGuardTimerOnTick;

            Closed += TaskbarMinimizeGuardOnClosed;

            InitializeTaskbarGuardLog();
            TaskbarGuardLog("INIT", "missed-minimize guard attached");
        }

        private IntPtr TaskbarMinimizeGuardWndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            switch (msg)
            {
                case GuardWmActivate:
                {
                    int state = GuardLowWord(wParam);
                    bool minimized = GuardHighWord(wParam) != 0;

                    if (state == GuardWaInactive)
                    {
                        TryArmTaskbarMinimizeGuard(hwnd, minimized, lParam);
                    }
                    else if ((state == GuardWaActive || state == GuardWaClickActive) &&
                             _taskbarGuardCandidateArmed &&
                             !minimized &&
                             !GuardIsIconic(hwnd))
                    {
                        _taskbarGuardReactivated = true;
                        TaskbarGuardLog(
                            "REACTIVATED",
                            $"candidate={_taskbarGuardCandidateId}; other=0x{lParam.ToInt64():X}");
                    }

                    break;
                }

                case GuardWmSysCommand:
                {
                    int command = unchecked((int)(wParam.ToInt64() & 0xFFF0L));
                    if (command == GuardScMinimize)
                    {
                        CancelTaskbarMinimizeGuard("native SC_MINIMIZE received", logCancellation: true);
                    }

                    break;
                }

                case GuardWmSize:
                {
                    int sizeType = unchecked((int)wParam.ToInt64());
                    if (sizeType == GuardSizeMinimized)
                    {
                        CancelTaskbarMinimizeGuard("native SIZE_MINIMIZED received", logCancellation: false);
                    }

                    break;
                }
            }

            // Observation/compensation only. Never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarMinimizeGuard(IntPtr hwnd, bool minimized, IntPtr otherWindow)
        {
            if (minimized ||
                _taskbarGuardTimer == null ||
                hwnd == IntPtr.Zero ||
                !IsLoaded ||
                !IsVisible ||
                !IsActive ||
                WindowState != WindowState.Maximized ||
                !GuardIsWindowVisible(hwnd) ||
                GuardIsIconic(hwnd) ||
                !GuardIsZoomed(hwnd))
            {
                return;
            }

            if (!TryGetTaskbarHit(out string childClass, out string rootClass))
            {
                return;
            }

            _taskbarGuardCandidateId++;
            _taskbarGuardCandidateArmed = true;
            _taskbarGuardReactivated = false;
            _taskbarGuardCandidateStartedUtc = DateTime.UtcNow;
            _taskbarGuardChildClass = childClass;
            _taskbarGuardRootClass = rootClass;

            _taskbarGuardTimer.Stop();
            _taskbarGuardTimer.Interval = TimeSpan.FromMilliseconds(GuardDelayMilliseconds);
            _taskbarGuardTimer.Start();

            TaskbarGuardLog(
                "ARM",
                $"candidate={_taskbarGuardCandidateId}; other=0x{otherWindow.ToInt64():X}; " +
                $"child={childClass}; root={rootClass}");
        }

        private void TaskbarMinimizeGuardTimerOnTick(object? sender, EventArgs e)
        {
            _taskbarGuardTimer?.Stop();

            if (!_taskbarGuardCandidateArmed)
            {
                return;
            }

            long candidateId = _taskbarGuardCandidateId;
            double elapsedMs = (DateTime.UtcNow - _taskbarGuardCandidateStartedUtc).TotalMilliseconds;

            bool shouldCompensate =
                _taskbarGuardReactivated &&
                elapsedMs >= GuardDelayMilliseconds - 30 &&
                elapsedMs < 2000 &&
                _taskbarGuardHwnd != IntPtr.Zero &&
                IsLoaded &&
                IsVisible &&
                IsActive &&
                WindowState == WindowState.Maximized &&
                GuardIsWindowVisible(_taskbarGuardHwnd) &&
                !GuardIsIconic(_taskbarGuardHwnd) &&
                GuardIsZoomed(_taskbarGuardHwnd);

            if (!shouldCompensate)
            {
                TaskbarGuardLog(
                    "SKIP",
                    $"candidate={candidateId}; elapsedMs={elapsedMs:F1}; " +
                    $"reactivated={_taskbarGuardReactivated}; child={_taskbarGuardChildClass}; " +
                    $"root={_taskbarGuardRootClass}");
                ClearTaskbarMinimizeGuardCandidate();
                return;
            }

            // Clear first so the state transition generated below cannot re-enter the
            // same candidate. Setting WindowState is the same path used by 1Remote's
            // own title-bar minimize button, which was observed to recover the taskbar.
            ClearTaskbarMinimizeGuardCandidate();

            TaskbarGuardLog(
                "COMPENSATE",
                $"candidate={candidateId}; elapsedMs={elapsedMs:F1}; applying WindowState=Minimized");

            try
            {
                WindowState = WindowState.Minimized;
            }
            catch (Exception ex)
            {
                TaskbarGuardLog("ERROR", $"candidate={candidateId}; {ex}");
            }
        }

        private void CancelTaskbarMinimizeGuard(string reason, bool logCancellation)
        {
            if (!_taskbarGuardCandidateArmed)
            {
                return;
            }

            if (logCancellation)
            {
                double elapsedMs = (DateTime.UtcNow - _taskbarGuardCandidateStartedUtc).TotalMilliseconds;
                TaskbarGuardLog(
                    "CANCEL",
                    $"candidate={_taskbarGuardCandidateId}; elapsedMs={elapsedMs:F1}; reason={reason}");
            }

            ClearTaskbarMinimizeGuardCandidate();
        }

        private void ClearTaskbarMinimizeGuardCandidate()
        {
            _taskbarGuardTimer?.Stop();
            _taskbarGuardCandidateArmed = false;
            _taskbarGuardReactivated = false;
            _taskbarGuardCandidateStartedUtc = DateTime.MinValue;
            _taskbarGuardChildClass = string.Empty;
            _taskbarGuardRootClass = string.Empty;
        }

        private bool TryGetTaskbarHit(out string childClass, out string rootClass)
        {
            childClass = string.Empty;
            rootClass = string.Empty;

            if (!GuardGetCursorPos(out GuardPoint point))
            {
                return false;
            }

            IntPtr child = GuardWindowFromPoint(point);
            if (child == IntPtr.Zero)
            {
                return false;
            }

            IntPtr root = GuardGetAncestor(child, GuardGaRoot);
            if (root == IntPtr.Zero)
            {
                root = child;
            }

            childClass = GuardGetClassName(child);
            rootClass = GuardGetClassName(root);

            bool isTaskList =
                childClass.Equals("MSTaskListWClass", StringComparison.OrdinalIgnoreCase) ||
                childClass.IndexOf("TaskList", StringComparison.OrdinalIgnoreCase) >= 0;

            bool isTaskbarRoot =
                rootClass.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                rootClass.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase);

            return isTaskList && isTaskbarRoot;
        }

        private void TaskbarMinimizeGuardOnClosed(object? sender, EventArgs e)
        {
            ClearTaskbarMinimizeGuardCandidate();

            if (_taskbarGuardTimer != null)
            {
                _taskbarGuardTimer.Tick -= TaskbarMinimizeGuardTimerOnTick;
                _taskbarGuardTimer = null;
            }

            if (_taskbarGuardHwndSource != null)
            {
                _taskbarGuardHwndSource.RemoveHook(TaskbarMinimizeGuardWndProc);
                _taskbarGuardHwndSource = null;
            }

            Closed -= TaskbarMinimizeGuardOnClosed;
            TaskbarGuardLog("CLOSED", "guard detached");
        }

        private void InitializeTaskbarGuardLog()
        {
            try
            {
                string logDirectory = Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".logs");
                Directory.CreateDirectory(logDirectory);
                _taskbarGuardLogPath = Path.Combine(
                    logDirectory,
                    $"TaskbarMinimizeGuard-{Process.GetCurrentProcess().Id}.log");

                string header =
                    $"# 1Remote 1.2.1 missed-minimize guard\r\n" +
                    $"# PID={Process.GetCurrentProcess().Id}; " +
                    $"OS={Environment.OSVersion}; Is64Bit={Environment.Is64BitProcess}; " +
                    $"Exe={Environment.ProcessPath}\r\n" +
                    $"# Delay={GuardDelayMilliseconds}ms; only arms on MSTaskListWClass/Shell_TrayWnd.\r\n";

                lock (_taskbarGuardLogLock)
                {
                    File.AppendAllText(_taskbarGuardLogPath, header, Encoding.UTF8);
                }
            }
            catch
            {
                _taskbarGuardLogPath = string.Empty;
            }
        }

        private void TaskbarGuardLog(string eventName, string details)
        {
            if (string.IsNullOrEmpty(_taskbarGuardLogPath))
            {
                return;
            }

            try
            {
                string snapshot =
                    $"time={DateTime.Now:O}; event={eventName}; {details}; " +
                    $"thread={Thread.CurrentThread.ManagedThreadId}; " +
                    $"hwnd=0x{_taskbarGuardHwnd.ToInt64():X}; " +
                    $"managedState={WindowState}; active={IsActive}; visible={IsVisible}; " +
                    $"showInTaskbar={ShowInTaskbar}; " +
                    $"nativeVisible={GuardIsWindowVisible(_taskbarGuardHwnd)}; " +
                    $"iconic={GuardIsIconic(_taskbarGuardHwnd)}; " +
                    $"zoomed={GuardIsZoomed(_taskbarGuardHwnd)}; " +
                    $"foreground=0x{GuardGetForegroundWindow().ToInt64():X}\r\n";

                lock (_taskbarGuardLogLock)
                {
                    File.AppendAllText(_taskbarGuardLogPath, snapshot, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging is best-effort and must never affect the remote session.
            }
        }

        private static int GuardLowWord(IntPtr value)
        {
            return unchecked((int)(value.ToInt64() & 0xFFFFL));
        }

        private static int GuardHighWord(IntPtr value)
        {
            return unchecked((int)((value.ToInt64() >> 16) & 0xFFFFL));
        }

        private static string GuardGetClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(256);
            return GuardGetClassNameNative(hwnd, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GuardPoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardGetCursorPos(out GuardPoint point);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
        private static extern IntPtr GuardWindowFromPoint(GuardPoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor")]
        private static extern IntPtr GuardGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GuardGetClassNameNative(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsIconic(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsZoomed", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr GuardGetForegroundWindow();
    }
}
