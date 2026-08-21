using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using _1RM.Service;
using Shawn.Utils;

namespace _1RM.View.Host
{
    /// <summary>
    /// Windows 11 25H2 + StartAllBack taskbar compatibility and trace.
    ///
    /// Root-cause guard: 1Remote's official 100 ms protocol-focus callback must
    /// not call SetForegroundWindow(desktop) or FocusOnMe while the pointer is on
    /// the taskbar immediately before a taskbar-button click.
    ///
    /// Fallback guard: if the shell still produces the captured sequence
    /// WA_INACTIVE -> immediate WA_ACTIVE without SC_MINIMIZE/SIZE_MINIMIZED,
    /// complete the same WPF minimize transition as the title-bar button.
    ///
    /// No AppUserModelID, ITaskbarList, owner/style, or ShowInTaskbar mutation.
    /// </summary>
    public partial class TabWindowView
    {
        private const int WmSize = 0x0005;
        private const int WmActivate = 0x0006;
        private const int WmSetFocus = 0x0007;
        private const int WmKillFocus = 0x0008;
        private const int WmShowWindow = 0x0018;
        private const int WmActivateApp = 0x001C;
        private const int WmWindowPosChanged = 0x0047;
        private const int WmStyleChanged = 0x007D;
        private const int WmNcActivate = 0x0086;
        private const int WmSysCommand = 0x0112;

        private const int WaInactive = 0;
        private const int WaActive = 1;
        private const int WaClickActive = 2;
        private const int SizeMinimized = 1;
        private const int ScMinimize = 0xF020;
        private const int ScRestore = 0xF120;
        private const uint GaRoot = 2;
        private const int GwlStyle = -16;
        private const int GwlExStyle = -20;
        private const uint GwOwner = 4;

        private const int CompensationDelayMs = 220;
        private const int ReactivationWindowMs = 750;
        private const int FocusSuppressionMs = 1000;

        private static readonly int TaskbarCreatedMessage =
            NativeRegisterWindowMessage("TaskbarCreated");

        private readonly object _taskbarTraceLock = new object();
        private StreamWriter? _taskbarTraceWriter;
        private string _taskbarTracePath = string.Empty;
        private HwndSource? _taskbarTraceSource;
        private DispatcherTimer? _taskbarCompensationTimer;
        private IntPtr _taskbarTraceHwnd;
        private bool _taskbarTraceDisabled;

        private bool _taskbarCandidateArmed;
        private bool _taskbarCandidateReactivated;
        private DateTime _taskbarCandidateStartedUtc = DateTime.MinValue;
        private long _taskbarCandidateId;
        private string _taskbarCandidateHit = string.Empty;

        // Shared with the replacement for the official 100 ms focus callback.
        private long _taskbarFocusSuppressedUntilUtcTicks;
        private long _taskbarLastFocusBlockLogUtcTicks;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarTraceHwnd = new WindowInteropHelper(this).Handle;
            _myHandle = _taskbarTraceHwnd;
            InitializeTraceWriter();

            try
            {
                _taskbarTraceSource = HwndSource.FromHwnd(_taskbarTraceHwnd);
                _taskbarTraceSource?.AddHook(TaskbarTraceWndProc);

                _taskbarCompensationTimer = new DispatcherTimer(
                    DispatcherPriority.Background,
                    Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(CompensationDelayMs),
                };
                _taskbarCompensationTimer.Tick += CompensationTimerOnTick;

                Loaded += OnTaskbarTraceLoaded;
                Activated += OnTaskbarActivated;
                Deactivated += OnTaskbarDeactivated;
                StateChanged += OnTaskbarStateChanged;
                IsVisibleChanged += OnTaskbarVisibilityChanged;
                Closed += OnTaskbarTraceClosed;

                Trace("INIT",
                    $"version=2.1; log={_taskbarTracePath}; " +
                    $"delayMs={CompensationDelayMs}; " +
                    "AppUserModelID=unchanged; ITaskbarList=unused");
            }
            catch (Exception ex)
            {
                DisableGuard("initialization failed", ex);
            }
        }

        private void OnTaskbarTraceLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnTaskbarTraceLoaded;

