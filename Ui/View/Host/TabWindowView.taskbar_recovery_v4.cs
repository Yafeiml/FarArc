using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace _1RM.View.Host
{
    /// <summary>
    /// Registers the v4 silent-reactivation watchdog without modifying the
    /// official TabWindowView constructor.  The v3 recovery remains responsible
    /// for the explicit WA_ACTIVE failure path; this supplement handles the
    /// second failure path captured on the affected machine, where the HWND is
    /// foreground again but WPF never receives WA_ACTIVE and IsActive stays false.
    /// </summary>
    internal static class TaskbarRecoveryV4Bootstrap
    {
        [ModuleInitializer]
        internal static void RegisterTaskbarRecoveryV4()
        {
            EventManager.RegisterClassHandler(
                typeof(TabWindowView),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnTabWindowLoaded),
                true);
        }

        private static void OnTabWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TabWindowView window)
            {
                window.InitializeTaskbarRecoveryV4();
            }
        }
    }

    public partial class TabWindowView
    {
        private const string RecoveryV4Version =
            "TaskbarRecoveryV4-silent-foreground-watchdog";

        // All normal taskbar clicks in the captured log delivered SC_MINIMIZE
        // within 1.4-79.8 ms.  Start the watchdog after that normal envelope.
        private const int RecoveryV4InitialDelayMilliseconds = 110;
        private const int RecoveryV4PollIntervalMilliseconds = 35;

        // The newly captured silent failures persisted for roughly 0.8-1.8 s.
        // Keep the candidate alive long enough to observe that state, but require
        // an exact foreground/activation contradiction before taking action.
        private const int RecoveryV4CandidateLifetimeMilliseconds = 2500;
        private const int RecoveryV4RequiredMismatchSamples = 2;

        private HwndSource? _taskbarRecoveryV4HwndSource;
        private DispatcherTimer? _taskbarRecoveryV4Timer;
        private IntPtr _taskbarRecoveryV4Hwnd = IntPtr.Zero;

        private bool _taskbarRecoveryV4Initialized;
        private bool _taskbarRecoveryV4Disabled;
        private bool _taskbarRecoveryV4CandidateArmed;
        private bool _taskbarRecoveryV4SyntheticMinimizeInProgress;
        private DateTime _taskbarRecoveryV4CandidateStartedUtc = DateTime.MinValue;
        private long _taskbarRecoveryV4Sequence;
        private long _taskbarRecoveryV4CandidateId;
        private int _taskbarRecoveryV4MismatchSamples;
        private string _taskbarRecoveryV4InitialHit = string.Empty;

        internal void InitializeTaskbarRecoveryV4()
        {
            if (_taskbarRecoveryV4Initialized || _taskbarRecoveryV4Disabled)
            {
                return;
            }

            _taskbarRecoveryV4Initialized = true;

            try
            {
                _taskbarRecoveryV4Hwnd = new WindowInteropHelper(this).Handle;
                if (_taskbarRecoveryV4Hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "The TabWindowView HWND was not available at Loaded.");
                }

                _taskbarRecoveryV4HwndSource =
                    HwndSource.FromHwnd(_taskbarRecoveryV4Hwnd);
                if (_taskbarRecoveryV4HwndSource == null)
                {
                    throw new InvalidOperationException(
                        "HwndSource.FromHwnd returned null for TabWindowView.");
                }

                _taskbarRecoveryV4HwndSource.AddHook(TaskbarRecoveryV4WndProc);

                _taskbarRecoveryV4Timer = new DispatcherTimer(
                    DispatcherPriority.Send,
                    Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(
                        RecoveryV4InitialDelayMilliseconds),
                };
                _taskbarRecoveryV4Timer.Tick += TaskbarRecoveryV4TimerOnTick;

                Closed += TaskbarRecoveryV4OnClosed;

                WriteTaskbarRecoveryLog(
                    "V4_INIT",
                    $"version={RecoveryV4Version}; " +
                    $"initialDelayMs={RecoveryV4InitialDelayMilliseconds}; " +
                    $"pollMs={RecoveryV4PollIntervalMilliseconds}; " +
                    $"lifetimeMs={RecoveryV4CandidateLifetimeMilliseconds}; " +
                    $"requiredMismatchSamples={RecoveryV4RequiredMismatchSamples}");
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV4("initialization failed", ex);
            }
        }

        private IntPtr TaskbarRecoveryV4WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarRecoveryV4Disabled)
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
                        TryArmTaskbarRecoveryV4(
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
                            _taskbarRecoveryV4SyntheticMinimizeInProgress
                                ? "V4_SYNTHETIC_SC_MINIMIZE"
                                : "V4_NATIVE_SC_MINIMIZE",
                            $"candidate={_taskbarRecoveryV4CandidateId}; " +
                            $"elapsedMs={TaskbarRecoveryV4ElapsedMilliseconds():F1}");

                        CancelTaskbarRecoveryV4Candidate(
                            "SC_MINIMIZE received");
                    }
                }
                else if (msg == WmSize &&
                         unchecked((int)wParam.ToInt64()) == SizeMinimized)
                {
                    WriteTaskbarRecoveryLog(
                        "V4_SIZE_MINIMIZED",
                        $"candidate={_taskbarRecoveryV4CandidateId}");

                    CancelTaskbarRecoveryV4Candidate(
                        "SIZE_MINIMIZED received");
                }
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV4(
                    $"WndProc failed for message 0x{msg:X4}",
                    ex);
            }

            // Observe and repair only; never consume a native message.
            return IntPtr.Zero;
        }

        private void TryArmTaskbarRecoveryV4(
            IntPtr hwnd,
            bool minimizedFlag,
            IntPtr otherWindow)
        {
            TaskbarHit hit = CaptureTaskbarHit();
            bool nativeVisible =
                hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);
            bool iconic =
                hwnd != IntPtr.Zero && NativeIsIconic(hwnd);

            bool eligible =
                _taskbarRecoveryV4Timer != null &&
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

            CancelTaskbarRecoveryV4Candidate(
                "replaced by newer taskbar candidate",
                false);

            _taskbarRecoveryV4CandidateId = ++_taskbarRecoveryV4Sequence;
            _taskbarRecoveryV4CandidateArmed = true;
            _taskbarRecoveryV4CandidateStartedUtc = DateTime.UtcNow;
            _taskbarRecoveryV4MismatchSamples = 0;
            _taskbarRecoveryV4InitialHit = hit.Describe();

            WriteTaskbarRecoveryLog(
                "V4_ARM",
                $"candidate={_taskbarRecoveryV4CandidateId}; " +
                $"other=0x{otherWindow.ToInt64():X}; " +
                _taskbarRecoveryV4InitialHit);

            _taskbarRecoveryV4Timer!.Stop();
            _taskbarRecoveryV4Timer.Interval = TimeSpan.FromMilliseconds(
                RecoveryV4InitialDelayMilliseconds);
            _taskbarRecoveryV4Timer.Start();
        }

        private void TaskbarRecoveryV4TimerOnTick(object? sender, EventArgs e)
        {
            _taskbarRecoveryV4Timer?.Stop();

            if (_taskbarRecoveryV4Disabled ||
                !_taskbarRecoveryV4CandidateArmed)
            {
                return;
            }

            long candidateId = _taskbarRecoveryV4CandidateId;
            double elapsedMilliseconds =
                TaskbarRecoveryV4ElapsedMilliseconds();

            try
            {
                IntPtr hwnd = _taskbarRecoveryV4Hwnd;
                IntPtr foreground = NativeGetForegroundWindow();
                bool nativeVisible =
                    hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd);
                bool iconic =
                    hwnd != IntPtr.Zero && NativeIsIconic(hwnd);

                if (hwnd == IntPtr.Zero ||
                    !IsLoaded ||
                    !IsVisible ||
                    !ShowInTaskbar ||
                    !nativeVisible ||
                    iconic ||
                    WindowState == WindowState.Minimized)
                {
                    CancelTaskbarRecoveryV4Candidate(
                        "window no longer eligible");
                    return;
                }

                if (elapsedMilliseconds < 0 ||
                    elapsedMilliseconds >
                        RecoveryV4CandidateLifetimeMilliseconds)
                {
                    WriteTaskbarRecoveryLog(
                        "V4_EXPIRED",
                        $"candidate={candidateId}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"active={IsActive}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"mismatchSamples={_taskbarRecoveryV4MismatchSamples}");

                    CancelTaskbarRecoveryV4Candidate(
                        "candidate lifetime expired");
                    return;
                }

                // This is the exact state v3 missed in candidates 9, 22 and 24:
                // user32 reports this HWND as foreground, while WPF still reports
                // the same window inactive and the HWND remains non-iconic.
                bool silentForegroundMismatch =
                    foreground == hwnd && !IsActive;

                if (silentForegroundMismatch)
                {
                    _taskbarRecoveryV4MismatchSamples++;

                    WriteTaskbarRecoveryLog(
                        "V4_SILENT_FOREGROUND_MISMATCH_SAMPLE",
                        $"candidate={candidateId}; " +
                        $"sample={_taskbarRecoveryV4MismatchSamples}; " +
                        $"elapsedMs={elapsedMilliseconds:F1}; " +
                        $"managedState={WindowState}; " +
                        $"foreground=0x{foreground.ToInt64():X}; " +
                        $"initialHit={_taskbarRecoveryV4InitialHit}");
                }
                else
                {
                    _taskbarRecoveryV4MismatchSamples = 0;
                }

                if (_taskbarRecoveryV4MismatchSamples >=
                    RecoveryV4RequiredMismatchSamples)
                {
                    RecoverSilentForegroundMismatchV4(
                        candidateId,
                        elapsedMilliseconds,
                        hwnd);
                    return;
                }

                _taskbarRecoveryV4Timer!.Interval =
                    TimeSpan.FromMilliseconds(
                        RecoveryV4PollIntervalMilliseconds);
                _taskbarRecoveryV4Timer.Start();
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV4(
                    $"candidate {candidateId} watchdog failed",
                    ex);
            }
        }

        private void RecoverSilentForegroundMismatchV4(
            long candidateId,
            double elapsedMilliseconds,
            IntPtr hwnd)
        {
            WriteTaskbarRecoveryLog(
                "V4_RECOVERY_SEND_SC_MINIMIZE",
                $"candidate={candidateId}; " +
                $"reason=foreground-self-wpf-inactive; " +
                $"elapsedMs={elapsedMilliseconds:F1}; " +
                $"mismatchSamples={_taskbarRecoveryV4MismatchSamples}");

            _taskbarRecoveryV4SyntheticMinimizeInProgress = true;
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
                _taskbarRecoveryV4SyntheticMinimizeInProgress = false;
            }

            bool resultIconic = NativeIsIconic(hwnd);
            WriteTaskbarRecoveryLog(
                "V4_RECOVERY_RESULT",
                $"candidate={candidateId}; " +
                $"managedState={WindowState}; iconic={resultIconic}");

            if (!resultIconic &&
                WindowState != WindowState.Minimized)
            {
                WriteTaskbarRecoveryLog(
                    "V4_RECOVERY_WPF_FALLBACK",
                    $"candidate={candidateId}");
                WindowState = WindowState.Minimized;
            }

            CancelTaskbarRecoveryV4Candidate(
                "silent foreground recovery completed");
        }

        private void CancelTaskbarRecoveryV4Candidate(
            string reason,
            bool writeLog = true)
        {
            if (!_taskbarRecoveryV4CandidateArmed)
            {
                return;
            }

            try
            {
                _taskbarRecoveryV4Timer?.Stop();
            }
            catch
            {
                // Best-effort cleanup only.
            }

            if (writeLog)
            {
                WriteTaskbarRecoveryLog(
                    "V4_CANCEL",
                    $"candidate={_taskbarRecoveryV4CandidateId}; " +
                    $"reason={reason}; " +
                    $"mismatchSamples={_taskbarRecoveryV4MismatchSamples}");
            }

            _taskbarRecoveryV4CandidateArmed = false;
            _taskbarRecoveryV4CandidateStartedUtc = DateTime.MinValue;
            _taskbarRecoveryV4MismatchSamples = 0;
            _taskbarRecoveryV4InitialHit = string.Empty;
        }

        private double TaskbarRecoveryV4ElapsedMilliseconds()
        {
            if (_taskbarRecoveryV4CandidateStartedUtc == DateTime.MinValue)
            {
                return -1;
            }

            return (DateTime.UtcNow -
                    _taskbarRecoveryV4CandidateStartedUtc)
                .TotalMilliseconds;
        }

        private void DisableTaskbarRecoveryV4(
            string context,
            Exception exception)
        {
            WriteTaskbarRecoveryLog(
                "V4_DISABLED",
                $"context={context}; " +
                $"exception={exception.GetType().FullName}: " +
                exception.Message);

            _taskbarRecoveryV4Disabled = true;
            CancelTaskbarRecoveryV4Candidate(context, false);
        }

        private void TaskbarRecoveryV4OnClosed(object? sender, EventArgs e)
        {
            try
            {
                CancelTaskbarRecoveryV4Candidate("window closed");

                if (_taskbarRecoveryV4Timer != null)
                {
                    _taskbarRecoveryV4Timer.Stop();
                    _taskbarRecoveryV4Timer.Tick -=
                        TaskbarRecoveryV4TimerOnTick;
                    _taskbarRecoveryV4Timer = null;
                }

                if (_taskbarRecoveryV4HwndSource != null)
                {
                    _taskbarRecoveryV4HwndSource.RemoveHook(
                        TaskbarRecoveryV4WndProc);
                    _taskbarRecoveryV4HwndSource = null;
                }

                Closed -= TaskbarRecoveryV4OnClosed;
                WriteTaskbarRecoveryLog(
                    "V4_CLOSED",
                    "silent foreground watchdog detached");
            }
            catch (Exception ex)
            {
                WriteTaskbarRecoveryLog(
                    "V4_CLOSE_ERROR",
                    ex.ToString());
            }
        }
    }
}
