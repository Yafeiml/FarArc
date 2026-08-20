using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using _1RM.Utils.WindowsApi;

namespace _1RM.View.Host
{
    public partial class TabWindowView
    {
        private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        private static readonly int TaskbarButtonCreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");

        private DispatcherTimer? _taskbarRepairTimer;
        private HwndSource? _taskbarRepairHwndSource;
        private string _taskbarRepairReason = "unknown";

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _myHandle = new WindowInteropHelper(this).Handle;
            _taskbarRepairHwndSource = HwndSource.FromHwnd(_myHandle);
            _taskbarRepairHwndSource?.AddHook(TaskbarRepairWndProc);

            _taskbarRepairTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _taskbarRepairTimer.Tick += TaskbarRepairTimerOnTick;

            Activated += TaskbarRepairOnActivated;
            Deactivated += TaskbarRepairOnDeactivated;
            StateChanged += TaskbarRepairOnStateChanged;
            IsVisibleChanged += TaskbarRepairOnIsVisibleChanged;
            Closed += TaskbarRepairOnClosed;

            // Let WPF finish creating the native window and let the taskbar process
            // its normal creation notifications before explicitly confirming the tab.
            ScheduleTaskbarRepair("SourceInitialized", 500);
        }

        private void TaskbarRepairOnActivated(object? sender, EventArgs e)
        {
            // Delaying is important. Replacement taskbars may update their internal
            // task-item model after WPF raises Activated; repairing immediately can
            // therefore be overwritten by the taskbar's later focus transition.
            ScheduleTaskbarRepair("Activated", 250);
        }

        private void TaskbarRepairOnDeactivated(object? sender, EventArgs e)
        {
            // The Win11/StartAllBack failure is most often observed after the session
            // window loses activation while remaining visible, so repair after that
            // transition as well as after activation.
            ScheduleTaskbarRepair("Deactivated", 400);
        }

        private void TaskbarRepairOnStateChanged(object? sender, EventArgs e)
        {
            if (WindowState != WindowState.Minimized)
            {
                ScheduleTaskbarRepair($"StateChanged:{WindowState}", 300);
            }
        }

        private void TaskbarRepairOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                ScheduleTaskbarRepair("IsVisibleChanged:Visible", 350);
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

            Activated -= TaskbarRepairOnActivated;
            Deactivated -= TaskbarRepairOnDeactivated;
            StateChanged -= TaskbarRepairOnStateChanged;
            IsVisibleChanged -= TaskbarRepairOnIsVisibleChanged;
            Closed -= TaskbarRepairOnClosed;
        }

        private void ScheduleTaskbarRepair(string reason, int delayMilliseconds)
        {
            if (IsClosing || IsClosed || _taskbarRepairTimer == null)
            {
                return;
            }

            _taskbarRepairReason = reason;
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
                return;
            }

            // Keep WPF's managed state correct, then explicitly re-register the HWND.
            // The assignment alone is intentionally not relied upon: when the value is
            // already true it does not invoke WPF's ShowInTaskbar change handler.
            ShowInTaskbar = true;
            TaskbarWindowRepair.TryRegister(_myHandle, IsActive, _taskbarRepairReason);
        }

        private IntPtr TaskbarRepairWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if ((TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage) ||
                (TaskbarButtonCreatedMessage != 0 && msg == TaskbarButtonCreatedMessage))
            {
                ScheduleTaskbarRepair(
                    msg == TaskbarCreatedMessage ? "TaskbarCreated" : "TaskbarButtonCreated",
                    750);
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RegisterWindowMessage(string message);
    }
}
