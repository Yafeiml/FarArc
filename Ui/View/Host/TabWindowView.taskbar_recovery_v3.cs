using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using _1RM.Service;
using Shawn.Utils;

namespace _1RM.View.Host
{
    /// <summary>
    /// Narrow compatibility recovery for the Windows 11 25H2 + StartAllBack
    /// active-taskbar-button failure captured on the affected machine.
    ///
    /// Normal shell path (left untouched):
    ///   WA_INACTIVE over MSTaskListWClass -> SC_MINIMIZE -> SIZE_MINIMIZED.
    ///
    /// Captured failure path:
    ///   WA_INACTIVE over MSTaskListWClass -> WA_ACTIVE while still non-iconic,
    ///   with no intervening SC_MINIMIZE.
    ///
    /// Only the captured failure path is repaired. After the non-minimized
    /// reactivation, a short grace period permits a late native SC_MINIMIZE.
    /// If none arrives, this class sends the same native SC_MINIMIZE command
    /// that Explorer normally sends. Restore commands are never consumed.
    /// </summary>
    public partial class TabWindowView
    {
        private const string RecoveryVersion = "TaskbarRecoveryV3-reactivation-45ms";

        private const int WmSize = 0x0005;
        private const int WmActivate = 0x0006;
        private const int WmSysCommand = 0x0112;

        private const int WaInactive = 0;
        private const int WaActive = 1;
        private const int WaClickActive = 2;
        private const int SizeMinimized = 1;
        private const int ScMinimize = 0xF020;

        private const uint GaRoot = 2;
        private const int RecoveryDelayAfterReactivationMilliseconds = 45;
        private const int CandidateLifetimeMilliseconds = 700;

        private readonly object _taskbarRecoveryLogLock = new object();

        private HwndSource? _taskbarRecoveryHwndSource;
        private DispatcherTimer? _taskbarRecoveryTimer;
        private StreamWriter? _taskbarRecoveryLogWriter;
        private IntPtr _taskbarRecoveryHwnd = IntPtr.Zero;

        private bool _taskbarRecoveryDisabled;
        private bool _taskbarCandidateArmed;
        private bool _taskbarCandidateReactivated;
        private bool _taskbarSyntheticMinimizeInProgress;
        private DateTime _taskbarCandidateStartedUtc = DateTime.MinValue;
        private long _taskbarCandidateSequence;
        private long _activeTaskbarCandidateId;
        private string _taskbarCandidateInitialHit = string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarRecoveryHwnd = new WindowInteropHelper(this).Handle;
            _myHandle = _taskbarRecoveryHwnd;
            InitializeTaskbarRecoveryLog();

            try
            {
                _taskbarRecoveryHwndSource = HwndSource.FromHwnd(_taskbarRecoveryHwnd);
                _taskbarRecoveryHwndSource?.AddHook(TaskbarRecoveryWndProc);

                _taskbarRecoveryTimer = new DispatcherTimer(
                    DispatcherPriority.Send,
                    Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(
                        RecoveryDelayAfterReactivationMilliseconds),
                };
                _taskbarRecoveryTimer.Tick += TaskbarRecoveryTimerOnTick;

                StateChanged += TaskbarRecoveryOnStateChanged;
                Closed += TaskbarRecoveryOnClosed;

                WriteTaskbarRecoveryLog(
                    "INIT",
                    $"version={RecoveryVersion}; hwnd=0x{_taskbarRecoveryHwnd.ToInt64():X}; " +
                    $"delayMs={RecoveryDelayAfterReactivationMilliseconds}; " +
                    $"candidateLifetimeMs={CandidateLifetimeMilliseconds}; " +
                    "AppUserModelID=unchanged; ITaskbarList=unused; restoreMessages=untouched");
            }
            catch (Exception ex)
            {
                DisableTaskbarRecovery("initialization failed", ex);
            }
        }

