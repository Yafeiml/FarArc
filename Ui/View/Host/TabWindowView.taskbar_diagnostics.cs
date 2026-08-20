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
    /// Diagnostic-only instrumentation for the Windows 11 / replacement-taskbar issue.
    ///
    /// This file deliberately does not call ITaskbarList, ShowInTaskbar, ShowWindow,
    /// Activate, SetForegroundWindow, or change WindowState. It only observes selected
    /// Win32 messages and records native/managed window state for one reproduction.
    /// </summary>
    public partial class TabWindowView
    {
        private const int DiagGwlStyle = -16;
        private const int DiagGwlExStyle = -20;
        private const uint DiagGwOwner = 4;
        private const uint DiagGaRoot = 2;

        private const int DiagWmSize = 0x0005;
        private const int DiagWmActivate = 0x0006;
        private const int DiagWmSetFocus = 0x0007;
        private const int DiagWmKillFocus = 0x0008;
        private const int DiagWmShowWindow = 0x0018;
        private const int DiagWmActivateApp = 0x001C;
        private const int DiagWmMouseActivate = 0x0021;
        private const int DiagWmWindowPosChanging = 0x0046;
        private const int DiagWmWindowPosChanged = 0x0047;
        private const int DiagWmStyleChanging = 0x007C;
        private const int DiagWmStyleChanged = 0x007D;
        private const int DiagWmNcActivate = 0x0086;
        private const int DiagWmSysCommand = 0x0112;

        private static readonly object TaskbarDiagnosticFileLock = new object();
        private static long _taskbarDiagnosticSequence;
        private static readonly int DiagTaskbarCreatedMessage = DiagRegisterWindowMessage("TaskbarCreated");
        private static readonly int DiagTaskbarButtonCreatedMessage = DiagRegisterWindowMessage("TaskbarButtonCreated");

        private HwndSource? _taskbarDiagnosticHwndSource;
        private string? _taskbarDiagnosticLogPath;

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _myHandle = new WindowInteropHelper(this).Handle;
            _taskbarDiagnosticHwndSource = HwndSource.FromHwnd(_myHandle);
            _taskbarDiagnosticHwndSource?.AddHook(TaskbarDiagnosticWndProc);

            Activated += TaskbarDiagnosticOnActivated;
            Deactivated += TaskbarDiagnosticOnDeactivated;
            StateChanged += TaskbarDiagnosticOnStateChanged;
            IsVisibleChanged += TaskbarDiagnosticOnIsVisibleChanged;
            Closed += TaskbarDiagnosticOnClosed;

            InitializeTaskbarDiagnosticLog();
            WriteTaskbarDiagnostic("EVENT SourceInitialized", string.Empty);
        }

        private void InitializeTaskbarDiagnosticLog()
        {
            try
            {
                string logDirectory = Path.Combine(AppPathHelper.Instance.BaseDirPathForLocality, ".logs");
                Directory.CreateDirectory(logDirectory);
                _taskbarDiagnosticLogPath = Path.Combine(logDirectory, $"TaskbarDiagnostics-{Environment.ProcessId}.log");
            }
            catch
            {
                _taskbarDiagnosticLogPath = Path.Combine(Path.GetTempPath(), $"1Remote-TaskbarDiagnostics-{Environment.ProcessId}.log");
            }

            string header =
                $"# 1Remote taskbar diagnostics (observation only){Environment.NewLine}" +
                $"# PID={Environment.ProcessId}; OS={Environment.OSVersion}; Is64BitProcess={Environment.Is64BitProcess}; " +
                $"Executable={Environment.ProcessPath}{Environment.NewLine}" +
                $"# No taskbar registration, activation, minimization, style, owner, or WindowState changes are performed by this instrumentation.{Environment.NewLine}";

            try
            {
                lock (TaskbarDiagnosticFileLock)
                {
                    File.AppendAllText(_taskbarDiagnosticLogPath, header, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never affect application behaviour.
            }
        }

        private void TaskbarDiagnosticOnActivated(object? sender, EventArgs e)
        {
            WriteTaskbarDiagnostic("EVENT Activated", string.Empty);
        }

        private void TaskbarDiagnosticOnDeactivated(object? sender, EventArgs e)
        {
            WriteTaskbarDiagnostic("EVENT Deactivated", string.Empty);
        }

        private void TaskbarDiagnosticOnStateChanged(object? sender, EventArgs e)
        {
            WriteTaskbarDiagnostic("EVENT StateChanged", $"newManagedState={WindowState}");
        }

        private void TaskbarDiagnosticOnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            WriteTaskbarDiagnostic("EVENT IsVisibleChanged", $"old={e.OldValue}; new={e.NewValue}");
        }

        private void TaskbarDiagnosticOnClosed(object? sender, EventArgs e)
        {
            WriteTaskbarDiagnostic("EVENT Closed", string.Empty);

            if (_taskbarDiagnosticHwndSource != null)
            {
                _taskbarDiagnosticHwndSource.RemoveHook(TaskbarDiagnosticWndProc);
                _taskbarDiagnosticHwndSource = null;
            }

            Activated -= TaskbarDiagnosticOnActivated;
            Deactivated -= TaskbarDiagnosticOnDeactivated;
            StateChanged -= TaskbarDiagnosticOnStateChanged;
            IsVisibleChanged -= TaskbarDiagnosticOnIsVisibleChanged;
            Closed -= TaskbarDiagnosticOnClosed;
        }

        private IntPtr TaskbarDiagnosticWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            string? messageName = GetTaskbarDiagnosticMessageName(msg);
            if (messageName != null)
            {
                string details = DescribeTaskbarDiagnosticMessage(msg, wParam, lParam);
                WriteTaskbarDiagnostic($"MSG {messageName}", details);
            }

            // Observation only. Never mark a message handled.
            return IntPtr.Zero;
        }

        private static string? GetTaskbarDiagnosticMessageName(int msg)
        {
            if (DiagTaskbarCreatedMessage != 0 && msg == DiagTaskbarCreatedMessage)
                return "TaskbarCreated";
            if (DiagTaskbarButtonCreatedMessage != 0 && msg == DiagTaskbarButtonCreatedMessage)
                return "TaskbarButtonCreated";

            return msg switch
            {
                DiagWmSize => "WM_SIZE",
                DiagWmActivate => "WM_ACTIVATE",
                DiagWmSetFocus => "WM_SETFOCUS",
                DiagWmKillFocus => "WM_KILLFOCUS",
                DiagWmShowWindow => "WM_SHOWWINDOW",
                DiagWmActivateApp => "WM_ACTIVATEAPP",
                DiagWmMouseActivate => "WM_MOUSEACTIVATE",
                DiagWmWindowPosChanging => "WM_WINDOWPOSCHANGING",
                DiagWmWindowPosChanged => "WM_WINDOWPOSCHANGED",
                DiagWmStyleChanging => "WM_STYLECHANGING",
                DiagWmStyleChanged => "WM_STYLECHANGED",
                DiagWmNcActivate => "WM_NCACTIVATE",
                DiagWmSysCommand => "WM_SYSCOMMAND",
                _ => null,
            };
        }

        private static string DescribeTaskbarDiagnosticMessage(int msg, IntPtr wParam, IntPtr lParam)
        {
            long wp = wParam.ToInt64();
            long lp = lParam.ToInt64();

            try
            {
                switch (msg)
                {
                    case DiagWmSize:
                        return $"type={DescribeSizeType(unchecked((int)wp))}; clientWidth={LowWord(lp)}; clientHeight={HighWord(lp)}";

                    case DiagWmActivate:
                        return $"state={DescribeActivateState(LowWord(wp))}; minimized={HighWord(wp) != 0}; other={DescribeNativeWindow(lParam)}";

                    case DiagWmSetFocus:
                        return $"previous={DescribeNativeWindow(wParam)}";

                    case DiagWmKillFocus:
                        return $"next={DescribeNativeWindow(wParam)}";

                    case DiagWmShowWindow:
                        return $"show={wp != 0}; status=0x{unchecked((ulong)lp):X}";

                    case DiagWmActivateApp:
                        return $"active={wp != 0}; otherThreadId={unchecked((uint)lp)}";

                    case DiagWmMouseActivate:
                        return $"topLevel={DescribeNativeWindow(wParam)}; hitTest={LowWord(lp)}; mouseMessage=0x{HighWord(lp):X4}";

                    case DiagWmWindowPosChanging:
                    case DiagWmWindowPosChanged:
                        if (lParam != IntPtr.Zero)
                        {
                            DiagWindowPos pos = Marshal.PtrToStructure<DiagWindowPos>(lParam);
                            return $"insertAfter={DescribeNativeWindow(pos.HwndInsertAfter)}; x={pos.X}; y={pos.Y}; cx={pos.Cx}; cy={pos.Cy}; flags=0x{pos.Flags:X8}";
                        }
                        break;

                    case DiagWmStyleChanging:
                    case DiagWmStyleChanged:
                        return $"index={unchecked((int)wp)}; styleStruct=0x{unchecked((ulong)lp):X}";

                    case DiagWmNcActivate:
                        return $"active={wp != 0}; region=0x{unchecked((ulong)lp):X}";

                    case DiagWmSysCommand:
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

        private void WriteTaskbarDiagnostic(string source, string details)
        {
            string? path = _taskbarDiagnosticLogPath;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            long sequence = Interlocked.Increment(ref _taskbarDiagnosticSequence);
            string snapshot;

            try
            {
                IntPtr hwnd = _myHandle;
                IntPtr foreground = DiagGetForegroundWindow();
                IntPtr owner = hwnd != IntPtr.Zero ? DiagGetWindow(hwnd, DiagGwOwner) : IntPtr.Zero;
                long style = hwnd != IntPtr.Zero ? DiagGetWindowLongPtr(hwnd, DiagGwlStyle).ToInt64() : 0;
                long exStyle = hwnd != IntPtr.Zero ? DiagGetWindowLongPtr(hwnd, DiagGwlExStyle).ToInt64() : 0;

                string cursorDescription = "unavailable";
                if (DiagGetCursorPos(out DiagPoint point))
                {
                    IntPtr cursorWindow = DiagWindowFromPoint(point);
                    IntPtr cursorRoot = cursorWindow != IntPtr.Zero ? DiagGetAncestor(cursorWindow, DiagGaRoot) : IntPtr.Zero;
                    cursorDescription = $"point=({point.X},{point.Y}); child={DescribeNativeWindow(cursorWindow)}; root={DescribeNativeWindow(cursorRoot)}";
                }

                DiagWindowPlacement placement = new DiagWindowPlacement
                {
                    Length = Marshal.SizeOf<DiagWindowPlacement>(),
                };
                bool hasPlacement = hwnd != IntPtr.Zero && DiagGetWindowPlacement(hwnd, ref placement);

                snapshot =
                    $"seq={sequence}; time={DateTime.Now:O}; source={source}; {details}; " +
                    $"hwnd={DescribeNativeWindow(hwnd)}; managedState={WindowState}; managedActive={IsActive}; " +
                    $"managedVisible={IsVisible}; showInTaskbar={ShowInTaskbar}; " +
                    $"nativeVisible={(hwnd != IntPtr.Zero && DiagIsWindowVisible(hwnd))}; iconic={(hwnd != IntPtr.Zero && DiagIsIconic(hwnd))}; " +
                    $"zoomed={(hwnd != IntPtr.Zero && DiagIsZoomed(hwnd))}; owner={DescribeNativeWindow(owner)}; " +
                    $"style=0x{unchecked((ulong)style):X}; exStyle=0x{unchecked((ulong)exStyle):X}; " +
                    $"placement={(hasPlacement ? DescribeShowCommand(placement.ShowCommand) : "unavailable")}; " +
                    $"foreground={DescribeNativeWindow(foreground)}; cursor={cursorDescription}";
            }
            catch (Exception ex)
            {
                snapshot = $"seq={sequence}; time={DateTime.Now:O}; source={source}; snapshotError={ex}";
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (TaskbarDiagnosticFileLock)
                    {
                        File.AppendAllText(path, snapshot + Environment.NewLine, Encoding.UTF8);
                    }
                }
                catch
                {
                    // Diagnostics must never affect application behaviour.
                }
            });
        }

        private static string DescribeNativeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return "0x0";

            string className = GetNativeWindowClassName(hwnd);
            string processName = "?";
            uint processId = 0;

            try
            {
                DiagGetWindowThreadProcessId(hwnd, out processId);
                if (processId != 0)
                {
                    using Process process = Process.GetProcessById(unchecked((int)processId));
                    processName = process.ProcessName;
                }
            }
            catch
            {
                // Best-effort diagnostic metadata only.
            }

            return $"0x{hwnd.ToInt64():X}[class={className}; process={processName}; pid={processId}]";
        }

        private static string GetNativeWindowClassName(IntPtr hwnd)
        {
            var buffer = new StringBuilder(256);
            return DiagGetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "?";
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

        private static IntPtr DiagGetWindowLongPtr(IntPtr hwnd, int index)
        {
            return IntPtr.Size == 8
                ? DiagGetWindowLongPtr64(hwnd, index)
                : new IntPtr(DiagGetWindowLong32(hwnd, index));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiagPoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiagRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiagWindowPlacement
        {
            public int Length;
            public int Flags;
            public int ShowCommand;
            public DiagPoint MinPosition;
            public DiagPoint MaxPosition;
            public DiagRect NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DiagWindowPos
        {
            public IntPtr Hwnd;
            public IntPtr HwndInsertAfter;
            public int X;
            public int Y;
            public int Cx;
            public int Cy;
            public uint Flags;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DiagRegisterWindowMessage(string message);

        [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
        private static extern IntPtr DiagGetForegroundWindow();

        [DllImport("user32.dll", EntryPoint = "GetWindow", SetLastError = true)]
        private static extern IntPtr DiagGetWindow(IntPtr hwnd, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr DiagGetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int DiagGetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiagIsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsIconic")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiagIsIconic(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "IsZoomed")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiagIsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowPlacement", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiagGetWindowPlacement(IntPtr hwnd, ref DiagWindowPlacement placement);

        [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DiagGetCursorPos(out DiagPoint point);

        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
        private static extern IntPtr DiagWindowFromPoint(DiagPoint point);

        [DllImport("user32.dll", EntryPoint = "GetAncestor")]
        private static extern IntPtr DiagGetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int DiagGetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
        private static extern uint DiagGetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    }
}
