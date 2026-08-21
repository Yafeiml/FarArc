using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using _1RM.Service;
using Shawn.Utils;

namespace _1RM.View.Host
{
    /// <summary>
    /// Early native recovery for the Windows 11 25H2 + StartAllBack
    /// active-taskbar-button minimize failure.
    ///
    /// The affected-machine V5 trace proved that the recovery command works,
    /// but a 700 ms shell-stall threshold leaves a visible taskbar-button gap.
    /// V6 keeps the native foreground state machine and takes over only after:
    ///   1. the active session loses activation over MSTaskListWClass;
    ///   2. no native SC_MINIMIZE arrives in the measured normal envelope;
    ///   3. foreground is still this session or a taskbar/StartAllBack surface;
    ///   4. that classification is stable for multiple samples.
    ///
    /// A foreign top-level window cancels the candidate immediately. V6 does
    /// not consume messages, block restore, mutate AppUserModelID, call
    /// ITaskbarList, or change ownership, styles, or ShowInTaskbar.
    /// </summary>
    public partial class TabWindowView
    {
        private const string RecoveryV6Version =
            "TaskbarRecoveryV6-early-stable-native-takeover";

        private const int V6WmSize = 0x0005;
        private const int V6WmActivate = 0x0006;
        private const int V6WmSysCommand = 0x0112;

        private const int V6WaInactive = 0;
        private const int V6WaActive = 1;
        private const int V6WaClickActive = 2;
        private const int V6SizeMinimized = 1;
        private const int V6ScMinimize = 0xF020;
        private const int V6ScRestore = 0xF120;

        private const int V6VkLeftButton = 0x01;
        private const int V6VkRightButton = 0x02;
        private const int V6VkMiddleButton = 0x04;
        private const uint V6GaRoot = 2;

        // The latest affected-machine log delivered every normal native
        // SC_MINIMIZE within 90.4 ms. Begin observing at 110 ms.
        private const int V6InitialDelayMilliseconds = 110;
        private const int V6PollIntervalMilliseconds = 20;

        // Switching to another 1Remote window was visible by 122-135 ms in the
        // same trace. Require a stable non-foreign classification and wait until
        // 170 ms before taking over. This removes the visible 700 ms gap while
        // preserving time for a genuine foreground switch to cancel.
        private const int V6EarlyTakeoverMilliseconds = 170;
        private const int V6RequiredStableSamples = 3;

        private const int V6CandidateLifetimeMilliseconds = 3000;
        private const int V6RecoveryResultDelayMilliseconds = 220;
        private const int V6LateNativeDiagnosticWindowMilliseconds = 1200;

        private readonly object _taskbarRecoveryV6StateLock = new object();
        private readonly object _taskbarRecoveryV6LogLock = new object();

        private HwndSource? _taskbarRecoveryV6HwndSource;
        private System.Threading.Timer? _taskbarRecoveryV6Timer;
        private StreamWriter? _taskbarRecoveryV6LogWriter;
        private IntPtr _taskbarRecoveryV6Hwnd = IntPtr.Zero;

        private bool _taskbarRecoveryV6Disabled;
        private bool _taskbarRecoveryV6Closed;
        private bool _taskbarRecoveryV6CandidateArmed;
        private bool _taskbarRecoveryV6RecoveryPosted;

        private long _taskbarRecoveryV6Sequence;
        private long _taskbarRecoveryV6CandidateId;
        private long _taskbarRecoveryV6CandidateStartedTimestamp;
        private long _taskbarRecoveryV6RecoveryPostedTimestamp;
        private long _taskbarRecoveryV6LastSyntheticTimestamp;
        private long _taskbarRecoveryV6LastSyntheticCandidateId;

        private int _taskbarRecoveryV6StableSamples;
        private V6ForegroundKind _taskbarRecoveryV6LastForegroundKind;
        private string _taskbarRecoveryV6InitialHit = string.Empty;
        private string _taskbarRecoveryV6RecoveryReason = string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarRecoveryV6Hwnd = new WindowInteropHelper(this).Handle;
            _myHandle = _taskbarRecoveryV6Hwnd;
            InitializeTaskbarRecoveryV6Log();

            try
            {
                if (_taskbarRecoveryV6Hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "TabWindowView HWND was unavailable at SourceInitialized.");
                }