        private IntPtr TaskbarRecoveryWndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarRecoveryDisabled)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (msg == WmActivate)
                {
                    int activationState = LowWord(wParam);
                    bool minimizedFlag = HighWord(wParam) != 0;

                    if (activationState == WaInactive)
                    {
                        TryArmTaskbarRecoveryCandidate(
                            hwnd,
                            minimizedFlag,
                            lParam);
                    }
                    else if ((activationState == WaActive ||
                              activationState == WaClickActive) &&
                             _taskbarCandidateArmed)
                    {
                        HandleCandidateReactivation(
                            hwnd,
                            minimizedFlag,
                            lParam);
                    }
                }
                else if (msg == WmSysCommand)
                {
                    int command = unchecked(
                        (int)(wParam.ToInt64() & 0xFFF0L));

                    if (command == ScMinimize)
                    {
                        WriteTaskbarRecoveryLog(
                            _taskbarSyntheticMinimizeInProgress
                                ? "SYNTHETIC_SC_MINIMIZE"
                                : "NATIVE_SC_MINIMIZE",
                            $"candidate={_activeTaskbarCandidateId}; " +
                            $"elapsedMs={CandidateElapsedMilliseconds():F1}");

                        CancelTaskbarRecoveryCandidate("SC_MINIMIZE received");
                    }
                }
                else if (msg == WmSize &&
                         unchecked((int)wParam.ToInt64()) == SizeMinimized)
                {
                    WriteTaskbarRecoveryLog(
                        "SIZE_MINIMIZED",
                        $"candidate={_activeTaskbarCandidateId}");
                    CancelTaskbarRecoveryCandidate("SIZE_MINIMIZED received");
                }
            }
            catch (Exception ex)
            {
                DisableTaskbarRecovery(
                    $"WndProc failed for message 0x{msg:X4}",
                    ex);
            }

            // This recovery observes activation/minimize messages and may send a
            // missing minimize command. It never consumes a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarRecoveryCandidate(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            TaskbarHit hit = CaptureTaskbarHit();
            bool nativeVisible = hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);
            bool iconic = hwnd != IntPtr.Zero && NativeIsIconic(hwnd);

            bool eligible =
                _taskbarRecoveryTimer != null &&
                hwnd != IntPtr.Zero &&
                otherWindow == IntPtr.Zero &&
                !minimizedFlag &&
                IsLoaded &&
                IsVisible &&
                IsActive &&
                ShowInTaskbar &&
                WindowState != WindowState.Minimized &&
                nativeVisible &&
                !iconic &&
                hit.IsTaskList;

            if (!eligible)
            {
                return;
            }

            CancelTaskbarRecoveryCandidate("replaced by newer taskbar candidate");

            _activeTaskbarCandidateId = ++_taskbarCandidateSequence;
            _taskbarCandidateArmed = true;
            _taskbarCandidateReactivated = false;
            _taskbarCandidateStartedUtc = DateTime.UtcNow;
            _taskbarCandidateInitialHit = hit.Describe();

            WriteTaskbarRecoveryLog(
                "ARM",
                $"candidate={_activeTaskbarCandidateId}; " +
                $"other=0x{otherWindow.ToInt64():X}; {_taskbarCandidateInitialHit}");
        }

        private void HandleCandidateReactivation(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            double elapsedMilliseconds = CandidateElapsedMilliseconds();

            if (elapsedMilliseconds < 0 ||
                elapsedMilliseconds > CandidateLifetimeMilliseconds ||
                minimizedFlag ||
                hwnd == IntPtr.Zero ||
                NativeIsIconic(hwnd) ||
                WindowState == WindowState.Minimized)
            {
                CancelTaskbarRecoveryCandidate("reactivation was not a missed minimize");
                return;
            }

            _taskbarCandidateReactivated = true;

            WriteTaskbarRecoveryLog(
                "REACTIVATED_NON_MINIMIZED",
                $"candidate={_activeTaskbarCandidateId}; " +
                $"elapsedMs={elapsedMilliseconds:F1}; " +
                $"other=0x{otherWindow.ToInt64():X}");

            // A normal taskbar click has delivered SC_MINIMIZE in roughly
            // 55-80 ms on the affected machine. The captured failure instead
            // reactivated the window at about 60 ms without SC_MINIMIZE.
            // Keep a small grace period for a late native command, then recover.
            _taskbarRecoveryTimer!.Stop();
            _taskbarRecoveryTimer.Interval = TimeSpan.FromMilliseconds(
                RecoveryDelayAfterReactivationMilliseconds);
            _taskbarRecoveryTimer.Start();
        }

        private void TaskbarRecoveryTimerOnTick(object? sender, EventArgs e)
        {
            _taskbarRecoveryTimer?.Stop();

            if (_taskbarRecoveryDisabled ||
                !_taskbarCandidateArmed ||
                !_taskbarCandidateReactivated)
            {
                return;
            }

            long candidateId = _activeTaskbarCandidateId;
            double elapsedMilliseconds = CandidateElapsedMilliseconds();

            try
            {
                IntPtr hwnd = _taskbarRecoveryHwnd;
                IntPtr foreground = NativeGetForegroundWindow();
                bool nativeVisible = hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);
                bool iconic = hwnd != IntPtr.Zero && NativeIsIconic(hwnd);

                bool shouldRecover =
                    hwnd != IntPtr.Zero &&
                    elapsedMilliseconds >= 0 &&
                    elapsedMilliseconds <= CandidateLifetimeMilliseconds &&
                    IsLoaded &&
                    IsVisible &&
                    IsActive &&
                    ShowInTaskbar &&
                    WindowState != WindowState.Minimized &&
                    nativeVisible &&
                    !iconic &&
                    foreground == hwnd;

                WriteTaskbarRecoveryLog(
                    "EVALUATE",
                    $"candidate={candidateId}; elapsedMs={elapsedMilliseconds:F1}; " +
                    $"active={IsActive}; managedState={WindowState}; " +
                    $"nativeVisible={nativeVisible}; iconic={iconic}; " +
                    $"foreground=0x{foreground.ToInt64():X}; recover={shouldRecover}; " +
                    $"initialHit={_taskbarCandidateInitialHit}");

                if (!shouldRecover)
                {
                    CancelTaskbarRecoveryCandidate("evaluation rejected recovery");
                    return;
                }

                WriteTaskbarRecoveryLog(
                    "RECOVERY_SEND_SC_MINIMIZE",
                    $"candidate={candidateId}; elapsedMs={elapsedMilliseconds:F1}");

                _taskbarSyntheticMinimizeInProgress = true;
                try
                {
                    NativeSendMessage(
                        hwnd,
                        WmSysCommand,
                        new IntPtr(ScMinimize),
                        IntPtr.Zero);
                }
                finally
                {
                    _taskbarSyntheticMinimizeInProgress = false;
                }

                bool resultIconic = NativeIsIconic(hwnd);
                WriteTaskbarRecoveryLog(
                    "RECOVERY_RESULT",
                    $"candidate={candidateId}; managedState={WindowState}; " +
                    $"iconic={resultIconic}");

                // SendMessage follows the normal native path. This fallback is
                // only for an unexpected host that ignored SC_MINIMIZE.
                if (!resultIconic && WindowState != WindowState.Minimized)
                {
                    WriteTaskbarRecoveryLog(
                        "RECOVERY_WPF_FALLBACK",
                        $"candidate={candidateId}");
                    WindowState = WindowState.Minimized;
                }
            }
            catch (Exception ex)
            {
                DisableTaskbarRecovery(
                    $"candidate {candidateId} recovery failed",
                    ex);
            }
            finally
            {
                CancelTaskbarRecoveryCandidate("recovery callback completed");
            }
        }

        private void TaskbarRecoveryOnStateChanged(object? sender, EventArgs e)
        {
            WriteTaskbarRecoveryLog(
                "STATE_CHANGED",
                $"managedState={WindowState}; iconic=" +
                $"{(_taskbarRecoveryHwnd != IntPtr.Zero && NativeIsIconic(_taskbarRecoveryHwnd))}");

            if (WindowState == WindowState.Minimized)
            {
                CancelTaskbarRecoveryCandidate("managed state became Minimized");
            }
        }

        private void CancelTaskbarRecoveryCandidate(string reason)
        {
            if (!_taskbarCandidateArmed)
            {
                return;
            }

            try
            {
                _taskbarRecoveryTimer?.Stop();
            }
            catch
            {
                // Best-effort cleanup only.
            }

            WriteTaskbarRecoveryLog(
                "CANCEL",
                $"candidate={_activeTaskbarCandidateId}; reason={reason}; " +
                $"reactivated={_taskbarCandidateReactivated}");

            _taskbarCandidateArmed = false;
            _taskbarCandidateReactivated = false;
            _taskbarCandidateStartedUtc = DateTime.MinValue;
            _taskbarCandidateInitialHit = string.Empty;
        }

        private double CandidateElapsedMilliseconds()
        {
            if (_taskbarCandidateStartedUtc == DateTime.MinValue)
            {
                return -1;
            }

            return (DateTime.UtcNow - _taskbarCandidateStartedUtc)
                .TotalMilliseconds;
        }

        private void DisableTaskbarRecovery(string context, Exception exception)
        {
            WriteTaskbarRecoveryLog(
                "DISABLED",
                $"context={context}; exception={exception.GetType().FullName}: " +
                exception.Message);

            _taskbarRecoveryDisabled = true;
            CancelTaskbarRecoveryCandidate(context);
            SimpleLogHelper.Warning(exception);
        }

        private void TaskbarRecoveryOnClosed(object? sender, EventArgs e)
        {
            try
            {
                CancelTaskbarRecoveryCandidate("window closed");

                if (_taskbarRecoveryTimer != null)
                {
                    _taskbarRecoveryTimer.Stop();
                    _taskbarRecoveryTimer.Tick -= TaskbarRecoveryTimerOnTick;
                    _taskbarRecoveryTimer = null;
                }

                if (_taskbarRecoveryHwndSource != null)
                {
                    _taskbarRecoveryHwndSource.RemoveHook(TaskbarRecoveryWndProc);
                    _taskbarRecoveryHwndSource = null;
                }

                StateChanged -= TaskbarRecoveryOnStateChanged;
                Closed -= TaskbarRecoveryOnClosed;
                WriteTaskbarRecoveryLog("CLOSED", "recovery hook detached");
            }
            catch (Exception ex)
            {
                WriteTaskbarRecoveryLog(
                    "CLOSE_ERROR",
                    ex.ToString());
            }
            finally
            {
                lock (_taskbarRecoveryLogLock)
                {
                    try
                    {
                        _taskbarRecoveryLogWriter?.Flush();
                        _taskbarRecoveryLogWriter?.Dispose();
                    }
                    catch
                    {
                        // Logging must never affect application shutdown.
                    }

                    _taskbarRecoveryLogWriter = null;
                }
            }
        }

        private TaskbarHit CaptureTaskbarHit()
        {
            NativeGetCursorPos(out NativePoint point);
            IntPtr child = NativeWindowFromPoint(point);
            IntPtr root = child == IntPtr.Zero
                ? IntPtr.Zero
                : NativeGetAncestor(child, GaRoot);

            if (root == IntPtr.Zero)
            {
                root = child;
            }

            string childClass = GetClassName(child);
            string rootClass = GetClassName(root);
            bool isTaskList = false;

            IntPtr current = child;
            for (int i = 0; i < 16 && current != IntPtr.Zero; i++)
            {
                string currentClass = GetClassName(current);
                if (currentClass.Equals(
                        "MSTaskListWClass",
                        StringComparison.OrdinalIgnoreCase) ||
                    currentClass.Contains(
                        "TaskList",
                        StringComparison.OrdinalIgnoreCase))
                {
                    isTaskList = true;
                    break;
                }

                if (current == root)
                {
                    break;
                }

                current = NativeGetParent(current);
            }

            bool isTaskbarRoot =
                rootClass.Equals(
                    "Shell_TrayWnd",
                    StringComparison.OrdinalIgnoreCase) ||
                rootClass.Equals(
                    "Shell_SecondaryTrayWnd",
                    StringComparison.OrdinalIgnoreCase);

            return new TaskbarHit(
                point,
                child,
                root,
                childClass,
                rootClass,
                isTaskbarRoot && isTaskList);
        }

        private static string GetClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(256);
            return NativeGetClassName(hwnd, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private void InitializeTaskbarRecoveryLog()
        {
            string fileName =
                $"TaskbarRecoveryV3-{Environment.ProcessId}-" +
                $"{DateTime.Now:yyyyMMdd-HHmmss}.log";

            try
            {
                string directory = Path.Combine(
                    AppPathHelper.Instance.BaseDirPathForLocality,
                    ".logs");
                Directory.CreateDirectory(directory);
                OpenTaskbarRecoveryLog(Path.Combine(directory, fileName));
            }
            catch
            {
                try
                {
                    string directory = Path.Combine(
                        Path.GetTempPath(),
                        "1Remote-TaskbarRecoveryV3");
                    Directory.CreateDirectory(directory);
                    OpenTaskbarRecoveryLog(Path.Combine(directory, fileName));
                }
                catch
                {
                    _taskbarRecoveryLogWriter = null;
                }
            }
        }

        private void OpenTaskbarRecoveryLog(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.WriteThrough);

            _taskbarRecoveryLogWriter = new StreamWriter(
                stream,
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        private void WriteTaskbarRecoveryLog(string eventName, string details)
        {
            StreamWriter? writer = _taskbarRecoveryLogWriter;
            if (writer == null)
            {
                return;
            }

            string state = string.Empty;
            try
            {
                IntPtr hwnd = _taskbarRecoveryHwnd;
                state =
                    $"hwnd=0x{hwnd.ToInt64():X}; managedState={WindowState}; " +
                    $"active={IsActive}; visible={IsVisible}; " +
                    $"showInTaskbar={ShowInTaskbar}; " +
                    $"nativeVisible={(hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd))}; " +
                    $"iconic={(hwnd != IntPtr.Zero && NativeIsIconic(hwnd))}; " +
                    $"foreground=0x{NativeGetForegroundWindow().ToInt64():X}";
            }
            catch (Exception ex)
            {
                state = $"snapshotError={ex.GetType().Name}: {ex.Message}";
            }

            string line =
                $"time={DateTime.Now:O}; event={eventName}; {details}; " +
                $"thread={Environment.CurrentManagedThreadId}; {state}";

            try
            {
                lock (_taskbarRecoveryLogLock)
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
            }
            catch
            {
                // Diagnostics must not alter window behaviour.
            }
        }

        private static int LowWord(IntPtr value) =>
            unchecked((ushort)(value.ToInt64() & 0xFFFFL));

        private static int HighWord(IntPtr value) =>
            unchecked((ushort)((value.ToInt64() >> 16) & 0xFFFFL));

        private readonly struct TaskbarHit
        {
            private readonly NativePoint _point;
            private readonly IntPtr _child;
            private readonly IntPtr _root;
            private readonly string _childClass;
            private readonly string _rootClass;

            public TaskbarHit(
                NativePoint point,
                IntPtr child,
                IntPtr root,
                string childClass,
                string rootClass,
                bool isTaskList)
            {
                _point = point;
                _child = child;
                _root = root;
                _childClass = childClass;
                _rootClass = rootClass;
                IsTaskList = isTaskList;
            }

            public bool IsTaskList { get; }

            public string Describe() =>
                $"cursor=({_point.X},{_point.Y}); " +
                $"child=0x{_child.ToInt64():X}[{_childClass}]; " +
                $"root=0x{_root.ToInt64():X}[{_rootClass}]; " +
                $"taskList={IsTaskList}";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport(
            "user32.dll",
            EntryPoint = "GetCursorPos",
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeGetCursorPos(out NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "WindowFromPoint",
            ExactSpelling = true)]
        private static extern IntPtr NativeWindowFromPoint(NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetAncestor",
            ExactSpelling = true)]
        private static extern IntPtr NativeGetAncestor(IntPtr hwnd, uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetParent",
            ExactSpelling = true)]
        private static extern IntPtr NativeGetParent(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern int NativeGetClassName(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowVisible",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsWindowVisible(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsIconic",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsIconic(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetForegroundWindow",
            ExactSpelling = true)]
        private static extern IntPtr NativeGetForegroundWindow();

        [DllImport(
            "user32.dll",
            EntryPoint = "SendMessageW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true)]
        private static extern IntPtr NativeSendMessage(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
