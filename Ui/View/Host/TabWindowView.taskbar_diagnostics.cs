using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using _1RM.Service;

namespace _1RM.View.Host
{
    /// <summary>
    /// Observation-only instrumentation for the Windows 11 taskbar issue.
    /// It never calls ITaskbarList, ShowWindow, Activate, SetForegroundWindow,
    /// changes ShowInTaskbar, changes WindowState, or marks a Win32 message handled.
    /// </summary>
    public partial class TabWindowView
    {
        private const int DiagGwlStyle = -16;
        private const int DiagGwlExStyle = -20;
        private const uint DiagGwOwner = 4;
        private const uint DiagGaRoot = 2;

        private const int WmSize = 0x0005;
        private const int WmActivate = 0x0006;
        private const int WmSetFocus = 0x0007;
        private const int WmKillFocus = 0x0008;
        private const int WmShowWindow = 0x0018;
        private const int WmActivateApp = 0x001C;
        private const int WmMouseActivate = 0x0021;
        private const int WmWindowPosChanged = 0x0047;
        private const int WmStyleChanging = 0x007C;
        private const int WmStyleChanged = 0x007D;
        private const int WmNcActivate = 0x0086;
        private const int WmSysCommand = 0x0112;

        private static readonly object DiagnosticFileLock = new object();
        private static long _diagnosticSequence;
        private static readonly int TaskbarCreatedMessage = NativeRegisterWindowMessage("TaskbarCreated");
        private static readonly int TaskbarButtonCreatedMessage = NativeRegisterWindowMessage("TaskbarButtonCreated");

        private HwndSource? _diagnosticHwndSource;
        private string? _diagnosticLogPath;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _myHandle = new WindowInteropHelper(this).Handle;
            InitializeDiagnosticLogPath();

            _diagnosticHwndSource = HwndSource.FromHwnd(_myHandle);
            _diagnosticHwndSource?.AddHook(DiagnosticWndProc);

            Activated += DiagnosticOnActivated;
            Deactivated += DiagnosticOnDeactivated;
            StateChanged += DiagnosticOnStateChanged;
            IsVisibleChanged += DiagnosticOnVisibilityChanged;
            Closed += DiagnosticOnClosed;

            WriteDiagnostic("EVENT SourceInitialized", string.Empty);
        }

        private void InitializeDiagnosticLogPath()
        {
            try
            {
                string directory = Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".logs");
                Directory.CreateDirectory(directory);
                _diagnosticLogPath = Path.Combine(directory, $"TaskbarDiagnostics-{Environment.ProcessId}.log");
            }
            catch
            {
                _diagnosticLogPath = Path.Combine(Path.GetTempPath(), $"1Remote-TaskbarDiagnostics-{Environment.ProcessId}.log");
            }

            try
            {
                string header =
                    $"# 1Remote 1.2.1 taskbar diagnostics — observation only{Environment.NewLine}" +
                    $"# PID={Environment.ProcessId}; OS={Environment.OSVersion}; Is64Bit={Environment.Is64BitProcess}; Exe={Environment.ProcessPath}{Environment.NewLine}" +
                    $"# This instrumentation does not modify taskbar registration, activation, window state, styles, or ownership.{Environment.NewLine}";
                lock (DiagnosticFileLock)
                {
                    File.AppendAllText(_diagnosticLogPath, header, Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never affect application behaviour.
            }
        }

        private void DiagnosticOnActivated(object? sender, EventArgs e) =>
            WriteDiagnostic("EVENT Activated", string.Empty);

        private void DiagnosticOnDeactivated(object? sender, EventArgs e) =>
            WriteDiagnostic("EVENT Deactivated", string.Empty);

        private void DiagnosticOnStateChanged(object? sender, EventArgs e) =>
            WriteDiagnostic("EVENT StateChanged", $"newManagedState={WindowState}");

        private void DiagnosticOnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) =>
            WriteDiagnostic("EVENT IsVisibleChanged", $"old={e.OldValue}; new={e.NewValue}");

        private void DiagnosticOnClosed(object? sender, EventArgs e)
        {
            WriteDiagnostic("EVENT Closed", string.Empty);

            if (_diagnosticHwndSource != null)
            {
                _diagnosticHwndSource.RemoveHook(DiagnosticWndProc);
                _diagnosticHwndSource = null;
            }

            Activated -= DiagnosticOnActivated;
            Deactivated -= DiagnosticOnDeactivated;
            StateChanged -= DiagnosticOnStateChanged;
            IsVisibleChanged -= DiagnosticOnVisibilityChanged;
            Closed -= DiagnosticOnClosed;
        }

        private IntPtr DiagnosticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            string? name = GetMessageName(msg);
            if (name != null)
            {
                WriteDiagnostic("MSG " + name, DescribeMessage(msg, wParam, lParam));
            }

