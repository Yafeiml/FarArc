using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace Shawn.Utils.WpfResources.Theme.Styles
{
    public abstract class WindowChromeBase : WindowBase
    {
        private static readonly bool IsWindows11OrLater = CheckIsWindows11OrLater();

        /// <summary>
        /// Opts a top-level window into preserving WS_VISIBLE while WPF's
        /// WindowChromeWorker performs transient style updates.
        /// </summary>
        protected virtual bool PreserveTaskbarVisibilityDuringChromeUpdates => false;

        // https://developercommunity.visualstudio.com/t/overflow-exception-in-windowchrome/167357%EF%BC%8C%E6%99%9A%E4%BA%9B%E6%88%91%E6%9C%89%E7%A9%BA%E5%86%8D%E4%BB%94%E7%BB%86%E7%9C%8B%E7%9C%8B%E8%BF%99%E4%B8%AA%E8%A7%A3%E5%86%B3%E6%96%B9%E6%A1%88%E6%98%AF%E5%95%A5%E5%9B%9E%E4%BA%8B
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ((HwndSource)PresentationSource.FromVisual(this)).AddHook(HookProc);
        }

        private IntPtr HookProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_STYLECHANGING = 0x007C;
            const int GWL_STYLE = -16;
            const uint WS_VISIBLE = 0x10000000;

            // WindowChromeWorker temporarily removes WS_VISIBLE while handling
            // messages such as WM_SIZE, WM_SETTEXT and WM_SETICON. Windows 11
            // can observe that transient state and lose the taskbar entry.
            if (msg == WM_STYLECHANGING
                && wParam.ToInt64() == GWL_STYLE
                && lParam != IntPtr.Zero
                && IsWindows11OrLater
                && PreserveTaskbarVisibilityDuringChromeUpdates)
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
                    // A native window hook must never take down a live window.
                    SimpleLogHelper.Warning($"{GetType().Name} taskbar compatibility hook failed: {ex.Message}");
                }
            }

            if (msg == 0x0084 /*WM_NCHITTEST*/ )
            {
                // This prevents a crash in WindowChromeWorker._HandleNCHitTest
                try
                {
                    lParam.ToInt32();
                }
                catch (OverflowException)
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
    }
}
