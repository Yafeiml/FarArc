using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using FarArc.Service;
using FarArc.Service.Locality;
using FarArc.Utils;
using Shawn.Utils.Wpf.Controls;
using Stylet;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Shawn.Utils;
using Shawn.Utils.Wpf;
using FarArc.View.Host.ProtocolHosts;

namespace FarArc.View.Host
{
    public partial class TabWindowView
    {
        public const double TITLE_BAR_HEIGHT = 30;

        protected readonly TabWindowViewModel Vm;
        public string Token => Vm.Token;

        private IntPtr _myHandle = IntPtr.Zero;
        private static readonly bool IsWindows11OrLater = CheckIsWindows11OrLater();



        public TabWindowView()
        {
            InitializeComponent();
            Vm = new TabWindowViewModel(this);
            DataContext = Vm;

            this.MinWidth = this.MinHeight = 300;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.WindowStyle = WindowStyle.SingleBorderWindow;

            Focusable = true;
            this.Loaded += (_, _) =>
            {
                InitWindowSizeOnLoaded();
                TimerInitOnLoaded();
                _myHandle = new WindowInteropHelper(this).Handle;
                Keyboard.Focus(this);

                var myHwndSource = System.Windows.Interop.HwndSource.FromHwnd(_myHandle);
                myHwndSource?.AddHook(new HwndSourceHook(AdditionalWndProc));

                // remember window size when size changed
                SizeChanged += (_, _) =>
                {
                    if (this.WindowState == WindowState.Normal)
                    {
                        IoC.Get<LocalityService>().TabWindowWidth = this.ActualWidth;
                        IoC.Get<LocalityService>().TabWindowHeight = this.ActualHeight;
                    }
                    SimpleLogHelper.DebugInfo($"(Window size changed) Tab size change to:W = {this.ActualWidth}, H = {this.ActualHeight}");
                };

                // remember window pos when size changed
                OnDragEnd += () =>
                {
                    IoC.Get<LocalityService>().TabWindowTop = this.Top;
                    IoC.Get<LocalityService>().TabWindowLeft = this.Left;
                };


                StateChanged += delegate
                {
                    if (this.WindowState == WindowState.Minimized)
                    {
                        Vm?.SelectedItem?.Content?.ToggleAutoResize(false);
                        return;
                    }

                    if (Vm.SelectedItem?.Content.CanResizeNow() != true)
                    {
                        return;
                    }
                    Vm?.SelectedItem?.Content?.ToggleAutoResize(true);
                    IoC.Get<LocalityService>().TabWindowState = this.WindowState;
                    SimpleLogHelper.DebugInfo($"(Window state changed) Tab size change to:W = {this.ActualWidth}, H = {this.ActualHeight}");
                };


                Closing += (_, args) =>
                {
                    if (this.GetViewModel().Items.Count > 0
                        && App.ExitingFlag == false
                        && IoC.Get<ConfigurationService>().General.ConfirmBeforeClosingSession == true
                        && false == MessageBoxHelper.Confirm(IoC.Translate("Are you sure you want to close the connection?"), ownerViewModel: Vm))
                    {
                        args.Cancel = true;
                    }
                };


                Closed += (_, _) =>
                {
                    TimerDispose();
                    try
                    {
                        var ids = Vm.Items.Select(x => x.Content.ConnectionId).ToArray();
                        if (ids.Length > 0)
                        {
                            IoC.Get<SessionControlService>().CloseProtocolHostAsync(ids);
                        }
                        Vm?.Dispose();
                    }
                    finally
                    {
                        DataContext = null;
                        System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet = System.Diagnostics.Process.GetCurrentProcess().MinWorkingSet;
                    }
                };


                this.Activated += (_, _) =>
                {
                    this.StopFlashingWindow();
                    // Ensure taskbar icon is visible when window is activated (fixes Win11 issue where taskbar icon disappears when window loses focus)
                    this.ShowInTaskbar = true;
                };

                if (IoC.Get<LocalityService>().TabWindowState != System.Windows.WindowState.Minimized)
                {
                    this.WindowState = IoC.Get<LocalityService>().TabWindowState;
                }
            };
        }

