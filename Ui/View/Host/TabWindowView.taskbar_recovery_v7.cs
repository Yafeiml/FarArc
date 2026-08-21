using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Interop;

namespace _1RM.View.Host
{
    /// <summary>
    /// Registers the V7 late-native-minimize de-duplicator after the V6 native
    /// recovery hook. V6 remains responsible for repairing a missed active
    /// taskbar minimize. V7 handles the second race captured on the affected
    /// machine: StartAllBack/Explorer may deliver its original SC_MINIMIZE
    /// hundreds of milliseconds after V6 already repaired, minimized, and the
    /// user restored the same window.
    /// </summary>
    internal static class TaskbarRecoveryV7Bootstrap
    {
        [ModuleInitializer]
        internal static void RegisterTaskbarRecoveryV7()
        {
            EventManager.RegisterClassHandler(
                typeof(TabWindowView),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnTabWindowLoaded),
                true);
        }

        private static void OnTabWindowLoaded(
            object sender,
            RoutedEventArgs e)
        {
            if (sender is TabWindowView window)
            {
                window.InitializeTaskbarRecoveryV7();
            }
        }
    }

    public partial class TabWindowView
    {
        private const string RecoveryV7Version =
            "TaskbarRecoveryV7-late-native-minimize-dedup";

        // The affected-machine V6 trace captured delayed native commands at
        // 260.7 ms and 978.5 ms after synthetic recovery. Keep a measured,
        // bounded window with margin; every other guard below must also match.
        private const int V7DuplicateWindowMilliseconds = 1500;

        // A current, legitimate taskbar click delivered native SC_MINIMIZE in at
        // most 99.9 ms in this trace. V6 itself takes over at about 170 ms, so a
        // command associated with a taskbar WA_INACTIVE within this envelope is
        // always allowed through.
        private const int V7CurrentTaskbarCommandEnvelopeMilliseconds = 165;
        private const int V7MinimumLateCommandMilliseconds = 100;

        private HwndSource? _taskbarRecoveryV7HwndSource;
        private bool _taskbarRecoveryV7Initialized;
        private bool _taskbarRecoveryV7Disabled;
        private bool _taskbarRecoveryV7Closed;

        private long _taskbarRecoveryV7LastTaskbarInactiveTimestamp;
        private long _taskbarRecoveryV7RestoreAfterSyntheticTimestamp;
        private long _taskbarRecoveryV7ProtectedSyntheticCandidateId;
        private long _taskbarRecoveryV7SuppressedCount;

        internal void InitializeTaskbarRecoveryV7()
        {
            if (_taskbarRecoveryV7Initialized ||
                _taskbarRecoveryV7Disabled)
            {
                return;
            }

            _taskbarRecoveryV7Initialized = true;

            try
            {
                IntPtr hwnd = _taskbarRecoveryV6Hwnd;
                if (hwnd == IntPtr.Zero)
                {
                    hwnd = new WindowInteropHelper(this).Handle;
                }

                if (hwnd == IntPtr.Zero)
                {
                    throw new InvalidOperationException(
                        "TabWindowView HWND was unavailable at Loaded.");
                }

                _taskbarRecoveryV7HwndSource =
                    HwndSource.FromHwnd(hwnd);
                if (_taskbarRecoveryV7HwndSource == null)
                {
                    throw new InvalidOperationException(
                        "HwndSource.FromHwnd returned null for V7.");
                }

                // Added at Loaded, after V6's SourceInitialized hook. V6 can log
                // and update its state first; V7 may then consume only a proven
                // delayed duplicate before DefWindowProc minimizes the HWND.
                _taskbarRecoveryV7HwndSource.AddHook(
                    TaskbarRecoveryV7WndProc);

                Closed += TaskbarRecoveryV7OnClosed;

                WriteTaskbarRecoveryV6Log(
                    "V7_INIT",
                    $"version={RecoveryV7Version}; " +
                    $"duplicateWindowMs={V7DuplicateWindowMilliseconds}; " +
                    $"currentClickEnvelopeMs=" +
                    $"{V7CurrentTaskbarCommandEnvelopeMilliseconds}; " +
                    "requiresRestoreAfterSynthetic=true; " +
                    "requiresTaskbarOrZeroForeground=true; " +
                    "targetedHandledMessage=true");
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV7(
                    "initialization failed",
                    ex);
            }
        }

        private IntPtr TaskbarRecoveryV7WndProc(
            IntPtr hwnd,
            int msg,
            IntPtr wParam,
            IntPtr lParam,
            ref bool handled)
        {
            if (_taskbarRecoveryV7Disabled ||
                _taskbarRecoveryV7Closed)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (msg == V6WmActivate &&
                    V6LowWord(wParam) == V6WaInactive)
                {
                    RecordTaskbarRecoveryV7Inactive();
                    return IntPtr.Zero;
                }

                if (msg != V6WmSysCommand)
                {
                    return IntPtr.Zero;
                }

                int command = unchecked(
                    (int)(wParam.ToInt64() & 0xFFF0L));

                if (command == V6ScRestore)
                {
                    RecordTaskbarRecoveryV7Restore(hwnd);
                    return IntPtr.Zero;
                }

                if (command != V6ScMinimize)
                {
                    return IntPtr.Zero;
                }

                if (!ShouldSuppressTaskbarRecoveryV7LateMinimize(
                        hwnd,
                        out V7Decision decision))
                {
                    if (decision.HasSyntheticProtection)
                    {
                        WriteTaskbarRecoveryV6Log(
                            "V7_SC_MINIMIZE_ALLOWED",
                            decision.Describe());
                    }

                    return IntPtr.Zero;
                }

                _taskbarRecoveryV7SuppressedCount++;

                WriteTaskbarRecoveryV6Log(
                    "V7_LATE_NATIVE_SC_MINIMIZE_SUPPRESSED",
                    decision.Describe() +
                    $"; suppressedCount=" +
                    $"{_taskbarRecoveryV7SuppressedCount}");

                // This is intentionally the only consumed message in the whole
                // recovery series. All of the following have been proven at the
                // same time: V6 already synthesized a minimize; a real restore
                // followed it; the HWND is currently restored/non-iconic; no new
                // taskbar click is inside the normal command envelope; and the
                // foreground is still 0/taskbar/StartAllBack rather than the
                // session or a foreign application.
                handled = true;
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                DisableTaskbarRecoveryV7(
                    $"WndProc failed for message 0x{msg:X4}",
                    ex);
                return IntPtr.Zero;
            }
        }

        private void RecordTaskbarRecoveryV7Inactive()
        {
            V6TaskbarHit hit = CaptureTaskbarRecoveryV6Hit();
            if (!hit.IsTaskList)
            {
                return;
            }

            bool rightDown = V6IsKeyDown(V6VkRightButton);
            bool middleDown = V6IsKeyDown(V6VkMiddleButton);
            if (rightDown || middleDown)
            {
                return;
            }

            _taskbarRecoveryV7LastTaskbarInactiveTimestamp =
                Stopwatch.GetTimestamp();
        }

        private void RecordTaskbarRecoveryV7Restore(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero ||
                !V6NativeIsIconic(hwnd))
            {
                return;
            }

            long lastSyntheticTimestamp;
            long lastSyntheticCandidateId;
            lock (_taskbarRecoveryV6StateLock)
            {
                lastSyntheticTimestamp =
                    _taskbarRecoveryV6LastSyntheticTimestamp;
                lastSyntheticCandidateId =
                    _taskbarRecoveryV6LastSyntheticCandidateId;
            }

            double sinceSyntheticMilliseconds =
                V6ElapsedMilliseconds(lastSyntheticTimestamp);

            if (lastSyntheticCandidateId <= 0 ||
                sinceSyntheticMilliseconds < 0 ||
                sinceSyntheticMilliseconds >
                    V7DuplicateWindowMilliseconds)
            {
                return;
            }

            _taskbarRecoveryV7ProtectedSyntheticCandidateId =
                lastSyntheticCandidateId;
            _taskbarRecoveryV7RestoreAfterSyntheticTimestamp =
                Stopwatch.GetTimestamp();

            WriteTaskbarRecoveryV6Log(
                "V7_RESTORE_AFTER_SYNTHETIC",
                $"protectedSyntheticCandidate=" +
                $"{lastSyntheticCandidateId}; " +
                $"sinceSyntheticMs={sinceSyntheticMilliseconds:F1}");
        }

        private bool ShouldSuppressTaskbarRecoveryV7LateMinimize(
            IntPtr hwnd,
            out V7Decision decision)
        {
            long lastSyntheticTimestamp;
            long lastSyntheticCandidateId;
            lock (_taskbarRecoveryV6StateLock)
            {
                lastSyntheticTimestamp =
                    _taskbarRecoveryV6LastSyntheticTimestamp;
                lastSyntheticCandidateId =
                    _taskbarRecoveryV6LastSyntheticCandidateId;
            }

            double sinceSyntheticMilliseconds =
                V6ElapsedMilliseconds(lastSyntheticTimestamp);
            double sinceRestoreMilliseconds =
                V6ElapsedMilliseconds(
                    _taskbarRecoveryV7RestoreAfterSyntheticTimestamp);
            double sinceTaskbarInactiveMilliseconds =
                V6ElapsedMilliseconds(
                    _taskbarRecoveryV7LastTaskbarInactiveTimestamp);

            bool iconic =
                hwnd != IntPtr.Zero &&
                V6NativeIsIconic(hwnd);

            IntPtr foreground =
                V6NativeGetForegroundWindow();
            IntPtr foregroundRoot =
                V6GetRootWindow(foreground);
            string foregroundClass =
                V6GetClassName(foregroundRoot);

            bool taskbarTransition =
                foreground == IntPtr.Zero ||
                foregroundRoot == IntPtr.Zero ||
                V6IsTaskbarRootClass(foregroundClass) ||
                V6IsStartAllBackTaskbarSurface(
                    foregroundClass);

            bool protectedSynthetic =
                lastSyntheticCandidateId > 0 &&
                _taskbarRecoveryV7ProtectedSyntheticCandidateId ==
                    lastSyntheticCandidateId &&
                _taskbarRecoveryV7RestoreAfterSyntheticTimestamp > 0;

            bool currentTaskbarCommand =
                sinceTaskbarInactiveMilliseconds >= 0 &&
                sinceTaskbarInactiveMilliseconds <=
                    V7CurrentTaskbarCommandEnvelopeMilliseconds;

            bool withinDuplicateWindow =
                sinceSyntheticMilliseconds >=
                    V7MinimumLateCommandMilliseconds &&
                sinceSyntheticMilliseconds <=
                    V7DuplicateWindowMilliseconds &&
                sinceRestoreMilliseconds >= 0 &&
                sinceRestoreMilliseconds <=
                    V7DuplicateWindowMilliseconds;

            decision = new V7Decision(
                lastSyntheticCandidateId,
                protectedSynthetic,
                sinceSyntheticMilliseconds,
                sinceRestoreMilliseconds,
                sinceTaskbarInactiveMilliseconds,
                currentTaskbarCommand,
                iconic,
                taskbarTransition,
                foreground,
                foregroundRoot,
                foregroundClass);

            return
                hwnd != IntPtr.Zero &&
                protectedSynthetic &&
                withinDuplicateWindow &&
                !currentTaskbarCommand &&
                !iconic &&
                taskbarTransition;
        }

        private void DisableTaskbarRecoveryV7(
            string context,
            Exception exception)
        {
            _taskbarRecoveryV7Disabled = true;

            WriteTaskbarRecoveryV6Log(
                "V7_DISABLED",
                $"context={context}; " +
                $"exception={exception.GetType().FullName}: " +
                exception.Message);
        }

        private void TaskbarRecoveryV7OnClosed(
            object? sender,
            EventArgs e)
        {
            _taskbarRecoveryV7Closed = true;

            try
            {
                if (_taskbarRecoveryV7HwndSource != null)
                {
                    _taskbarRecoveryV7HwndSource.RemoveHook(
                        TaskbarRecoveryV7WndProc);
                    _taskbarRecoveryV7HwndSource = null;
                }

                Closed -= TaskbarRecoveryV7OnClosed;
            }
            catch
            {
                // V7 cleanup must never affect application shutdown.
            }
        }

        private readonly struct V7Decision
        {
            public V7Decision(
                long syntheticCandidateId,
                bool hasSyntheticProtection,
                double sinceSyntheticMilliseconds,
                double sinceRestoreMilliseconds,
                double sinceTaskbarInactiveMilliseconds,
                bool currentTaskbarCommand,
                bool iconic,
                bool taskbarTransition,
                IntPtr foreground,
                IntPtr foregroundRoot,
                string foregroundClass)
            {
                SyntheticCandidateId = syntheticCandidateId;
                HasSyntheticProtection = hasSyntheticProtection;
                SinceSyntheticMilliseconds =
                    sinceSyntheticMilliseconds;
                SinceRestoreMilliseconds =
                    sinceRestoreMilliseconds;
                SinceTaskbarInactiveMilliseconds =
                    sinceTaskbarInactiveMilliseconds;
                CurrentTaskbarCommand = currentTaskbarCommand;
                Iconic = iconic;
                TaskbarTransition = taskbarTransition;
                Foreground = foreground;
                ForegroundRoot = foregroundRoot;
                ForegroundClass = foregroundClass;
            }

            public long SyntheticCandidateId { get; }
            public bool HasSyntheticProtection { get; }
            public double SinceSyntheticMilliseconds { get; }
            public double SinceRestoreMilliseconds { get; }
            public double SinceTaskbarInactiveMilliseconds { get; }
            public bool CurrentTaskbarCommand { get; }
            public bool Iconic { get; }
            public bool TaskbarTransition { get; }
            public IntPtr Foreground { get; }
            public IntPtr ForegroundRoot { get; }
            public string ForegroundClass { get; }

            public string Describe() =>
                $"syntheticCandidate={SyntheticCandidateId}; " +
                $"protected={HasSyntheticProtection}; " +
                $"sinceSyntheticMs={SinceSyntheticMilliseconds:F1}; " +
                $"sinceRestoreMs={SinceRestoreMilliseconds:F1}; " +
                $"sinceTaskbarInactiveMs=" +
                $"{SinceTaskbarInactiveMilliseconds:F1}; " +
                $"currentTaskbarCommand={CurrentTaskbarCommand}; " +
                $"iconic={Iconic}; " +
                $"taskbarTransition={TaskbarTransition}; " +
                $"foreground=0x{Foreground.ToInt64():X}; " +
                $"root=0x{ForegroundRoot.ToInt64():X}" +
                $"[{ForegroundClass}]";
        }
    }
}
