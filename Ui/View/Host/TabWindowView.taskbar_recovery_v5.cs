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
    /// Windows 11 25H2 / StartAllBack active-taskbar-button compatibility.
    ///
    /// Captured normal path:
    ///   WA_INACTIVE over MSTaskListWClass -> SC_MINIMIZE within 1-80 ms.
    ///
    /// Captured failure paths:
    ///   A. The same HWND becomes foreground again without SC_MINIMIZE.
    ///   B. Foreground remains 0 / Shell_TrayWnd for seconds, while the session
    ///      HWND remains visible and non-iconic; WPF may receive no useful
    ///      activation transition before the user manually minimizes it.
    ///
    /// V5 uses a thread-pool watchdog, not DispatcherTimer.  It cancels as soon
    /// as a foreign application becomes foreground.  It posts the standard
    /// native SC_MINIMIZE only when either the original HWND returns to the
    /// foreground or the taskbar transition remains stuck beyond the generous
    /// normal-minimize envelope.  It never consumes messages or mutates taskbar
    /// identity, ownership, styles, or ShowInTaskbar.
    /// </summary>
    public partial class TabWindowView
    {
        private const string RecoveryV5Version =
            "TaskbarRecoveryV5-native-foreground-state-machine";

        private const int V5WmSize = 0x0005;
        private const int V5WmActivate = 0x0006;
        private const int V5WmSysCommand = 0x0112;

        private const int V5WaInactive = 0;
        private const int V5WaActive = 1;
        private const int V5WaClickActive = 2;
        private const int V5SizeMinimized = 1;
        private const int V5ScMinimize = 0xF020;
        private const int V5ScRestore = 0xF120;

        private const uint V5GaRoot = 2;

        // Normal native minimize was always delivered within 80.2 ms in the
        // latest affected-machine trace.  Start observation after that window.
        private const int V5InitialDelayMilliseconds = 120;
        private const int V5PollIntervalMilliseconds = 25;
        private const int V5ExplicitReactivationGraceMilliseconds = 40;

        // Both reported V4 failures remained in the taskbar/zero foreground
        // transition for far longer than normal.  700 ms leaves almost 9x the
        // observed native envelope before recovery.
        private const int V5ShellStallRecoveryMilliseconds = 700;
        private const int V5CandidateLifetimeMilliseconds = 6000;
        private const int V5RequiredSelfForegroundSamples = 2;
        private const int V5RecoveryResultDelayMilliseconds = 250;

        private readonly object _taskbarRecoveryV5StateLock = new object();
        private readonly object _taskbarRecoveryV5LogLock = new object();

        private HwndSource? _taskbarRecoveryV5HwndSource;
        private System.Threading.Timer? _taskbarRecoveryV5Timer;
        private StreamWriter? _taskbarRecoveryV5LogWriter;
        private IntPtr _taskbarRecoveryV5Hwnd = IntPtr.Zero;

        private bool _taskbarRecoveryV5Disabled;
        private bool _taskbarRecoveryV5Closed;
        private bool _taskbarRecoveryV5CandidateArmed;
        private bool _taskbarRecoveryV5RecoveryPosted;
        private long _taskbarRecoveryV5Sequence;
        private long _taskbarRecoveryV5CandidateId;
        private long _taskbarRecoveryV5CandidateStartedTimestamp;
        private long _taskbarRecoveryV5RecoveryPostedTimestamp;
        private int _taskbarRecoveryV5SelfForegroundSamples;
        private int _taskbarRecoveryV5TransitionSamples;
        private V5ForegroundKind _taskbarRecoveryV5LastForegroundKind;
        private string _taskbarRecoveryV5InitialHit = string.Empty;
        private string _taskbarRecoveryV5RecoveryReason = string.Empty;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _taskbarRecoveryV5Hwnd = new WindowInteropHelper(this).Handle;
            _myHandle = _taskbarRecoveryV5Hwnd;
            InitializeTaskbarRecoveryV5Log();

            try
            {
                if (_taskbarRecoveryV5Hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "TabWindowView HWND was unavailable at SourceInitialized.");
                }

                _taskbarRecoveryV5HwndSource =
                    HwndSource.FromHwnd(_taskbarRecoveryV5Hwnd);
                if (_taskbarRecoveryV5HwndSource == null)
                {
                    throw new InvalidOperationException(
                        "HwndSource.FromHwnd returned null for TabWindowView.");
                }

                _taskbarRecoveryV5HwndSource.AddHook(
                    TaskbarRecoveryV5WndProc);

                _taskbarRecoveryV5Timer = new System.Threading.Timer(
                    TaskbarRecoveryV5TimerCallback,
                    null,
                    Timeout.Infinite,
                    Timeout.Infinite);

                StateChanged += TaskbarRecoveryV5OnStateChanged;
                Closed += TaskbarRecoveryV5OnClosed;

                WriteTaskbarRecoveryV5Log(
                    "V5_INIT",
                    $"version={RecoveryV5Version}; " +
                    $"initialDelayMs={V5InitialDelayMilliseconds}; " +
                    $"pollMs={V5PollIntervalMilliseconds}; " +
                    $"shellStallMs={V5ShellStallRecoveryMilliseconds}; " +
                    $"lifetimeMs={V5CandidateLifetimeMilliseconds}; " +
                    $"requiredSelfSamples={V5RequiredSelfForegroundSamples}; " +
                    "threadPoolTimer=true; restoreMessages=untouched; " +
                    "AppUserModelID=unchanged; ITaskbarList=unused");
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV5("initialization failed", ex);
            }
        }

        private IntPtr TaskbarRecoveryV5WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarRecoveryV5Disabled)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (msg == V5WmActivate)
                {
                    int activationState = V5LowWord(wParam);
                    bool minimizedFlag = V5HighWord(wParam) != 0;

                    if (activationState == V5WaInactive)
                    {
                        TryArmTaskbarRecoveryV5(
                            hwnd,
                            minimizedFlag,
                            lParam);
                    }
                    else if ((activationState == V5WaActive ||
                              activationState == V5WaClickActive) &&
                             IsTaskbarRecoveryV5CandidateArmed())
                    {
                        HandleTaskbarRecoveryV5ExplicitReactivation(
                            hwnd,
                            minimizedFlag,
                            lParam);
                    }
                }
                else if (msg == V5WmSysCommand)
                {
                    int command = unchecked(
                        (int)(wParam.ToInt64() & 0xFFF0L));

                    if (command == V5ScMinimize)
                    {
                        bool synthetic;
                        long candidateId;
                        lock (_taskbarRecoveryV5StateLock)
                        {
                            synthetic =
                                _taskbarRecoveryV5CandidateArmed &&
                                _taskbarRecoveryV5RecoveryPosted;
                            candidateId = _taskbarRecoveryV5CandidateId;
                        }

                        WriteTaskbarRecoveryV5Log(
                            synthetic
                                ? "V5_SYNTHETIC_SC_MINIMIZE"
                                : "V5_NATIVE_SC_MINIMIZE",
                            $"candidate={candidateId}; " +
                            $"elapsedMs={TaskbarRecoveryV5ElapsedMilliseconds():F1}");

                        CancelTaskbarRecoveryV5Candidate(
                            "SC_MINIMIZE received");
                    }
                    else if (command == V5ScRestore)
                    {
                        bool iconic =
                            hwnd != IntPtr.Zero && V5NativeIsIconic(hwnd);

                        WriteTaskbarRecoveryV5Log(
                            "V5_SC_RESTORE",
                            $"candidate={GetTaskbarRecoveryV5CandidateId()}; " +
                            $"iconic={iconic}");

                        // A restore from a genuinely minimized state is normal.
                        // A restore against an already non-iconic window is part
                        // of the broken taskbar transaction, so keep observing.
                        if (iconic)
                        {
                            CancelTaskbarRecoveryV5Candidate(
                                "legitimate SC_RESTORE received");
                        }
                        else
                        {
                            ScheduleTaskbarRecoveryV5Timer(
                                V5ExplicitReactivationGraceMilliseconds);
                        }
                    }
                }
                else if (msg == V5WmSize &&
                         unchecked((int)wParam.ToInt64()) ==
                         V5SizeMinimized)
                {
                    WriteTaskbarRecoveryV5Log(
                        "V5_SIZE_MINIMIZED",
                        $"candidate={GetTaskbarRecoveryV5CandidateId()}");

                    CancelTaskbarRecoveryV5Candidate(
                        "SIZE_MINIMIZED received");
                }
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV5(
                    $"WndProc failed for message 0x{msg:X4}",
                    ex);
            }

            // Observation/recovery only.  Never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarRecoveryV5(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            V5TaskbarHit hit = CaptureTaskbarRecoveryV5Hit();
            bool nativeVisible =
                hwnd != IntPtr.Zero && V5NativeIsWindowVisible(hwnd);
            bool iconic =
                hwnd != IntPtr.Zero && V5NativeIsIconic(hwnd);

            bool eligible =
                _taskbarRecoveryV5Timer != null &&
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

            long candidateId;
            lock (_taskbarRecoveryV5StateLock)
            {
                _taskbarRecoveryV5CandidateId =
                    ++_taskbarRecoveryV5Sequence;
                candidateId = _taskbarRecoveryV5CandidateId;
                _taskbarRecoveryV5CandidateArmed = true;
                _taskbarRecoveryV5RecoveryPosted = false;
                _taskbarRecoveryV5CandidateStartedTimestamp =
                    Stopwatch.GetTimestamp();
                _taskbarRecoveryV5RecoveryPostedTimestamp = 0;
                _taskbarRecoveryV5SelfForegroundSamples = 0;
                _taskbarRecoveryV5TransitionSamples = 0;
                _taskbarRecoveryV5LastForegroundKind =
                    V5ForegroundKind.None;
                _taskbarRecoveryV5InitialHit = hit.Describe();
                _taskbarRecoveryV5RecoveryReason = string.Empty;

                _taskbarRecoveryV5Timer?.Change(
                    V5InitialDelayMilliseconds,
                    Timeout.Infinite);
            }

            WriteTaskbarRecoveryV5Log(
                "V5_ARM",
                $"candidate={candidateId}; " +
                $"other=0x{otherWindow.ToInt64():X}; " +
                hit.Describe());
        }

        private void HandleTaskbarRecoveryV5ExplicitReactivation(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            if (minimizedFlag ||
                hwnd == IntPtr.Zero ||
                V5NativeIsIconic(hwnd))
            {
                CancelTaskbarRecoveryV5Candidate(
                    "reactivation arrived after minimization");
                return;
            }

            WriteTaskbarRecoveryV5Log(
                "V5_WA_ACTIVE_NONICONIC",
                $"candidate={GetTaskbarRecoveryV5CandidateId()}; " +
                $"elapsedMs={TaskbarRecoveryV5ElapsedMilliseconds():F1}; " +
                $"other=0x{otherWindow.ToInt64():X}");

            // Do not depend on WPF IsActive.  Let the native watchdog verify
            // foreground ownership after a small late-message grace period.
            ScheduleTaskbarRecoveryV5Timer(
                V5ExplicitReactivationGraceMilliseconds);
        }

        private void TaskbarRecoveryV5TimerCallback(object? state)
        {
            long candidateId;
            bool recoveryPosted;
            long recoveryPostedTimestamp;

            lock (_taskbarRecoveryV5StateLock)
            {
                if (_taskbarRecoveryV5Disabled ||
                    _taskbarRecoveryV5Closed ||
                    !_taskbarRecoveryV5CandidateArmed)
                {
                    return;
                }

                candidateId = _taskbarRecoveryV5CandidateId;
                recoveryPosted = _taskbarRecoveryV5RecoveryPosted;
                recoveryPostedTimestamp =
                    _taskbarRecoveryV5RecoveryPostedTimestamp;
            }

            try
            {
                IntPtr hwnd = _taskbarRecoveryV5Hwnd;
                if (hwnd == IntPtr.Zero ||
                    !V5NativeIsWindow(hwnd) ||
                    !V5NativeIsWindowVisible(hwnd))
                {
                    CancelTaskbarRecoveryV5Candidate(
                        "window is unavailable or invisible");
                    return;
                }

                bool iconic = V5NativeIsIconic(hwnd);
                if (iconic)
                {
                    WriteTaskbarRecoveryV5Log(
                        recoveryPosted
                            ? "V5_RECOVERY_RESULT"
                            : "V5_NATIVE_MINIMIZE_OBSERVED",
                        $"candidate={candidateId}; iconic=True");

                    CancelTaskbarRecoveryV5Candidate(
                        "window became iconic");
                    return;
                }

                if (recoveryPosted)
                {
                    double postedElapsedMilliseconds =
                        V5ElapsedMilliseconds(recoveryPostedTimestamp);

                    if (postedElapsedMilliseconds >=
                        V5RecoveryResultDelayMilliseconds)
                    {
                        WriteTaskbarRecoveryV5Log(
                            "V5_RECOVERY_WPF_FALLBACK",
                            $"candidate={candidateId}; " +
                            $"reason={_taskbarRecoveryV5RecoveryReason}; " +
                            $"postedElapsedMs={postedElapsedMilliseconds:F1}");

                        Dispatcher.BeginInvoke(
                            new Action(() =>
                            {
                                if (!IsClosed &&
                                    WindowState != WindowState.Minimized)
                                {
                                    WindowState = WindowState.Minimized;
                                }
                            }));

                        CancelTaskbarRecoveryV5Candidate(
                            "WPF fallback queued");
                        return;
                    }

                    ScheduleTaskbarRecoveryV5Timer(
                        V5PollIntervalMilliseconds);
                    return;
                }

                double elapsedMilliseconds =
                    TaskbarRecoveryV5ElapsedMilliseconds();
                if (elapsedMilliseconds < 0 ||
                    elapsedMilliseconds >
                        V5CandidateLifetimeMilliseconds)
                {
                    WriteTaskbarRecoveryV5Log(
                        "V5_EXPIRED",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}");

                    CancelTaskbarRecoveryV5Candidate(
                        "candidate lifetime expired");
                    return;
                }

                IntPtr foreground = V5NativeGetForegroundWindow();
                IntPtr foregroundRoot = V5GetRootWindow(foreground);
                string foregroundClass = V5GetClassName(foregroundRoot);
                V5ForegroundKind foregroundKind =
                    ClassifyTaskbarRecoveryV5Foreground(
                        foreground,
                        foregroundRoot,
                        foregroundClass,
                        hwnd);

                int selfSamples;
                int transitionSamples;
                V5ForegroundKind previousKind;

                lock (_taskbarRecoveryV5StateLock)
                {
                    if (!_taskbarRecoveryV5CandidateArmed ||
                        _taskbarRecoveryV5CandidateId != candidateId)
                    {
                        return;
                    }

                    previousKind =
                        _taskbarRecoveryV5LastForegroundKind;
                    _taskbarRecoveryV5LastForegroundKind =
                        foregroundKind;

                    if (foregroundKind == V5ForegroundKind.Self)
                    {
                        _taskbarRecoveryV5SelfForegroundSamples++;
                        _taskbarRecoveryV5TransitionSamples = 0;
                    }
                    else if (foregroundKind ==
                             V5ForegroundKind.TaskbarTransition)
                    {
                        _taskbarRecoveryV5TransitionSamples++;
                        _taskbarRecoveryV5SelfForegroundSamples = 0;
                    }
                    else
                    {
                        _taskbarRecoveryV5SelfForegroundSamples = 0;
                        _taskbarRecoveryV5TransitionSamples = 0;
                    }

                    selfSamples =
                        _taskbarRecoveryV5SelfForegroundSamples;
                    transitionSamples =
                        _taskbarRecoveryV5TransitionSamples;
                }

                if (foregroundKind == V5ForegroundKind.Foreign)
                {
                    WriteTaskbarRecoveryV5Log(
                        "V5_FOREIGN_FOREGROUND_CANCEL",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"root=0x{foregroundRoot.ToInt64():X}" +
                        $"[{foregroundClass}]");

                    CancelTaskbarRecoveryV5Candidate(
                        "a foreign application became foreground");
                    return;
                }

                if (foregroundKind != previousKind ||
                    selfSamples == 1 ||
                    transitionSamples == 1 ||
                    transitionSamples % 10 == 0)
                {
                    WriteTaskbarRecoveryV5Log(
                        "V5_WATCHDOG_SAMPLE",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"kind={foregroundKind}; " +
                        $"selfSamples={selfSamples}; " +
                        $"transitionSamples={transitionSamples}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"root=0x{foregroundRoot.ToInt64():X}" +
                        $"[{foregroundClass}]");
                }

                string? recoveryReason = null;
                if (foregroundKind == V5ForegroundKind.Self &&
                    selfSamples >= V5RequiredSelfForegroundSamples)
                {
                    recoveryReason =
                        "same-session-hwnd-returned-foreground";
                }
                else if (foregroundKind ==
                             V5ForegroundKind.TaskbarTransition &&
                         elapsedMilliseconds >=
                             V5ShellStallRecoveryMilliseconds)
                {
                    recoveryReason =
                        "taskbar-or-zero-foreground-stalled";
                }

                if (recoveryReason != null)
                {
                    PostTaskbarRecoveryV5Minimize(
                        candidateId,
                        elapsedMilliseconds,
                        recoveryReason,
                        hwnd,
                        foreground,
                        foregroundRoot,
                        foregroundClass);
                    return;
                }

                ScheduleTaskbarRecoveryV5Timer(
                    V5PollIntervalMilliseconds);
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV5(
                    $"candidate {candidateId} watchdog failed",
                    ex);
            }
        }

        private void PostTaskbarRecoveryV5Minimize(
            long candidateId,
            double elapsedMilliseconds,
            string recoveryReason,
            IntPtr hwnd,
            IntPtr foreground,
            IntPtr foregroundRoot,
            string foregroundClass)
        {
            lock (_taskbarRecoveryV5StateLock)
            {
                if (!_taskbarRecoveryV5CandidateArmed ||
                    _taskbarRecoveryV5CandidateId != candidateId ||
                    _taskbarRecoveryV5RecoveryPosted)
                {
                    return;
                }

                _taskbarRecoveryV5RecoveryPosted = true;
                _taskbarRecoveryV5RecoveryPostedTimestamp =
                    Stopwatch.GetTimestamp();
                _taskbarRecoveryV5RecoveryReason = recoveryReason;
            }

            WriteTaskbarRecoveryV5Log(
                "V5_RECOVERY_POST_SC_MINIMIZE",
                $"candidate={candidateId}; " +
                $"reason={recoveryReason}; " +
                $"elapsedMs={elapsedMilliseconds:F1}; " +
                $"foreground=0x{foreground.ToInt64():X}; " +
                $"root=0x{foregroundRoot.ToInt64():X}" +
                $"[{foregroundClass}]; " +
                $"initialHit={_taskbarRecoveryV5InitialHit}");

            bool posted = V5NativePostMessage(
                hwnd,
                V5WmSysCommand,
                new IntPtr(V5ScMinimize),
                IntPtr.Zero);

            WriteTaskbarRecoveryV5Log(
                "V5_RECOVERY_POST_RESULT",
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

                CancelTaskbarRecoveryV5Candidate(
                    "PostMessage failed; WPF fallback queued");
                return;
            }

            ScheduleTaskbarRecoveryV5Timer(
                V5RecoveryResultDelayMilliseconds);
        }

        private bool IsTaskbarRecoveryV5CandidateArmed()
        {
            lock (_taskbarRecoveryV5StateLock)
            {
                return _taskbarRecoveryV5CandidateArmed;
            }
        }

        private long GetTaskbarRecoveryV5CandidateId()
        {
            lock (_taskbarRecoveryV5StateLock)
            {
                return _taskbarRecoveryV5CandidateId;
            }
        }

        private void ScheduleTaskbarRecoveryV5Timer(int dueMilliseconds)
        {
            lock (_taskbarRecoveryV5StateLock)
            {
                if (_taskbarRecoveryV5Disabled ||
                    _taskbarRecoveryV5Closed ||
                    !_taskbarRecoveryV5CandidateArmed)
                {
                    return;
                }

                _taskbarRecoveryV5Timer?.Change(
                    Math.Max(1, dueMilliseconds),
                    Timeout.Infinite);
            }
        }

        private void CancelTaskbarRecoveryV5Candidate(string reason)
        {
            long candidateId;
            int selfSamples;
            int transitionSamples;
            bool recoveryPosted;

            lock (_taskbarRecoveryV5StateLock)
            {
                if (!_taskbarRecoveryV5CandidateArmed)
                {
                    return;
                }

                candidateId = _taskbarRecoveryV5CandidateId;
                selfSamples =
                    _taskbarRecoveryV5SelfForegroundSamples;
                transitionSamples =
                    _taskbarRecoveryV5TransitionSamples;
                recoveryPosted =
                    _taskbarRecoveryV5RecoveryPosted;

                try
                {
                    _taskbarRecoveryV5Timer?.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }
                catch
                {
                    // Best-effort cancellation only.
                }

                _taskbarRecoveryV5CandidateArmed = false;
                _taskbarRecoveryV5RecoveryPosted = false;
                _taskbarRecoveryV5CandidateStartedTimestamp = 0;
                _taskbarRecoveryV5RecoveryPostedTimestamp = 0;
                _taskbarRecoveryV5SelfForegroundSamples = 0;
                _taskbarRecoveryV5TransitionSamples = 0;
                _taskbarRecoveryV5LastForegroundKind =
                    V5ForegroundKind.None;
                _taskbarRecoveryV5InitialHit = string.Empty;
                _taskbarRecoveryV5RecoveryReason = string.Empty;
            }

            WriteTaskbarRecoveryV5Log(
                "V5_CANCEL",
                $"candidate={candidateId}; reason={reason}; " +
                $"selfSamples={selfSamples}; " +
                $"transitionSamples={transitionSamples}; " +
                $"recoveryPosted={recoveryPosted}");
        }

        private double TaskbarRecoveryV5ElapsedMilliseconds()
        {
            long timestamp;
            lock (_taskbarRecoveryV5StateLock)
            {
                timestamp =
                    _taskbarRecoveryV5CandidateStartedTimestamp;
            }

            return V5ElapsedMilliseconds(timestamp);
        }

        private static double V5ElapsedMilliseconds(long timestamp)
        {
            if (timestamp <= 0)
            {
                return -1;
            }

            return (Stopwatch.GetTimestamp() - timestamp) *
                   1000.0 /
                   Stopwatch.Frequency;
        }

        private void TaskbarRecoveryV5OnStateChanged(
            object? sender,
            EventArgs e)
        {
            bool iconic =
                _taskbarRecoveryV5Hwnd != IntPtr.Zero &&
                V5NativeIsIconic(_taskbarRecoveryV5Hwnd);

            WriteTaskbarRecoveryV5Log(
                "V5_STATE_CHANGED",
                $"managedState={WindowState}; iconic={iconic}");

            if (WindowState == WindowState.Minimized || iconic)
            {
                CancelTaskbarRecoveryV5Candidate(
                    "managed/native state became minimized");
            }
        }

        private void DisableTaskbarRecoveryV5(
            string context,
            Exception exception)
        {
            WriteTaskbarRecoveryV5Log(
                "V5_DISABLED",
                $"context={context}; " +
                $"exception={exception.GetType().FullName}: " +
                exception.Message);

            _taskbarRecoveryV5Disabled = true;
            CancelTaskbarRecoveryV5Candidate(context);
            SimpleLogHelper.Warning(exception);
        }

        private void TaskbarRecoveryV5OnClosed(
            object? sender,
            EventArgs e)
        {
            try
            {
                lock (_taskbarRecoveryV5StateLock)
                {
                    _taskbarRecoveryV5Closed = true;
                    _taskbarRecoveryV5CandidateArmed = false;
                    _taskbarRecoveryV5Timer?.Change(
                        Timeout.Infinite,
                        Timeout.Infinite);
                }

                if (_taskbarRecoveryV5HwndSource != null)
                {
                    _taskbarRecoveryV5HwndSource.RemoveHook(
                        TaskbarRecoveryV5WndProc);
                    _taskbarRecoveryV5HwndSource = null;
                }

                StateChanged -= TaskbarRecoveryV5OnStateChanged;
                Closed -= TaskbarRecoveryV5OnClosed;

                _taskbarRecoveryV5Timer?.Dispose();
                _taskbarRecoveryV5Timer = null;

                WriteTaskbarRecoveryV5Log(
                    "V5_CLOSED",
                    "native foreground state machine detached");
            }
            catch (Exception ex)
            {
                WriteTaskbarRecoveryV5Log(
                    "V5_CLOSE_ERROR",
                    ex.ToString());
            }
            finally
            {
                lock (_taskbarRecoveryV5LogLock)
                {
                    try
                    {
                        _taskbarRecoveryV5LogWriter?.Flush();
                        _taskbarRecoveryV5LogWriter?.Dispose();
                    }
                    catch
                    {
                        // Logging must never affect shutdown.
                    }

                    _taskbarRecoveryV5LogWriter = null;
                }
            }
        }

        private V5TaskbarHit CaptureTaskbarRecoveryV5Hit()
        {
            V5NativeGetCursorPos(out V5NativePoint point);
            IntPtr child = V5NativeWindowFromPoint(point);
            IntPtr root = V5GetRootWindow(child);
            string childClass = V5GetClassName(child);
            string rootClass = V5GetClassName(root);
            bool isTaskList = false;

            IntPtr current = child;
            for (int i = 0; i < 16 && current != IntPtr.Zero; i++)
            {
                string currentClass = V5GetClassName(current);
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

                current = V5NativeGetParent(current);
            }

            bool isTaskbarRoot = V5IsTaskbarRootClass(rootClass);

            return new V5TaskbarHit(
                point,
                child,
                root,
                childClass,
                rootClass,
                isTaskList && isTaskbarRoot);
        }

        private static V5ForegroundKind
            ClassifyTaskbarRecoveryV5Foreground(
                IntPtr foreground,
                IntPtr foregroundRoot,
                string foregroundRootClass,
                IntPtr sessionHwnd)
        {
            if (foregroundRoot == sessionHwnd)
            {
                return V5ForegroundKind.Self;
            }

            if (foreground == IntPtr.Zero ||
                foregroundRoot == IntPtr.Zero ||
                V5IsTaskbarRootClass(foregroundRootClass))
            {
                return V5ForegroundKind.TaskbarTransition;
            }

            return V5ForegroundKind.Foreign;
        }

        private static bool V5IsTaskbarRootClass(string className) =>
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

        private static IntPtr V5GetRootWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            IntPtr root = V5NativeGetAncestor(hwnd, V5GaRoot);
            return root == IntPtr.Zero ? hwnd : root;
        }

        private static string V5GetClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            var buffer = new StringBuilder(256);
            return V5NativeGetClassName(
                       hwnd,
                       buffer,
                       buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        private void InitializeTaskbarRecoveryV5Log()
        {
            string fileName =
                $"TaskbarRecoveryV5-{Environment.ProcessId}-" +
                $"{DateTime.Now:yyyyMMdd-HHmmss}.log";

            try
            {
                string directory = Path.Combine(
                    AppPathHelper.Instance.BaseDirPathForLocality,
                    ".logs");
                Directory.CreateDirectory(directory);
                OpenTaskbarRecoveryV5Log(
                    Path.Combine(directory, fileName));
            }
            catch
            {
                try
                {
                    string directory = Path.Combine(
                        Path.GetTempPath(),
                        "1Remote-TaskbarRecoveryV5");
                    Directory.CreateDirectory(directory);
                    OpenTaskbarRecoveryV5Log(
                        Path.Combine(directory, fileName));
                }
                catch
                {
                    _taskbarRecoveryV5LogWriter = null;
                }
            }
        }

        private void OpenTaskbarRecoveryV5Log(string path)
        {
            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.WriteThrough);

            _taskbarRecoveryV5LogWriter = new StreamWriter(
                stream,
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }

        private void WriteTaskbarRecoveryV5Log(
            string eventName,
            string details)
        {
            StreamWriter? writer =
                _taskbarRecoveryV5LogWriter;
            if (writer == null)
            {
                return;
            }

            string nativeState;
            try
            {
                IntPtr hwnd = _taskbarRecoveryV5Hwnd;
                IntPtr foreground = V5NativeGetForegroundWindow();
                IntPtr foregroundRoot = V5GetRootWindow(foreground);
                string foregroundClass =
                    V5GetClassName(foregroundRoot);

                nativeState =
                    $"hwnd=0x{hwnd.ToInt64():X}; " +
                    $"nativeVisible=" +
                    $"{(hwnd != IntPtr.Zero && V5NativeIsWindowVisible(hwnd))}; " +
                    $"iconic=" +
                    $"{(hwnd != IntPtr.Zero && V5NativeIsIconic(hwnd))}; " +
                    $"foreground=0x{foreground.ToInt64():X}; " +
                    $"foregroundRoot=0x{foregroundRoot.ToInt64():X}" +
                    $"[{foregroundClass}]";
            }
            catch (Exception ex)
            {
                nativeState =
                    $"snapshotError={ex.GetType().Name}: {ex.Message}";
            }

            string line =
                $"time={DateTime.Now:O}; event={eventName}; " +
                $"{details}; thread={Environment.CurrentManagedThreadId}; " +
                nativeState;

            try
            {
                lock (_taskbarRecoveryV5LogLock)
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

        private static int V5LowWord(IntPtr value) =>
            unchecked((ushort)(value.ToInt64() & 0xFFFFL));

        private static int V5HighWord(IntPtr value) =>
            unchecked((ushort)((value.ToInt64() >> 16) & 0xFFFFL));

        private enum V5ForegroundKind
        {
            None,
            Self,
            TaskbarTransition,
            Foreign,
        }

        private readonly struct V5TaskbarHit
        {
            private readonly V5NativePoint _point;
            private readonly IntPtr _child;
            private readonly IntPtr _root;
            private readonly string _childClass;
            private readonly string _rootClass;

            public V5TaskbarHit(
                V5NativePoint point,
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
        private struct V5NativePoint
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
        private static extern bool V5NativeGetCursorPos(
            out V5NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "WindowFromPoint",
            ExactSpelling = true)]
        private static extern IntPtr V5NativeWindowFromPoint(
            V5NativePoint point);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetAncestor",
            ExactSpelling = true)]
        private static extern IntPtr V5NativeGetAncestor(
            IntPtr hwnd,
            uint flags);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetParent",
            ExactSpelling = true)]
        private static extern IntPtr V5NativeGetParent(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        private static extern int V5NativeGetClassName(
            IntPtr hwnd,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindow",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V5NativeIsWindow(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsWindowVisible",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V5NativeIsWindowVisible(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "IsIconic",
            ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V5NativeIsIconic(IntPtr hwnd);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetForegroundWindow",
            ExactSpelling = true)]
        private static extern IntPtr V5NativeGetForegroundWindow();

        [DllImport(
            "user32.dll",
            EntryPoint = "PostMessageW",
            CharSet = CharSet.Unicode,
            ExactSpelling = true,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool V5NativePostMessage(
            IntPtr hwnd,
            int message,
            IntPtr wParam,
            IntPtr lParam);
    }
}