            try
            {
                // TimerInitOnLoaded() was registered first in the constructor and
                // has already attached the official callback at this point.
                _timer4CheckForegroundWindow.Stop();
                _timer4CheckForegroundWindow.Elapsed -=
                    Timer4CheckForegroundWindowOnElapsed;
                _timer4CheckForegroundWindow.Elapsed -= SafeFocusTimerOnElapsed;
                _timer4CheckForegroundWindow.Elapsed += SafeFocusTimerOnElapsed;
                _timer4CheckForegroundWindow.Start();
                Trace("FOCUS_TIMER_REPLACED",
                    "official 100 ms callback replaced by taskbar-safe wrapper");
            }
            catch (Exception ex)
            {
                DisableGuard("focus timer replacement failed", ex);
            }
        }

        private void SafeFocusTimerOnElapsed(
            object? sender,
            System.Timers.ElapsedEventArgs e)
        {
            _timer4CheckForegroundWindow.Stop();
            try
            {
                if (DateTime.UtcNow.Ticks <
                    Interlocked.Read(ref _taskbarFocusSuppressedUntilUtcTicks))
                {
                    return;
                }

                if (ShouldBlockProtocolFocusCorrectionAtTaskbar())
                {
                    return;
                }

                RunForRdpV2();
                RunForIntegrate();
            }
            catch (Exception ex)
            {
                SimpleLogHelper.Warning(ex);
                Trace("FOCUS_TIMER_ERROR", ex.ToString(), snapshot: false);
            }
            finally
            {
                try
                {
                    _timer4CheckForegroundWindow.Start();
                }
                catch
                {
                    // Window may already be closing and the timer disposed.
                }
            }
        }

        private bool ShouldBlockProtocolFocusCorrectionAtTaskbar()
        {
            TaskbarHit hit = CaptureTaskbarHit();
            if (!hit.IsTaskbar)
            {
                return false;
            }

            long now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(
                ref _taskbarFocusSuppressedUntilUtcTicks,
                DateTime.UtcNow.AddMilliseconds(350).Ticks);

            long previous = Interlocked.Read(ref _taskbarLastFocusBlockLogUtcTicks);
            if (now - previous >= TimeSpan.FromMilliseconds(500).Ticks &&
                Interlocked.CompareExchange(
                    ref _taskbarLastFocusBlockLogUtcTicks,
                    now,
                    previous) == previous)
            {
                Trace("FOCUS_CORRECTION_BLOCKED",
                    "cursor is on taskbar; skipped protocol focus mutation; " +
                    hit.Describe(),
                    snapshot: false);
            }

            return true;
        }

        private IntPtr TaskbarTraceWndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarTraceDisabled)
            {
                return IntPtr.Zero;
            }

            try
            {
                string? name = MessageName(msg);
                if (name != null)
                {
                    Trace("MSG " + name, DescribeMessage(msg, wParam, lParam));
                }

                if (TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage)
                {
                    CancelCandidate("TaskbarCreated", true);
                    return IntPtr.Zero;
                }

                if (msg == WmActivate)
                {
                    int state = LowWord(wParam);
                    bool minimized = HighWord(wParam) != 0;
                    if (state == WaInactive)
                    {
                        TryArmCandidate(hwnd, minimized, lParam);
                    }
                    else if ((state == WaActive || state == WaClickActive) &&
                             _taskbarCandidateArmed)
                    {
                        double elapsed =
                            (DateTime.UtcNow - _taskbarCandidateStartedUtc)
                            .TotalMilliseconds;
                        if (elapsed <= ReactivationWindowMs &&
                            !minimized &&
                            !NativeIsIconic(hwnd))
                        {
                            _taskbarCandidateReactivated = true;
                            Trace("REACTIVATED",
                                $"candidate={_taskbarCandidateId}; " +
                                $"elapsedMs={elapsed:F1}; other=0x{lParam.ToInt64():X}");
                        }
                    }
                }
                else if (msg == WmSysCommand)
                {
                    int command = unchecked((int)(wParam.ToInt64() & 0xFFF0L));
                    if (command == ScMinimize)
                    {
                        CancelCandidate("native SC_MINIMIZE", true);
                    }
                    else if (command == ScRestore)
                    {
                        CancelCandidate("native SC_RESTORE", false);
                    }
                }
                else if (msg == WmSize &&
                         unchecked((int)wParam.ToInt64()) == SizeMinimized)
                {
                    CancelCandidate("native SIZE_MINIMIZED", true);
                }
            }
            catch (Exception ex)
            {
                DisableGuard($"WndProc failed: 0x{msg:X4}", ex);
            }

            // Never consume or rewrite a native message.
            return IntPtr.Zero;
        }

        private void TryArmCandidate(IntPtr hwnd, bool minimizedFlag, IntPtr other)
        {
            TaskbarHit hit = CaptureTaskbarHit();
            bool nativeVisible = hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);
            bool iconic = hwnd != IntPtr.Zero && NativeIsIconic(hwnd);
            bool eligible =
                _taskbarCompensationTimer != null &&
                hwnd != IntPtr.Zero &&
                IsLoaded &&
                IsVisible &&
                WindowState != WindowState.Minimized &&
                nativeVisible &&
                !iconic &&
                !minimizedFlag &&
                hit.IsTaskbar;

            Trace(eligible ? "INACTIVE_ELIGIBLE" : "INACTIVE_REJECTED",
                $"other=0x{other.ToInt64():X}; minimizedFlag={minimizedFlag}; " +
                $"loaded={IsLoaded}; visible={IsVisible}; " +
                $"nativeVisible={nativeVisible}; iconic={iconic}; " +
                $"managedState={WindowState}; {hit.Describe()}");

            if (!eligible)
            {
                CancelCandidate("ineligible WA_INACTIVE", false);
                return;
            }

            CancelCandidate("replaced by newer candidate", false);
            _taskbarCandidateId++;
            _taskbarCandidateArmed = true;
            _taskbarCandidateReactivated = false;
            _taskbarCandidateStartedUtc = DateTime.UtcNow;
            _taskbarCandidateHit = hit.Describe();

            Interlocked.Exchange(
                ref _taskbarFocusSuppressedUntilUtcTicks,
                DateTime.UtcNow.AddMilliseconds(FocusSuppressionMs).Ticks);

            _taskbarCompensationTimer!.Stop();
            _taskbarCompensationTimer.Interval =
                TimeSpan.FromMilliseconds(CompensationDelayMs);
            _taskbarCompensationTimer.Start();

            Trace("ARM",
                $"candidate={_taskbarCandidateId}; other=0x{other.ToInt64():X}; " +
                _taskbarCandidateHit);
        }

        private void CompensationTimerOnTick(object? sender, EventArgs e)
        {
            _taskbarCompensationTimer?.Stop();
            if (_taskbarTraceDisabled || !_taskbarCandidateArmed)
            {
                return;
            }

            long id = _taskbarCandidateId;
            bool reactivated = _taskbarCandidateReactivated;
            double elapsed =
                (DateTime.UtcNow - _taskbarCandidateStartedUtc).TotalMilliseconds;

            try
            {
                bool nativeVisible =
                    _taskbarTraceHwnd != IntPtr.Zero &&
                    NativeIsWindowVisible(_taskbarTraceHwnd);
                bool iconic =
                    _taskbarTraceHwnd != IntPtr.Zero &&
                    NativeIsIconic(_taskbarTraceHwnd);
                bool shouldCompensate =
                    reactivated &&
                    elapsed >= CompensationDelayMs - 60 &&
                    elapsed < 1500 &&
                    IsLoaded &&
                    IsVisible &&
                    WindowState != WindowState.Minimized &&
                    nativeVisible &&
                    !iconic;

                Trace("EVALUATE",
                    $"candidate={id}; elapsedMs={elapsed:F1}; " +
                    $"reactivated={reactivated}; nativeVisible={nativeVisible}; " +
                    $"iconic={iconic}; managedState={WindowState}; " +
                    $"shouldCompensate={shouldCompensate}; " +
                    $"initialHit={_taskbarCandidateHit}");

                CancelCandidate("evaluation complete", false);
                if (!shouldCompensate)
                {
                    return;
                }

                Trace("COMPENSATE_BEGIN",
                    $"candidate={id}; applying WindowState=Minimized");
                WindowState = WindowState.Minimized;

                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => Trace("COMPENSATE_RESULT",
                        $"candidate={id}; managedState={WindowState}")));
            }
            catch (Exception ex)
            {
                DisableGuard($"candidate {id} evaluation failed", ex);
            }
        }

        private void OnTaskbarActivated(object? sender, EventArgs e) =>
            Trace("EVENT Activated", string.Empty);

        private void OnTaskbarDeactivated(object? sender, EventArgs e) =>
            Trace("EVENT Deactivated", string.Empty);

        private void OnTaskbarStateChanged(object? sender, EventArgs e)
        {
            Trace("EVENT StateChanged", $"newManagedState={WindowState}");
            if (WindowState == WindowState.Minimized)
            {
                CancelCandidate("managed state became Minimized", true);
            }
        }

        private void OnTaskbarVisibilityChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            Trace("EVENT IsVisibleChanged", $"old={e.OldValue}; new={e.NewValue}");
            if (!IsVisible)
            {
                CancelCandidate("window became invisible", true);
            }
        }

        private void CancelCandidate(string reason, bool log)
        {
            if (!_taskbarCandidateArmed)
            {
                return;
            }

            if (log)
            {
                Trace("CANCEL",
                    $"candidate={_taskbarCandidateId}; reason={reason}; " +
                    $"reactivated={_taskbarCandidateReactivated}");
            }

            try
            {
                _taskbarCompensationTimer?.Stop();
            }
            catch
            {
            }

            _taskbarCandidateArmed = false;
            _taskbarCandidateReactivated = false;
            _taskbarCandidateStartedUtc = DateTime.MinValue;
            _taskbarCandidateHit = string.Empty;
        }

        private void DisableGuard(string context, Exception ex)
        {
            Trace("DISABLED",
                $"context={context}; exception={ex.GetType().FullName}: {ex.Message}",
                snapshot: false);
            _taskbarTraceDisabled = true;
            CancelCandidate(context, false);
        }

        private void OnTaskbarTraceClosed(object? sender, EventArgs e)
        {
            try
            {
                CancelCandidate("closed", true);
                Trace("CLOSED", "trace detached");

                Loaded -= OnTaskbarTraceLoaded;
                _timer4CheckForegroundWindow.Elapsed -= SafeFocusTimerOnElapsed;

                if (_taskbarCompensationTimer != null)
                {
                    _taskbarCompensationTimer.Tick -= CompensationTimerOnTick;
                    _taskbarCompensationTimer.Stop();
                    _taskbarCompensationTimer = null;
                }

                if (_taskbarTraceSource != null)
                {
                    _taskbarTraceSource.RemoveHook(TaskbarTraceWndProc);
                    _taskbarTraceSource = null;
                }

                Activated -= OnTaskbarActivated;
                Deactivated -= OnTaskbarDeactivated;
                StateChanged -= OnTaskbarStateChanged;
                IsVisibleChanged -= OnTaskbarVisibilityChanged;
                Closed -= OnTaskbarTraceClosed;
            }
            catch (Exception ex)
            {
                Trace("CLOSE_ERROR", ex.ToString(), snapshot: false);
            }
            finally
            {
                lock (_taskbarTraceLock)
                {
                    try
                    {
                        _taskbarTraceWriter?.Flush();
                        _taskbarTraceWriter?.Dispose();
                    }
                    catch
                    {
                    }
                    _taskbarTraceWriter = null;
                }
            }
        }

        private void InitializeTraceWriter()
        {
            string name =
                $"TaskbarTraceV2-{Environment.ProcessId}-" +
                $"{DateTime.Now:yyyyMMdd-HHmmss}.log";

            try
            {
                string directory = Path.Combine(
                    AppPathHelper.Instance.BaseDirPathForLocality,
                    ".logs");
                Directory.CreateDirectory(directory);
                _taskbarTracePath = Path.Combine(directory, name);
                OpenWriter(_taskbarTracePath);
            }
            catch
            {
                try
                {
                    string directory = Path.Combine(
                        Path.GetTempPath(),
                        "1Remote-TaskbarTraceV2");
                    Directory.CreateDirectory(directory);
                    _taskbarTracePath = Path.Combine(directory, name);
                    OpenWriter(_taskbarTracePath);
                }
                catch
                {
                    _taskbarTracePath = string.Empty;
                    _taskbarTraceWriter = null;
                }
            }
        }

        private void OpenWriter(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.WriteThrough);
            _taskbarTraceWriter = new StreamWriter(
                stream,
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        private void Trace(string eventName, string details, bool snapshot = true)
        {
            StreamWriter? writer = _taskbarTraceWriter;
            if (writer == null)
            {
                return;
            }

            string state = string.Empty;
            if (snapshot)
            {
                try
                {
                    state = "; " + CaptureState();
                }
                catch (Exception ex)
                {
                    state = $"; snapshotError={ex.GetType().Name}: {ex.Message}";
                }
            }

            string line =
                $"time={DateTime.Now:O}; event={eventName}; {details}; " +
                $"thread={Environment.CurrentManagedThreadId}" + state;
            try
            {
                lock (_taskbarTraceLock)
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
            }
            catch
            {
            }
        }

        private string CaptureState()
        {
            IntPtr hwnd = _taskbarTraceHwnd;
            IntPtr foreground = NativeGetForegroundWindow();
            IntPtr owner = hwnd == IntPtr.Zero
                ? IntPtr.Zero
                : NativeGetWindow(hwnd, GwOwner);
            long style = hwnd == IntPtr.Zero
                ? 0
                : GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
            long exStyle = hwnd == IntPtr.Zero
                ? 0
                : GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
            TaskbarHit hit = CaptureTaskbarHit();

            return
                $"hwnd=0x{hwnd.ToInt64():X}; managedState={WindowState}; " +
                $"active={IsActive}; visible={IsVisible}; " +
                $"showInTaskbar={ShowInTaskbar}; " +
                $"nativeVisible={(hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd))}; " +
                $"iconic={(hwnd != IntPtr.Zero && NativeIsIconic(hwnd))}; " +
                $"zoomed={(hwnd != IntPtr.Zero && NativeIsZoomed(hwnd))}; " +
                $"owner=0x{owner.ToInt64():X}; style=0x{unchecked((ulong)style):X}; " +
                $"exStyle=0x{unchecked((ulong)exStyle):X}; " +
                $"foreground=0x{foreground.ToInt64():X}[{ClassName(foreground)}]; " +
                $"candidateArmed={_taskbarCandidateArmed}; " +
                $"candidateReactivated={_taskbarCandidateReactivated}; " +
                hit.Describe();
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

            string childClass = ClassName(child);
            string rootClass = ClassName(root);
            bool taskList = false;
            IntPtr current = child;
            for (int i = 0; i < 16 && current != IntPtr.Zero; i++)
            {
                string value = ClassName(current);
                if (value.Equals("MSTaskListWClass", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("TaskList", StringComparison.OrdinalIgnoreCase))
                {
                    taskList = true;
                    break;
                }
                if (current == root)
                {
                    break;
                }
                current = NativeGetParent(current);
            }

            bool taskbar =
                rootClass.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                rootClass.Equals("Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
                rootClass.Contains("TrayWnd", StringComparison.OrdinalIgnoreCase);

            return new TaskbarHit(
                point,
                child,
                root,
                childClass,
                rootClass,
                taskbar,
                taskList);
        }

        private static string? MessageName(int msg)
        {
            if (TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage)
            {
                return "TaskbarCreated";
            }

            return msg switch
            {
                WmSize => "WM_SIZE",
                WmActivate => "WM_ACTIVATE",
                WmSetFocus => "WM_SETFOCUS",
                WmKillFocus => "WM_KILLFOCUS",
                WmShowWindow => "WM_SHOWWINDOW",
                WmActivateApp => "WM_ACTIVATEAPP",
                WmWindowPosChanged => "WM_WINDOWPOSCHANGED",
                WmStyleChanged => "WM_STYLECHANGED",
                WmNcActivate => "WM_NCACTIVATE",
                WmSysCommand => "WM_SYSCOMMAND",
                _ => null,
            };
        }

        private static string DescribeMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            long wp = wParam.ToInt64();
            long lp = lParam.ToInt64();
            if (msg == WmActivate)
            {
                return $"state={ActivationName(LowWord(wParam))}; " +
                       $"minimized={HighWord(wParam) != 0}; other=0x{lp:X}";
            }
            if (msg == WmSysCommand)
            {
                int command = unchecked((int)(wp & 0xFFF0L));
                return $"command={CommandName(command)}; raw=0x{unchecked((ulong)wp):X}";
            }
            if (msg == WmSize)
            {
                return $"type={SizeName(unchecked((int)wp))}; " +
                       $"width={LowWord(lParam)}; height={HighWord(lParam)}";
            }
            if (msg == WmActivateApp)
            {
                return $"active={wp != 0}; otherThread={unchecked((uint)lp)}";
            }
            return $"wParam=0x{unchecked((ulong)wp):X}; " +
                   $"lParam=0x{unchecked((ulong)lp):X}";
        }

        private static string ActivationName(int value) => value switch
        {
            WaInactive => "WA_INACTIVE",
            WaActive => "WA_ACTIVE",
            WaClickActive => "WA_CLICKACTIVE",
            _ => value.ToString(),
        };

        private static string SizeName(int value) => value switch
        {
            0 => "SIZE_RESTORED",
            1 => "SIZE_MINIMIZED",
            2 => "SIZE_MAXIMIZED",
            3 => "SIZE_MAXSHOW",
            4 => "SIZE_MAXHIDE",
            _ => value.ToString(),
        };

        private static string CommandName(int value) => value switch
        {
            ScMinimize => "SC_MINIMIZE",
            ScRestore => "SC_RESTORE",
            0xF030 => "SC_MAXIMIZE",
            0xF060 => "SC_CLOSE",
            _ => $"0x{value:X4}",
        };

        private static string ClassName(IntPtr hwnd)
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

        private static int LowWord(IntPtr value) =>
            unchecked((ushort)(value.ToInt64() & 0xFFFFL));

        private static int HighWord(IntPtr value) =>
            unchecked((ushort)((value.ToInt64() >> 16) & 0xFFFFL));

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8
                ? NativeGetWindowLongPtr64(hwnd, index)
                : new IntPtr(NativeGetWindowLong32(hwnd, index));

        private readonly struct TaskbarHit
        {
            private readonly NativePoint _point;
            private readonly IntPtr _child;
            private readonly IntPtr _root;
            private readonly string _childClass;
            private readonly string _rootClass;
            public readonly bool IsTaskbar;
            private readonly bool _isTaskList;

            public TaskbarHit(
                NativePoint point,
                IntPtr child,
                IntPtr root,
                string childClass,
                string rootClass,
                bool isTaskbar,
                bool isTaskList)
            {
                _point = point;
                _child = child;
                _root = root;
                _childClass = childClass;
                _rootClass = rootClass;
                IsTaskbar = isTaskbar;
                _isTaskList = isTaskList;
            }

            public string Describe() =>
                $"cursor=({_point.X},{_point.Y}); " +
                $"child=0x{_child.ToInt64():X}[{_childClass}]; " +
                $"root=0x{_root.ToInt64():X}[{_rootClass}]; " +
                $"taskbar={IsTaskbar}; taskList={_isTaskList}";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW",
            CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int NativeRegisterWindowMessage(string value);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos",
            ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeGetCursorPos(out NativePoint point);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint", ExactSpelling = true)]
        private static extern IntPtr NativeWindowFromPoint(NativePoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor", ExactSpelling = true)]
        private static extern IntPtr NativeGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetParent", ExactSpelling = true)]
        private static extern IntPtr NativeGetParent(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int NativeGetClassName(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow", ExactSpelling = true)]
        private static extern IntPtr NativeGetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindow",
            ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr NativeGetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW",
            ExactSpelling = true, SetLastError = true)]
        private static extern IntPtr NativeGetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW",
            ExactSpelling = true, SetLastError = true)]
        private static extern int NativeGetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsIconic(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsZoomed", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsZoomed(IntPtr hwnd);
    }
}