        private IntPtr AdditionalWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_STYLECHANGING = 0x007C;
            const int WM_GETICON = 0x007F;
            const int GWL_STYLE = -16;
            const int ICON_BIG = 1;
            const int ICON_SMALL = 0;
            const uint WS_VISIBLE = 0x10000000;

            // WPF WindowChromeWorker temporarily removes WS_VISIBLE while handling
            // WM_SIZE, WM_SETTEXT and WM_SETICON. Windows 11 taskbar replacements
            // such as StartAllBack can observe the transient hidden state and fail
            // to add this top-level window back to the taskbar. Keep the native
            // window visible while a live session window is logically visible.
            // WM_STYLECHANGING explicitly allows the receiver to amend styleNew.
            if (msg == WM_STYLECHANGING
                && wParam.ToInt64() == GWL_STYLE
                && lParam != IntPtr.Zero
                && IsWindows11OrLater
                && App.ExitingFlag == false
                && IsClosing == false
                && Visibility == System.Windows.Visibility.Visible
                && IsVisible
                && Vm.Items.Count > 0)
            {
                try
                {
                    var style = Marshal.PtrToStructure<StyleStruct>(lParam);
                    if ((style.StyleOld & WS_VISIBLE) != 0
                        && (style.StyleNew & WS_VISIBLE) == 0
                        && (style.StyleOld ^ style.StyleNew) == WS_VISIBLE)
                    {
                        style.StyleNew |= WS_VISIBLE;
                        Marshal.StructureToPtr(style, lParam, false);
                    }
                }
                catch (Exception ex)
                {
                    // A WndProc hook must never take down a live remote session.
                    SimpleLogHelper.Warning($"TabWindow taskbar compatibility hook failed: {ex.Message}");
                }
            }

            if (!IsWindows11OrLater && msg == WM_GETICON)
            {
                // Preserve the original Windows 10 workaround that prevents the
                // volume mixer from adopting per-session icons. On Windows 11,
                // let DefWindowProc/Shell resolve the icon so taskbar replacements
                // can rebuild the window's taskbar entry reliably.
                int size = wParam.ToInt32();
                int dpi = lParam.ToInt32();
                if (dpi == 0 && (size == ICON_SMALL || size == ICON_BIG))
                {
                    handled = true;
                }
            }

            return IntPtr.Zero;
        }