                _taskbarRecoveryV6HwndSource =
                    HwndSource.FromHwnd(_taskbarRecoveryV6Hwnd);
                if (_taskbarRecoveryV6HwndSource == null)
                {
                    throw new InvalidOperationException(
                        "HwndSource.FromHwnd returned null for TabWindowView.");
                }

                _taskbarRecoveryV6HwndSource.AddHook(
                    TaskbarRecoveryV6WndProc);

                _taskbarRecoveryV6Timer = new System.Threading.Timer(
                    TaskbarRecoveryV6TimerCallback,
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                StateChanged += TaskbarRecoveryV6OnStateChanged;
                Closed += TaskbarRecoveryV6OnClosed;

                WriteTaskbarRecoveryV6Log(
                    "V6_INIT",
                    $"version={RecoveryV6Version}; " +
                    $"initialDelayMs={V6InitialDelayMilliseconds}; " +
                    $"pollMs={V6PollIntervalMilliseconds}; " +
                    $"earlyTakeoverMs={V6EarlyTakeoverMilliseconds}; " +
                    $"requiredStableSamples={V6RequiredStableSamples}; " +
                    $"lifetimeMs={V6CandidateLifetimeMilliseconds}; " +
                    "threadPoolTimer=true; StartAllBackSurfaces=recognized; " +
                    "restoreMessages=untouched; AppUserModelID=unchanged; " +
                    "ITaskbarList=unused");
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV6("initialization failed", ex);
            }
        }

