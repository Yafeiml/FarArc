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
    /// The guard compensates only for this sequence:
    ///   active maximized window -> WA_INACTIVE over the taskbar -> immediate
    ///   WA_ACTIVE -> no native SC_MINIMIZE follows.
    ///
    /// It never calls ITaskbarList, ActivateTab, SetForegroundWindow, ShowInTaskbar,
    /// changes window styles/owners, or consumes a native message.
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
        private bool _taskbarGuardDisabled;
        private int _taskbarGuardFaultLogged;
        private DateTime _taskbarGuardCandidateStartedUtc = DateTime.MinValue;
        private long _taskbarGuardCandidateId;
        private string _taskbarGuardChildClass = string.Empty;
        private string _taskbarGuardRootClass = string.Empty;
        private string _taskbarGuardLogPath = string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarGuardHwnd = new WindowInteropHelper(this).Handle;
            InitializeTaskbarGuardLog();

            // Validate every native helper before a HwndSource hook is installed.
            // If anything is unavailable, the guard stays disabled and 1Remote keeps
            // running normally instead of allowing an exception to escape WndProc.
            if (!TaskbarGuardNativePreflight(out Exception? preflightError))
            {
                _taskbarGuardDisabled = true;
                TaskbarGuardEmergencyLog(
                    "DISABLED",
                    "native preflight failed",
                    preflightError);
                return;
            }

            try
            {
                _taskbarGuardHwndSource = HwndSource.FromHwnd(_taskbarGuardHwnd);
                _taskbarGuardHwndSource?.AddHook(TaskbarMinimizeGuardWndProc);

                _taskbarGuardTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(GuardDelayMilliseconds),
                };
                _taskbarGuardTimer.Tick += TaskbarMinimizeGuardTimerOnTick;

                Closed += TaskbarMinimizeGuardOnClosed;
                TaskbarGuardLog("INIT", "missed-minimize guard attached");
            }
            catch (Exception ex)
            {
                DisableTaskbarMinimizeGuard("initialization failed", ex);
            }
        }

        private IntPtr TaskbarMinimizeGuardWndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarGuardDisabled)
            {
                return IntPtr.Zero;
            }

            // An exception must never escape an HwndSource hook. The previous test
            // build did exactly that and the application's global exception handler
            // opened one error window per repeated activation message.
            try
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
            }
            catch (Exception ex)
            {
                DisableTaskbarMinimizeGuard($"WndProc failed; msg=0x{msg:X4}", ex);
            }

            // Observation/compensation only. Never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarMinimizeGuard(IntPtr hwnd, bool minimized, IntPtr otherWindow)
        {
            if (_taskbarGuardDisabled ||
                minimized ||
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
            if (_taskbarGuardDisabled)
            {
                _taskbarGuardTimer?.Stop();
                return;
            }

            try
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

                // Clear first so the state transition generated below cannot re-enter
                // the same candidate. This is the same WPF state path used by the
                // application's own title-bar minimize button.
                ClearTaskbarMinimizeGuardCandidate();

                TaskbarGuardLog(
                    "COMPENSATE",
                    $"candidate={candidateId}; elapsedMs={elapsedMs:F1}; applying WindowState=Minimized");

                WindowState = WindowState.Minimized;
            }
            catch (Exception ex)
            {
                DisableTaskbarMinimizeGuard("timer callback failed", ex);
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
            try
            {
                _taskbarGuardTimer?.Stop();
            }
            catch
            {
                // Best-effort cleanup only.
            }

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

            // Use the managed WinForms cursor facade instead of declaring a renamed
            // GetCursorPos P/Invoke. The v3 crash was caused by a missing EntryPoint
            // mapping on that renamed declaration.
            System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
            var point = new GuardPoint
            {
                X = cursor.X,
                Y = cursor.Y,
            };

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

        private bool TaskbarGuardNativePreflight(out Exception? error)
        {
            try
            {
                System.Drawing.Point cursor = System.Windows.Forms.Cursor.Position;
                var point = new GuardPoint { X = cursor.X, Y = cursor.Y };
                IntPtr child = GuardWindowFromPoint(point);
                if (child != IntPtr.Zero)
                {
                    IntPtr root = GuardGetAncestor(child, GuardGaRoot);
                    _ = GuardGetClassName(child);
                    if (root != IntPtr.Zero)
                    {
                        _ = GuardGetClassName(root);
                    }
                }

                _ = GuardIsWindowVisible(_taskbarGuardHwnd);
                _ = GuardIsIconic(_taskbarGuardHwnd);
                _ = GuardIsZoomed(_taskbarGuardHwnd);
                _ = GuardGetForegroundWindow();

                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }

        private void DisableTaskbarMinimizeGuard(string context, Exception? exception)
        {
            _taskbarGuardDisabled = true;
            ClearTaskbarMinimizeGuardCandidate();

            if (Interlocked.Exchange(ref _taskbarGuardFaultLogged, 1) == 0)
            {
                // This logger performs file I/O only. It deliberately avoids all
                // native calls so a failed P/Invoke cannot recursively fault again.
                TaskbarGuardEmergencyLog("DISABLED", context, exception);
            }
        }

        private void TaskbarMinimizeGuardOnClosed(object? sender, EventArgs e)
        {
            ClearTaskbarMinimizeGuardCandidate();

            try
            {
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
            }
            catch (Exception ex)
            {
                TaskbarGuardEmergencyLog("CLEANUP_ERROR", "guard cleanup failed", ex);
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
                    $"# 1Remote 1.2.1 missed-minimize guard v3.1\r\n" +
                    $"# PID={Process.GetCurrentProcess().Id}; " +
                    $"OS={Environment.OSVersion}; Is64Bit={Environment.Is64BitProcess}; " +
                    $"Exe={Environment.ProcessPath}\r\n" +
                    $"# Delay={GuardDelayMilliseconds}ms; fail-closed HwndSource hook.\r\n";

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

            string snapshot;
            try
            {
                snapshot =
                    $"time={DateTime.Now:O}; event={eventName}; {details}; " +
                    $"thread={Thread.CurrentThread.ManagedThreadId}; " +
                    $"hwnd=0x{_taskbarGuardHwnd.ToInt64():X}; " +
                    $"managedState={WindowState}; active={IsActive}; visible={IsVisible}; " +
                    $"showInTaskbar={ShowInTaskbar}; " +
                    $"nativeVisible={GuardIsWindowVisible(_taskbarGuardHwnd)}; " +
                    $"iconic={GuardIsIconic(_taskbarGuardHwnd)}; " +
                    $"zoomed={GuardIsZoomed(_taskbarGuardHwnd)}; " +
                    $"foreground=0x{GuardGetForegroundWindow().ToInt64():X}\r\n";
            }
            catch (Exception ex)
            {
                snapshot =
                    $"time={DateTime.Now:O}; event={eventName}; {details}; " +
                    $"snapshotError={ex.GetType().FullName}: {ex.Message}\r\n";
            }

            QueueTaskbarGuardLogLine(snapshot);
        }

        private void QueueTaskbarGuardLogLine(string line)
        {
            string path = _taskbarGuardLogPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (_taskbarGuardLogLock)
                    {
                        File.AppendAllText(path, line, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Logging is best-effort and must never affect the remote session.
                }
            });
        }

        private void TaskbarGuardEmergencyLog(string eventName, string context, Exception? exception)
        {
            try
            {
                if (string.IsNullOrEmpty(_taskbarGuardLogPath))
                {
                    string logDirectory = Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".logs");
                    Directory.CreateDirectory(logDirectory);
                    _taskbarGuardLogPath = Path.Combine(
                        logDirectory,
                        $"TaskbarMinimizeGuard-{Process.GetCurrentProcess().Id}.log");
                }

                string exceptionText = exception == null
                    ? "none"
                    : $"{exception.GetType().FullName}: {exception.Message}";

                string line =
                    $"time={DateTime.Now:O}; event={eventName}; context={context}; " +
                    $"exception={exceptionText}\r\n";

                lock (_taskbarGuardLogLock)
                {
                    File.AppendAllText(_taskbarGuardLogPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Last-resort diagnostics must never throw.
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

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint", ExactSpelling = true)]
        private static extern IntPtr GuardWindowFromPoint(GuardPoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor", ExactSpelling = true)]
        private static extern IntPtr GuardGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GuardGetClassNameNative(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsIconic(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsZoomed", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GuardIsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", ExactSpelling = true)]
        private static extern IntPtr GuardGetForegroundWindow();
    }
}