        private static bool CheckIsWindows11OrLater()
        {
            var osVersion = Environment.OSVersion.Version;
            return osVersion.Major >= 10 && osVersion.Build >= 22000;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StyleStruct
        {
            public uint StyleOld;
            public uint StyleNew;
        }

        private void InitWindowSizeOnLoaded()
        {
            var screenEx = ScreenInfoEx.GetCurrentScreenBySystemPosition(ScreenInfoEx.GetMouseSystemPosition());
            var leftTopOfCurrentScreen = new Point(screenEx.VirtualWorkingArea.X, screenEx.VirtualWorkingArea.Y);
            var rightBottomOfCurrentScreen = new Point(screenEx.VirtualWorkingArea.X + screenEx.VirtualWorkingArea.Width, screenEx.VirtualWorkingArea.Y + screenEx.VirtualWorkingArea.Height);
            if (WindowState == System.Windows.WindowState.Maximized)
            {

            }
            else
            {
                this.WindowStartupLocation = WindowStartupLocation.Manual;
                this.Width = IoC.Get<LocalityService>().TabWindowWidth;
                this.Height = IoC.Get<LocalityService>().TabWindowHeight;
                // check current screen size
                if (IoC.Get<LocalityService>().TabWindowTop <= leftTopOfCurrentScreen.Y - TITLE_BAR_HEIGHT                                // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowTop > rightBottomOfCurrentScreen.Y                                             // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowLeft > rightBottomOfCurrentScreen.X                                            // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth < leftTopOfCurrentScreen.X              // check if the title bar outside the screen.
                    || IoC.Get<LocalityService>().TabWindowTop + IoC.Get<LocalityService>().TabWindowHeight / 2 < leftTopOfCurrentScreen.Y          // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowTop + IoC.Get<LocalityService>().TabWindowHeight / 2 > rightBottomOfCurrentScreen.Y      // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth / 2 < leftTopOfCurrentScreen.X          // check if the center of tab window local in current screen
                    || IoC.Get<LocalityService>().TabWindowLeft + IoC.Get<LocalityService>().TabWindowWidth / 2 > rightBottomOfCurrentScreen.X      // check if the center of tab window local in current screen
                   )
                {
                    // default width & height
                    if (this.Width >= screenEx.VirtualWorkingArea.Width)
                        this.Width = Math.Min(screenEx.VirtualWorkingArea.Width * 0.8, this.Width * 0.8);
                    if (this.Height >= screenEx.VirtualWorkingArea.Height)
                        this.Height = Math.Min(screenEx.VirtualWorkingArea.Height * 0.8, this.Height * 0.8);
                    // default top & left
                    this.Top = screenEx.VirtualWorkingAreaCenter.Y - this.Height / 2;
                    this.Left = screenEx.VirtualWorkingAreaCenter.X - this.Width / 2;
                }
                else
                {
                    this.Top = IoC.Get<LocalityService>().TabWindowTop;
                    this.Left = IoC.Get<LocalityService>().TabWindowLeft;
                }
            }
        }

        protected virtual void TabablzControl_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Vm?.SelectedItem?.Content != null)
            {
                this.Icon = IoC.Get<ConfigurationService>().General.ShowSessionIconInSessionWindow ?
                    Vm.SelectedItem.Content.ProtocolServer.IconImg : null;
            }
        }

        public TabWindowViewModel GetViewModel()
        {
            return Vm;
        }

        public Size GetTabContentSize(bool withoutBorderColor)
        {
            var size = new Size(800, 600);
            Execute.OnUIThreadSync(() =>
            {
                if (!this.IsLoaded || TabablzControl == null) return;
                Debug.Assert(this.Resources["TabContentBorderWithColor"] != null);
                Debug.Assert(this.Resources["TabContentBorderWithOutColor"] != null);
                var tabContentBorderWithColor = (Thickness)this.Resources["TabContentBorderWithColor"];
                var tabContentBorderWithOutColor = (Thickness)this.Resources["TabContentBorderWithOutColor"];

                var screenEx = ScreenInfoEx.GetCurrentScreen(this);
                double actualWidth = TabablzControl.ActualWidth;
                double actualHeight = this.WindowState == WindowState.Maximized ? screenEx.VirtualWorkingArea.Height : TabablzControl.ActualHeight;
                double border1 = withoutBorderColor ? tabContentBorderWithOutColor.Left + tabContentBorderWithOutColor.Right : tabContentBorderWithColor.Left + tabContentBorderWithColor.Right;
                double border2 = withoutBorderColor ? tabContentBorderWithOutColor.Top + tabContentBorderWithOutColor.Bottom : tabContentBorderWithColor.Top + tabContentBorderWithColor.Bottom;
                size.Width = actualWidth - border1;
                size.Height = actualHeight - TITLE_BAR_HEIGHT - border2;
            });
            return size;
        }



        /// <summary>
        /// double click title bar to Maximized
        /// </summary>
        public override void WinTitleBar_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (Vm.SelectedItem?.Content.CanResizeNow() == false)
                    return;
            }
            base.WinTitleBar_OnPreviewMouseDown(sender, e);
        }



        private void TabablzControl_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var t = sender.GetType();
            SimpleLogHelper.DebugWarning(t);
            // focus to be on the integrated exe after clicking on the WPF window.
            RunForIntegrate();
        }

        public override void WinTitleBar_OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            var isDragging = _isDragging;
            base.WinTitleBar_OnPreviewMouseMove(sender, e);
            if (Vm?.SelectedItem?.Content?.GetProtocolHostType() != ProtocolHostType.Integrate)
            {
                // When stop dragging, focus on the integrated exe
                if (isDragging && !_isDragging)
                {
                    // focus to be on the integrated exe after drag on the WPF window.
                    RunForIntegrate();
                }
            }
        }
    }
}