            // Never set handled: this build only observes messages.
            return IntPtr.Zero;
        }

        private static string? GetMessageName(int msg)
        {
            if (TaskbarCreatedMessage != 0 && msg == TaskbarCreatedMessage)
                return "TaskbarCreated";
            if (TaskbarButtonCreatedMessage != 0 && msg == TaskbarButtonCreatedMessage)
                return "TaskbarButtonCreated";

            return msg switch
            {
                WmSize => "WM_SIZE",
                WmActivate => "WM_ACTIVATE",
                WmSetFocus => "WM_SETFOCUS",
                WmKillFocus => "WM_KILLFOCUS",
                WmShowWindow => "WM_SHOWWINDOW",
                WmActivateApp => "WM_ACTIVATEAPP",
                WmMouseActivate => "WM_MOUSEACTIVATE",
                WmWindowPosChanged => "WM_WINDOWPOSCHANGED",
                WmStyleChanging => "WM_STYLECHANGING",
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

            try
            {
                switch (msg)
                {
                    case WmSize:
                        return $"type={DescribeSizeType(unchecked((int)wp))}; clientWidth={LowWord(lp)}; clientHeight={HighWord(lp)}";
                    case WmActivate:
                        return $"state={DescribeActivateState(LowWord(wp))}; minimized={HighWord(wp) != 0}; other={DescribeWindow(lParam)}";
                    case WmSetFocus:
                        return $"previous={DescribeWindow(wParam)}";
                    case WmKillFocus:
                        return $"next={DescribeWindow(wParam)}";
                    case WmShowWindow:
                        return $"show={wp != 0}; status=0x{unchecked((ulong)lp):X}";
                    case WmActivateApp:
                        return $"active={wp != 0}; otherThreadId={unchecked((uint)lp)}";
                    case WmMouseActivate:
                        return $"topLevel={DescribeWindow(wParam)}; hitTest={LowWord(lp)}; mouseMessage=0x{HighWord(lp):X4}";
                    case WmWindowPosChanged:
                        if (lParam != IntPtr.Zero)
                        {
                            NativeWindowPos pos = Marshal.PtrToStructure<NativeWindowPos>(lParam);
                            return $"insertAfter={DescribeWindow(pos.HwndInsertAfter)}; x={pos.X}; y={pos.Y}; cx={pos.Cx}; cy={pos.Cy}; flags=0x{pos.Flags:X8}";
                        }
                        break;
                    case WmStyleChanging:
                    case WmStyleChanged:
                        return $"index={unchecked((int)wp)}; styleStruct=0x{unchecked((ulong)lp):X}";
                    case WmNcActivate:
                        return $"active={wp != 0}; region=0x{unchecked((ulong)lp):X}";
                    case WmSysCommand:
                        int command = unchecked((int)(wp & 0xFFF0));
                        return $"command={DescribeSysCommand(command)}; raw=0x{unchecked((ulong)wp):X}; source=0x{unchecked((ulong)lp):X}";
                }
            }
            catch (Exception ex)
            {
                return $"decodeError={ex.GetType().Name}; wParam=0x{unchecked((ulong)wp):X}; lParam=0x{unchecked((ulong)lp):X}";
            }

            return $"wParam=0x{unchecked((ulong)wp):X}; lParam=0x{unchecked((ulong)lp):X}";
        }

        private void WriteDiagnostic(string source, string details)
        {
            string? path = _diagnosticLogPath;
            if (string.IsNullOrWhiteSpace(path))
                return;

            long sequence = Interlocked.Increment(ref _diagnosticSequence);
            string line;

            try
            {
                IntPtr hwnd = _myHandle;
                IntPtr foreground = NativeGetForegroundWindow();
                IntPtr owner = hwnd != IntPtr.Zero ? NativeGetWindow(hwnd, DiagGwOwner) : IntPtr.Zero;
                long style = hwnd != IntPtr.Zero ? GetWindowLongPtr(hwnd, DiagGwlStyle).ToInt64() : 0;
                long exStyle = hwnd != IntPtr.Zero ? GetWindowLongPtr(hwnd, DiagGwlExStyle).ToInt64() : 0;

                string cursor = "unavailable";
                if (NativeGetCursorPos(out NativePoint point))
                {
                    IntPtr child = NativeWindowFromPoint(point);
                    IntPtr root = child != IntPtr.Zero ? NativeGetAncestor(child, DiagGaRoot) : IntPtr.Zero;
                    cursor = $"point=({point.X},{point.Y}); child={DescribeWindow(child)}; root={DescribeWindow(root)}";
                }

                NativeWindowPlacement placement = new NativeWindowPlacement
                {
                    Length = Marshal.SizeOf<NativeWindowPlacement>(),
                };
                bool hasPlacement = hwnd != IntPtr.Zero && NativeGetWindowPlacement(hwnd, ref placement);

                line =
                    $"seq={sequence}; time={DateTime.Now:O}; source={source}; {details}; " +
                    $"hwnd={DescribeWindow(hwnd)}; managedState={WindowState}; managedActive={IsActive}; managedVisible={IsVisible}; showInTaskbar={ShowInTaskbar}; " +
                    $"nativeVisible={(hwnd != IntPtr.Zero && NativeIsWindowVisible(hwnd))}; iconic={(hwnd != IntPtr.Zero && NativeIsIconic(hwnd))}; zoomed={(hwnd != IntPtr.Zero && NativeIsZoomed(hwnd))}; " +
                    $"owner={DescribeWindow(owner)}; style=0x{unchecked((ulong)style):X}; exStyle=0x{unchecked((ulong)exStyle):X}; " +
                    $"placement={(hasPlacement ? DescribeShowCommand(placement.ShowCommand) : "unavailable")}; foreground={DescribeWindow(foreground)}; cursor={cursor}";
            }
            catch (Exception ex)
            {
                line = $"seq={sequence}; time={DateTime.Now:O}; source={source}; snapshotError={ex}";
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (DiagnosticFileLock)
                    {
                        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Logging must never affect application behaviour.
                }
            });
        }

        private static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "0x0";

            var classBuffer = new StringBuilder(256);
            string className = NativeGetClassName(hwnd, classBuffer, classBuffer.Capacity) > 0 ? classBuffer.ToString() : "?";
            uint processId = 0;
            string processName = "?";

            try
            {
                NativeGetWindowThreadProcessId(hwnd, out processId);
                if (processId != 0)
                {
                    using Process process = Process.GetProcessById(unchecked((int)processId));
                    processName = process.ProcessName;
                }
            }
            catch
            {
                // Best effort only.
            }

            return $"0x{hwnd.ToInt64():X}[class={className}; process={processName}; pid={processId}]";
        }

        private static int LowWord(long value) => unchecked((ushort)(value & 0xFFFF));
        private static int HighWord(long value) => unchecked((ushort)((value >> 16) & 0xFFFF));

        private static string DescribeActivateState(int value) => value switch
        {
            0 => "WA_INACTIVE",
            1 => "WA_ACTIVE",
            2 => "WA_CLICKACTIVE",
            _ => value.ToString(),
        };

        private static string DescribeSizeType(int value) => value switch
        {
            0 => "SIZE_RESTORED",
            1 => "SIZE_MINIMIZED",
            2 => "SIZE_MAXIMIZED",
            3 => "SIZE_MAXSHOW",
            4 => "SIZE_MAXHIDE",
            _ => value.ToString(),
        };

        private static string DescribeSysCommand(int value) => value switch
        {
            0xF000 => "SC_SIZE",
            0xF010 => "SC_MOVE",
            0xF020 => "SC_MINIMIZE",
            0xF030 => "SC_MAXIMIZE",
            0xF060 => "SC_CLOSE",
            0xF090 => "SC_MOUSEMENU",
            0xF100 => "SC_KEYMENU",
            0xF120 => "SC_RESTORE",
            0xF130 => "SC_TASKLIST",
            _ => $"0x{value:X4}",
        };

        private static string DescribeShowCommand(int value) => value switch
        {
            0 => "SW_HIDE",
            1 => "SW_SHOWNORMAL",
            2 => "SW_SHOWMINIMIZED",
            3 => "SW_SHOWMAXIMIZED",
            4 => "SW_SHOWNOACTIVATE",
            5 => "SW_SHOW",
            6 => "SW_MINIMIZE",
            7 => "SW_SHOWMINNOACTIVE",
            8 => "SW_SHOWNA",
            9 => "SW_RESTORE",
            10 => "SW_SHOWDEFAULT",
            11 => "SW_FORCEMINIMIZE",
            _ => value.ToString(),
        };

        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) =>
            IntPtr.Size == 8 ? NativeGetWindowLongPtr64(hwnd, index) : new IntPtr(NativeGetWindowLong32(hwnd, index));

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCommand;
            public NativePoint MinPosition;
            public NativePoint MaxPosition;
            public NativeRect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeWindowPos
        {
            public IntPtr Hwnd;
            public IntPtr HwndInsertAfter;
            public int X;
            public int Y;
            public int Cx;
            public int Cy;
            public uint Flags;
        }

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NativeRegisterWindowMessage(string message);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr NativeGetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
        private static extern IntPtr NativeGetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr NativeGetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int NativeGetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsIconic(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsZoomed")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeIsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeGetWindowPlacement(IntPtr hwnd, ref NativeWindowPlacement placement);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool NativeGetCursorPos(out NativePoint point);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
        private static extern IntPtr NativeWindowFromPoint(NativePoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor")]
        private static extern IntPtr NativeGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NativeGetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
        private static extern uint NativeGetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