        private IntPtr TaskbarRecoveryV6WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarRecoveryV6Disabled)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (msg == V6WmActivate)
                {
                    int activationState = V6LowWord(wParam);
                    bool minimizedFlag = V6HighWord(wParam) != 0;

                    if (activationState == V6WaInactive)
                    {
                        TryArmTaskbarRecoveryV6(
                            hwnd,
                            minimizedFlag,
                            lParam);
                    }
                    else if ((activationState == V6WaActive ||
                              activationState == V6WaClickActive) &&
                             IsTaskbarRecoveryV6CandidateArmed())
                    {
                        WriteTaskbarRecoveryV6Log(
                            "V6_WA_ACTIVE_NONICONIC",
                            $"candidate={GetTaskbarRecoveryV6CandidateId()}; " +
                            $"elapsedMs={TaskbarRecoveryV6ElapsedMilliseconds():F1}; " +
                            $"minimizedFlag={minimizedFlag}; " +
                            $"other=0x{lParam.ToInt64():X}");

                        if (minimizedFlag ||
                            hwnd == IntPtr.Zero ||
                            V6NativeIsIconic(hwnd))
                        {
                            CancelTaskbarRecoveryV6Candidate(
                                "reactivation arrived after minimization");
                        }
                        else
                        {
                            ScheduleTaskbarRecoveryV6Timer(
                                V6PollIntervalMilliseconds);
                        }
                    }
                }
                else if (msg == V6WmSysCommand)
                {
                    int command = unchecked(
                        (int)(wParam.ToInt64() & 0xFFF0L));

                    if (command == V6ScMinimize)
                    {
                        bool synthetic;
                        bool candidateArmed;
                        long candidateId;
                        long lastSyntheticTimestamp;
                        long lastSyntheticCandidateId;

                        lock (_taskbarRecoveryV6StateLock)
                        {
                            candidateArmed =
                                _taskbarRecoveryV6CandidateArmed;
                            synthetic =
                                candidateArmed &&
                                _taskbarRecoveryV6RecoveryPosted;
                            candidateId =
                                _taskbarRecoveryV6CandidateId;
                            lastSyntheticTimestamp =
                                _taskbarRecoveryV6LastSyntheticTimestamp;
                            lastSyntheticCandidateId =
                                _taskbarRecoveryV6LastSyntheticCandidateId;
                        }

                        double sinceSynthetic =
                            V6ElapsedMilliseconds(lastSyntheticTimestamp);
                        string eventName;
                        if (synthetic)
                        {
                            eventName = "V6_SYNTHETIC_SC_MINIMIZE";
                        }
                        else if (!candidateArmed &&
                                 sinceSynthetic >= 0 &&
                                 sinceSynthetic <=
                                     V6LateNativeDiagnosticWindowMilliseconds)
                        {
                            eventName =
                                "V6_NATIVE_SC_MINIMIZE_AFTER_RECOVERY";
                        }
                        else
                        {
                            eventName = "V6_NATIVE_SC_MINIMIZE";
                        }

                        WriteTaskbarRecoveryV6Log(
                            eventName,
                            $"candidate={candidateId}; " +
                            $"elapsedMs={TaskbarRecoveryV6ElapsedMilliseconds():F1}; " +
                            $"lastSyntheticCandidate={lastSyntheticCandidateId}; " +
                            $"sinceSyntheticMs={sinceSynthetic:F1}");

                        CancelTaskbarRecoveryV6Candidate(
                            "SC_MINIMIZE received");
                    }
                    else if (command == V6ScRestore)
                    {
                        bool iconic =
                            hwnd != IntPtr.Zero &&
                            V6NativeIsIconic(hwnd);

                        WriteTaskbarRecoveryV6Log(
                            "V6_SC_RESTORE",
                            $"candidate={GetTaskbarRecoveryV6CandidateId()}; " +
                            $"iconic={iconic}");

                        if (iconic)
                        {
                            CancelTaskbarRecoveryV6Candidate(
                                "legitimate SC_RESTORE received");
                        }
                        else
                        {
                            ScheduleTaskbarRecoveryV6Timer(
                                V6PollIntervalMilliseconds);
                        }
                    }
                }
                else if (msg == V6WmSize &&
                         unchecked((int)wParam.ToInt64()) ==
                         V6SizeMinimized)
                {
                    WriteTaskbarRecoveryV6Log(
                        "V6_SIZE_MINIMIZED",
                        $"candidate={GetTaskbarRecoveryV6CandidateId()}");

                    CancelTaskbarRecoveryV6Candidate(
                        "SIZE_MINIMIZED received");
                }
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV6(
                    $"WndProc failed for message 0x{msg:X4}",
                    ex);
            }

            // Observation/recovery only. Never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarRecoveryV6(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            V6TaskbarHit hit = CaptureTaskbarRecoveryV6Hit();
            bool nativeVisible =
                hwnd != IntPtr.Zero &&
                V6NativeIsWindowVisible(hwnd);
            bool iconic =
                hwnd != IntPtr.Zero &&
                V6NativeIsIconic(hwnd);

            bool leftDown = V6IsKeyDown(V6VkLeftButton);
            bool rightDown = V6IsKeyDown(V6VkRightButton);
            bool middleDown = V6IsKeyDown(V6VkMiddleButton);

            bool baseEligible =
                _taskbarRecoveryV6Timer != null &&
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

            if (!baseEligible)
            {
                return;
            }

            // A right/middle-button taskbar interaction can open a jump list or
            // auxiliary menu and must never be converted into a minimize.
            if (rightDown || middleDown)
            {
                WriteTaskbarRecoveryV6Log(
                    "V6_ARM_REJECTED_MOUSE_BUTTON",
                    $"leftDown={leftDown}; rightDown={rightDown}; " +
                    $"middleDown={middleDown}; {hit.Describe()}");
                return;
            }

            long candidateId;
            lock (_taskbarRecoveryV6StateLock)
            {
                _taskbarRecoveryV6CandidateId =
                    ++_taskbarRecoveryV6Sequence;
                candidateId = _taskbarRecoveryV6CandidateId;
                _taskbarRecoveryV6CandidateArmed = true;
                _taskbarRecoveryV6RecoveryPosted = false;
                _taskbarRecoveryV6CandidateStartedTimestamp =
                    Stopwatch.GetTimestamp();
                _taskbarRecoveryV6RecoveryPostedTimestamp = 0;
                _taskbarRecoveryV6StableSamples = 0;
                _taskbarRecoveryV6LastForegroundKind =
                    V6ForegroundKind.None;
                _taskbarRecoveryV6InitialHit = hit.Describe();
                _taskbarRecoveryV6RecoveryReason = string.Empty;

                _taskbarRecoveryV6Timer?.Change(
                    V6InitialDelayMilliseconds,
                    Timeout.Infinite);
            }

            WriteTaskbarRecoveryV6Log(
                "V6_ARM",
                $"candidate={candidateId}; " +
                $"other=0x{otherWindow.ToInt64():X}; " +
                $"leftDown={leftDown}; rightDown={rightDown}; " +
                $"middleDown={middleDown}; {hit.Describe()}");
        }

        private void TaskbarRecoveryV6TimerCallback(object? state)
        {
            long candidateId;
            bool recoveryPosted;
            long recoveryPostedTimestamp;

            lock (_taskbarRecoveryV6StateLock)
            {
                if (_taskbarRecoveryV6Disabled ||
                    _taskbarRecoveryV6Closed ||
                    !_taskbarRecoveryV6CandidateArmed)
                {
                    return;
                }

                candidateId = _taskbarRecoveryV6CandidateId;
                recoveryPosted =
                    _taskbarRecoveryV6RecoveryPosted;
                recoveryPostedTimestamp =
                    _taskbarRecoveryV6RecoveryPostedTimestamp;
            }

            try
            {
                IntPtr hwnd = _taskbarRecoveryV6Hwnd;
                if (hwnd == IntPtr.Zero ||
                    !V6NativeIsWindow(hwnd) ||
                    !V6NativeIsWindowVisible(hwnd))
                {
                    CancelTaskbarRecoveryV6Candidate(
                        "window is unavailable or invisible");
                    return;
                }

                bool iconic = V6NativeIsIconic(hwnd);
                if (iconic)
                {
                    WriteTaskbarRecoveryV6Log(
                        recoveryPosted
                            ? "V6_RECOVERY_RESULT"
                            : "V6_NATIVE_MINIMIZE_OBSERVED",
                        $"candidate={candidateId}; iconic=True");

                    CancelTaskbarRecoveryV6Candidate(
                        "window became iconic");
                    return;
                }

                if (recoveryPosted)
                {
                    double postedElapsedMilliseconds =
                        V6ElapsedMilliseconds(
                            recoveryPostedTimestamp);

                    if (postedElapsedMilliseconds >=
                        V6RecoveryResultDelayMilliseconds)
                    {
                        WriteTaskbarRecoveryV6Log(
                            "V6_RECOVERY_WPF_FALLBACK",
                            $"candidate={candidateId}; " +
                            $"reason={_taskbarRecoveryV6RecoveryReason}; " +
                            $"postedElapsedMs={postedElapsedMilliseconds:F1}");

                        Dispatcher.BeginInvoke(
                            new Action(() =>
                            {
                                if (!IsClosed &&
                                    WindowState !=
                                        WindowState.Minimized)
                                {
                                    WindowState =
                                        WindowState.Minimized;
                                }
                            }));

                        CancelTaskbarRecoveryV6Candidate(
                            "WPF fallback queued");
                        return;
                    }

                    ScheduleTaskbarRecoveryV6Timer(
                        V6PollIntervalMilliseconds);
                    return;
                }

                double elapsedMilliseconds =
                    TaskbarRecoveryV6ElapsedMilliseconds();
                if (elapsedMilliseconds < 0 ||
                    elapsedMilliseconds >
                        V6CandidateLifetimeMilliseconds)
                {
                    WriteTaskbarRecoveryV6Log(
                        "V6_EXPIRED",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}");

                    CancelTaskbarRecoveryV6Candidate(
                        "candidate lifetime expired");
                    return;
                }

                IntPtr foreground =
                    V6NativeGetForegroundWindow();
                IntPtr foregroundRoot =
                    V6GetRootWindow(foreground);
                string foregroundClass =
                    V6GetClassName(foregroundRoot);
                V6ForegroundKind foregroundKind =
                    ClassifyTaskbarRecoveryV6Foreground(
                        foreground,
                        foregroundRoot,
                        foregroundClass,
                        hwnd);

                int stableSamples;
                V6ForegroundKind previousKind;

                lock (_taskbarRecoveryV6StateLock)
                {
                    if (!_taskbarRecoveryV6CandidateArmed ||
                        _taskbarRecoveryV6CandidateId !=
                            candidateId)
                    {
                        return;
                    }

                    previousKind =
                        _taskbarRecoveryV6LastForegroundKind;

                    if (foregroundKind ==
                            V6ForegroundKind.Self ||
                        foregroundKind ==
                            V6ForegroundKind.TaskbarTransition)
                    {
                        if (foregroundKind == previousKind)
                        {
                            _taskbarRecoveryV6StableSamples++;
                        }
                        else
                        {
                            _taskbarRecoveryV6StableSamples = 1;
                        }
                    }
                    else
                    {
                        _taskbarRecoveryV6StableSamples = 0;
                    }

                    _taskbarRecoveryV6LastForegroundKind =
                        foregroundKind;
                    stableSamples =
                        _taskbarRecoveryV6StableSamples;
                }

                if (foregroundKind == V6ForegroundKind.Foreign)
                {
                    WriteTaskbarRecoveryV6Log(
                        "V6_FOREIGN_FOREGROUND_CANCEL",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"root=0x{foregroundRoot.ToInt64():X}" +
                        $"[{foregroundClass}]");

                    CancelTaskbarRecoveryV6Candidate(
                        "a foreign top-level window became foreground");
                    return;
                }

                if (foregroundKind != previousKind ||
                    stableSamples == 1 ||
                    stableSamples ==
                        V6RequiredStableSamples)
                {
                    WriteTaskbarRecoveryV6Log(
                        "V6_WATCHDOG_SAMPLE",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"kind={foregroundKind}; " +
                        $"stableSamples={stableSamples}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"root=0x{foregroundRoot.ToInt64():X}" +
                        $"[{foregroundClass}]");
                }

                if ((foregroundKind == V6ForegroundKind.Self ||
                     foregroundKind ==
                         V6ForegroundKind.TaskbarTransition) &&
                    stableSamples >=
                        V6RequiredStableSamples &&
                    elapsedMilliseconds >=
                        V6EarlyTakeoverMilliseconds)
                {
                    string reason =
                        foregroundKind ==
                            V6ForegroundKind.Self
                            ? "same-session-hwnd-returned-stably"
                            : "taskbar-transition-stable-beyond-native-envelope";

                    PostTaskbarRecoveryV6Minimize(
                        candidateId,
                        elapsedMilliseconds,
                        reason,
                        hwnd,
                        foreground,
                        foregroundRoot,
                        foregroundClass,
                        stableSamples);
                    return;
                }

                ScheduleTaskbarRecoveryV6Timer(
                    V6PollIntervalMilliseconds);
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV6(
                    $"candidate {candidateId} watchdog failed",
                    ex);
            }
        }

        private void PostTaskbarRecoveryV6Minimize(
            long candidateId,
            double elapsedMilliseconds,
            string recoveryReason,
            IntPtr hwnd,
            IntPtr foreground,
            IntPtr foregroundRoot,
            string foregroundClass,
            int stableSamples)
        {
            long postedTimestamp = Stopwatch.GetTimestamp();

            lock (_taskbarRecoveryV6StateLock)
            {
                if (!_taskbarRecoveryV6CandidateArmed ||
                    _taskbarRecoveryV6CandidateId !=
                        candidateId ||
                    _taskbarRecoveryV6RecoveryPosted)
                {
                    return;
                }

                _taskbarRecoveryV6RecoveryPosted = true;
                _taskbarRecoveryV6RecoveryPostedTimestamp =
                    postedTimestamp;
                _taskbarRecoveryV6LastSyntheticTimestamp =
                    postedTimestamp;
                _taskbarRecoveryV6LastSyntheticCandidateId =
                    candidateId;
                _taskbarRecoveryV6RecoveryReason =
                    recoveryReason;
            }

            WriteTaskbarRecoveryV6Log(
                "V6_RECOVERY_POST_SC_MINIMIZE",
                $"candidate={candidateId}; " +
                $"reason={recoveryReason}; " +
                $"elapsedMs={elapsedMilliseconds:F1}; " +
                $"stableSamples={stableSamples}; " +
                $"foreground=0x{foreground.ToInt64():X}; " +
                $"root=0x{foregroundRoot.ToInt64():X}" +
                $"[{foregroundClass}]; " +
                $"initialHit={_taskbarRecoveryV6InitialHit}");

            bool posted = V6NativePostMessage(
                hwnd,
                V6WmSysCommand,
                new IntPtr(V6ScMinimize),
                IntPtr.Zero);

            WriteTaskbarRecoveryV6Log(
                "V6_RECOVERY_POST_RESULT",
                $"candidate={candidateId}; posted={posted}; " +
                $"win32Error={(posted ? 0 : Marshal.GetLastWin32Error())}");

            if (!posted)
            {
                Dispatcher.BeginInvoke(
                    new Action(() =>
                    {
                        if (!IsClosed &&
                            WindowState != WindowState.Minimized)
                        {
                            WindowState = WindowState.Minimized;
                        }
                    }));

                CancelTaskbarRecoveryV6Candidate(
                    "PostMessage failed; WPF fallback queued");
                return;
            }

            ScheduleTaskbarRecoveryV6Timer(
                V6RecoveryResultDelayMilliseconds);
        }

        private bool IsTaskbarRecoveryV6CandidateArmed()
        {
            lock (_taskbarRecoveryV6StateLock)
            {
                return _taskbarRecoveryV6CandidateArmed;
            }
        }

        private long GetTaskbarRecoveryV6CandidateId()
        {
            lock (_taskbarRecoveryV6StateLock)
            {
                return _taskbarRecoveryV6CandidateId;
            }
        }

        private void ScheduleTaskbarRecoveryV6Timer(
            int dueMilliseconds)
        {
            lock (_taskbarRecoveryV6StateLock)
            {
                if (_taskbarRecoveryV6Disabled ||
                    _taskbarRecoveryV6Closed ||
                    !_taskbarRecoveryV6CandidateArmed)
                {
                    return;
                }

                _taskbarRecoveryV6Timer?.Change(
                    Math.Max(1, dueMilliseconds),
                    Timeout.Infinite);
            }
        }

        private void CancelTaskbarRecoveryV6Candidate(
            string reason)
        {
            long candidateId;
            int stableSamples;
            bool recoveryPosted;
            V6ForegroundKind lastKind;

            lock (_taskbarRecoveryV6StateLock)
            {
                if (!_taskbarRecoveryV6CandidateArmed)
                {
                    return;
                }

                candidateId =
                    _taskbarRecoveryV6CandidateId;
                stableSamples =
                    _taskbarRecoveryV6StableSamples;
                recoveryPosted =
                    _taskbarRecoveryV6RecoveryPosted;
                lastKind =
                    _taskbarRecoveryV6LastForegroundKind;

                try
                {
                    _taskbarRecoveryV6Timer?.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }
                catch
                {
                    // Best-effort cancellation only.
                }

                _taskbarRecoveryV6CandidateArmed = false;
                _taskbarRecoveryV6RecoveryPosted = false;
                _taskbarRecoveryV6CandidateStartedTimestamp = 0;
                _taskbarRecoveryV6RecoveryPostedTimestamp = 0;
                _taskbarRecoveryV6StableSamples = 0;
                _taskbarRecoveryV6LastForegroundKind =
                    V6ForegroundKind.None;
                _taskbarRecoveryV6InitialHit =
                    string.Empty;
                _taskbarRecoveryV6RecoveryReason =
                    string.Empty;
            }

            WriteTaskbarRecoveryV6Log(
                "V6_CANCEL",
                $"candidate={candidateId}; reason={reason}; " +
                $"lastKind={lastKind}; " +
                $"stableSamples={stableSamples}; " +
                $"recoveryPosted={recoveryPosted}");
        }

        private double TaskbarRecoveryV6ElapsedMilliseconds()
        {
            long timestamp;
            lock (_taskbarRecoveryV6StateLock)
            {
                timestamp =
                    _taskbarRecoveryV6CandidateStartedTimestamp;
            }

            return V6ElapsedMilliseconds(timestamp);
        }

        private static double V6ElapsedMilliseconds(
            long timestamp)
        {
            if (timestamp <= 0)
            {
                return -1;
            }

            return (Stopwatch.GetTimestamp() - timestamp) *
                   1000.0 /
                   Stopwatch.Frequency;
        }

        private void TaskbarRecoveryV6OnStateChanged(
            object? sender,
            EventArgs e)
        {
            bool iconic =
                _taskbarRecoveryV6Hwnd != IntPtr.Zero &&
                V6NativeIsIconic(_taskbarRecoveryV6Hwnd);

            WriteTaskbarRecoveryV6Log(
                "V6_STATE_CHANGED",
                $"managedState={WindowState}; iconic={iconic}");

            if (WindowState == WindowState.Minimized ||
                iconic)
            {
                CancelTaskbarRecoveryV6Candidate(
                    "managed/native state became minimized");
            }
        }

        private void DisableTaskbarRecoveryV6(
            string context,
            Exception exception)
        {
            WriteTaskbarRecoveryV6Log(
                "V6_DISABLED",
                $"context={context}; " +
                $"exception={exception.GetType().FullName}: " +
                exception.Message);

            _taskbarRecoveryV6Disabled = true;
            CancelTaskbarRecoveryV6Candidate(context);
            SimpleLogHelper.Warning(exception);
        }

        private void TaskbarRecoveryV6OnClosed(
            object? sender,
            EventArgs e)
        {
            try
            {
                lock (_taskbarRecoveryV6StateLock)
                {
                    _taskbarRecoveryV6Closed = true;
                    _taskbarRecoveryV6CandidateArmed = false;
                    _taskbarRecoveryV6Timer?.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }

                if (_taskbarRecoveryV6HwndSource != null)
                {
                    _taskbarRecoveryV6HwndSource.RemoveHook(
                        TaskbarRecoveryV6WndProc);
                    _taskbarRecoveryV6HwndSource = null;
                }

                StateChanged -=
                    TaskbarRecoveryV6OnStateChanged;
                Closed -= TaskbarRecoveryV6OnClosed;

                _taskbarRecoveryV6Timer?.Dispose();
                _taskbarRecoveryV6Timer = null;

                WriteTaskbarRecoveryV6Log(
                    "V6_CLOSED",
                    "early native state machine detached");
            }
            catch (Exception ex)
            {
                WriteTaskbarRecoveryV6Log(
                    "V6_CLOSE_ERROR",
                    ex.ToString());
            }
            finally
            {
                lock (_taskbarRecoveryV6LogLock)
                {
                    try
                    {
                        _taskbarRecoveryV6LogWriter?.Flush();
                        _taskbarRecoveryV6LogWriter?.Dispose();
                    }
                    catch
                    {
                        // Logging must never affect shutdown.
                    }

                    _taskbarRecoveryV6LogWriter = null;
                }
            }
        }

        private V6TaskbarHit CaptureTaskbarRecoveryV6Hit()
        {
            V6NativeGetCursorPos(out V6NativePoint point);
            IntPtr child = V6NativeWindowFromPoint(point);
            IntPtr root = V6GetRootWindow(child);
            string childClass = V6GetClassName(child);
            string rootClass = V6GetClassName(root);
            bool isTaskList = false;

            IntPtr current = child;
            for (int i = 0;
                 i < 16 && current != IntPtr.Zero;
                 i++)
            {
                string currentClass =
                    V6GetClassName(current);
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

                current = V6NativeGetParent(current);
            }

            bool isTaskbarRoot =
                V6IsTaskbarRootClass(rootClass);

            return new V6TaskbarHit(
                point,
                child,
                root,
                childClass,
                rootClass,
                isTaskList && isTaskbarRoot);
        }

        private static V6ForegroundKind
            ClassifyTaskbarRecoveryV6Foreground(
                IntPtr foreground,
                IntPtr foregroundRoot,
                string foregroundRootClass,
                IntPtr sessionHwnd)
        {
            if (foregroundRoot == sessionHwnd)
            {
                return V6ForegroundKind.Self;
            }

            if (foreground == IntPtr.Zero ||
                foregroundRoot == IntPtr.Zero ||
                V6IsTaskbarRootClass(
                    foregroundRootClass) ||
                V6IsStartAllBackTaskbarSurface(
                    foregroundRootClass))
            {
                return V6ForegroundKind.TaskbarTransition;
            }

            return V6ForegroundKind.Foreign;
        }

        private static bool V6IsTaskbarRootClass(
            string className) =>
            className.Equals(
                "Shell_TrayWnd",
                StringComparison.OrdinalIgnoreCase) ||
            className.Equals(
                "Shell_SecondaryTrayWnd",
                StringComparison.OrdinalIgnoreCase) ||
            className.Contains(
                "TrayWnd",
                StringComparison.OrdinalIgnoreCase) ||
            className.Contains(
                "Taskbar",
                StringComparison.OrdinalIgnoreCase);

        private static bool V6IsStartAllBackTaskbarSurface(
            string className) =>
            className.Equals(
                "SIBJumpView",
                StringComparison.OrdinalIgnoreCase) ||
            className.Equals(
                "SIBFlyout",
                StringComparison.OrdinalIgnoreCase) ||
            className.StartsWith(
                "SIB",
                StringComparison.OrdinalIgnoreCase);

        private static IntPtr V6GetRootWindow(
            IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr root =
                V6NativeGetAncestor(hwnd, V6GaRoot);
            return root == IntPtr.Zero ? hwnd : root;
        }

        private static string V6GetClassName(
            IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(256);
            return V6NativeGetClassName(
                       hwnd,
                       buffer,
                       buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private static bool V6IsKeyDown(int virtualKey) =>
            (V6NativeGetAsyncKeyState(virtualKey) &
             unchecked((short)0x8000)) != 0;

        private void InitializeTaskbarRecoveryV6Log()
        {
            string fileName =
                $"TaskbarRecoveryV6-{Environment.ProcessId}-" +
                $"{DateTime.Now:yyyyMMdd-HHmmss}.log";

            try
            {
                string directory = Path.Combine(
                    AppPathHelper.Instance.BaseDirPathForLocality,
                    ".logs");
                Directory.CreateDirectory(directory);
                OpenTaskbarRecoveryV6Log(
                    Path.Combine(directory, fileName));
            }
            catch
            {
                try
                {
                    string directory = Path.Combine(
                        Path.GetTempPath(),
                        "1Remote-TaskbarRecoveryV6");
                    Directory.CreateDirectory(directory);
                    OpenTaskbarRecoveryV6Log(
                        Path.Combine(directory, fileName));
                }
                catch
                {
                    _taskbarRecoveryV6LogWriter = null;
                }
            }
        }

        private void OpenTaskbarRecoveryV6Log(
            string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.WriteThrough);

            _taskbarRecoveryV6LogWriter =
                new StreamWriter(
                    stream,
                    new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
        }

        private void WriteTaskbarRecoveryV6Log(
            string eventName,
            string details)
        {
            StreamWriter? writer =
                _taskbarRecoveryV6LogWriter;
            if (writer == null)
            {
                return;
            }

            string nativeState;
            try
            {
                IntPtr hwnd = _taskbarRecoveryV6Hwnd;
                IntPtr foreground =
                    V6NativeGetForegroundWindow();
                IntPtr foregroundRoot =
                    V6GetRootWindow(foreground);
                string foregroundClass =
                    V6GetClassName(foregroundRoot);

                nativeState =
                    $"hwnd=0x{hwnd.ToInt64():X}; " +
                    $"nativeVisible=" +
                    $"{(hwnd != IntPtr.Zero && V6NativeIsWindowVisible(hwnd))}; " +
                    $"iconic=" +
                    $"{(hwnd != IntPtr.Zero && V6NativeIsIconic(hwnd))}; " +
                    $"foreground=0x{foreground.ToInt64():X}; " +
                    $"foregroundRoot=0x{foregroundRoot.ToInt64():X}" +
                    $"[{foregroundClass}]";
            }
            catch (Exception ex)
            {
                nativeState =
                    $"snapshotError={ex.GetType().Name}: " +
                    ex.Message;
            }

            string line =
                $"time={DateTime.Now:O}; event={eventName}; " +
                $"{details}; thread=" +
                $"{Environment.CurrentManagedThreadId}; " +
                nativeState;

            try
            {
                lock (_taskbarRecoveryV6LogLock)
                {
                    writer.WriteLine(line);
                    writer.Flush();
                }
            }
            catch
            {
                // Diagnostics must never alter window behaviour.
            }
        }

        private static int V6LowWord(IntPtr value) =>
            unchecked(
                (ushort)(value.ToInt64() & 0xFFFFL));

        private static int V6HighWord(IntPtr value) =>
            unchecked(
                (ushort)((value.ToInt64() >> 16) &
                         0xFFFFL));

        private enum V6ForegroundKind
        {
            None,
            Self,
            TaskbarTransition,
            Foreign,
        }

        private readonly struct V6TaskbarHit
        {
            private readonly V6NativePoint _point;
            private readonly IntPtr _child;
            private readonly IntPtr _root;
            private readonly string _childClass;
            private readonly string _rootClass;

            public V6TaskbarHit(
                V6NativePoint point,
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
                $"child=0x{_child.ToInt64():X}" +
                $"[{_childClass}]; " +
                $"root=0x{_root.ToInt64():X}" +
                $"[{_rootClass}]; " +
                $"taskList={IsTaskList}";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct V6NativePoint
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
        private static extern bool V6NativeGetCursorPos(
            out V6NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "WindowFromPoint",
            ExactSpelling = true)]
        private static extern IntPtr V6NativeWindowFromPoint(
            V6NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetAncestor",
            ExactSpelling = true)]
        private static extern IntPtr V6NativeGetAncestor(
            IntPtr hwnd,
            uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetParent",
            ExactSpelling = true)]
        private static extern IntPtr V6NativeGetParent(
            IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern int V6NativeGetClassName(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindow",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V6NativeIsWindow(
            IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowVisible",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V6NativeIsWindowVisible(
            IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsIconic",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V6NativeIsIconic(
            IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetForegroundWindow",
            ExactSpelling = true)]
        private static extern IntPtr
            V6NativeGetForegroundWindow();

        [DllImport(
            "user32.dll",
            EntryPoint = "GetAsyncKeyState",
            ExactSpelling = true)]
        private static extern short V6NativeGetAsyncKeyState(
            int virtualKey);

        [DllImport(
            "user32.dll",
            EntryPoint = "PostMessageW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V6NativePostMessage(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
