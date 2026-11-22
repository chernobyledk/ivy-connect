using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

internal static partial class Native
{
    internal const uint NoActivate = 0x10, NoZOrder = 4, NoOwnerZOrder = 0x200, AsyncPosition = 0x4000;
    internal const int ShowNormal = 1, ShowMinimizedNoActivate = 7;
    internal static readonly IntPtr Topmost = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Placement
    {
        public int Length, Flags, Show;
        public Point Min, Max;
        public Rect Normal;
    }
    internal delegate bool EnumCallback(IntPtr hwnd, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, Menu, MoveSize, Caret;
        public Rect CaretRect;
    }
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumCallback cb, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumCallback cb, IntPtr data);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref ThreadInfo info);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int WindowStyle(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder b, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder b, int n);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement p);
    [DllImport("user32.dll")] internal static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement p);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attr, out int result, int size);

    internal static int Pid(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return (int)pid;
    }
    internal static string Title(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }
    internal static string Class(IntPtr hwnd)
    {
        var text = new StringBuilder(128);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }
    internal static Rectangle Bounds(IntPtr hwnd)
    {
        Rect rect;
        if (!GetWindowRect(hwnd, out rect)) throw new InvalidOperationException("无法读取窗口矩形");
        return rect.ToRectangle();
    }
    internal static Placement GetPlacement(IntPtr hwnd)
    {
        var p = new Placement { Length = Marshal.SizeOf(typeof(Placement)) };
        if (!GetWindowPlacement(hwnd, ref p)) throw new InvalidOperationException("无法读取原始窗口状态");
        return p;
    }
    internal static bool Visible(IntPtr hwnd)
    {
        int cloaked;
        return IsWindowVisible(hwnd) && (DwmGetWindowAttribute(hwnd, 14, out cloaked, 4) != 0 || cloaked == 0);
    }
    internal static bool Above(IntPtr overlay, IntPtr target)
    {
        for (var window = GetWindow(target, 3); window != IntPtr.Zero; window = GetWindow(window, 3))
            if (window == overlay) return true;
        return false;
    }
    internal static bool IsTopmost(IntPtr hwnd) { return (WindowStyle(hwnd, -20) & 8) != 0; }
    internal static string ForegroundLabel(IntPtr target)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == target) return "QQ target";
        try { using (var p = Process.GetProcessById(Pid(hwnd))) return p.ProcessName; }
        catch (ArgumentException) { return "(none)"; }
    }
    internal static bool HasChildFocus(IntPtr hwnd)
    {
        uint pid;
        var thread = GetWindowThreadProcessId(hwnd, out pid);
        var info = new ThreadInfo { Size = Marshal.SizeOf(typeof(ThreadInfo)) };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) && info.Active == hwnd && IsChild(hwnd, info.Focus);
    }
}

internal sealed class Target
{
    internal readonly IntPtr Hwnd;
    internal readonly int Pid;
    internal readonly string Title, WindowClass;
    internal readonly DateTime Start;

    private Target(IntPtr hwnd, Process process)
    {
        Hwnd = hwnd;
        Pid = process.Id;
        Title = Native.Title(hwnd);
        WindowClass = Native.Class(hwnd);
        Start = process.StartTime;
    }
    internal static List<Target> List(bool includeHidden = false)
    {
        var result = new List<Target>();
        Native.EnumWindows((hwnd, data) => {
            if ((!includeHidden && !Native.Visible(hwnd)) || Native.Title(hwnd).Length == 0) return true;
            try
            {
                using (var p = Process.GetProcessById(Native.Pid(hwnd)))
                    if (p.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase)
                            && p.SessionId == Session && (Native.IsIconic(hwnd) || Native.Bounds(hwnd).Width >= 60))
                        result.Add(new Target(hwnd, p));
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    internal static readonly int Session = Process.GetCurrentProcess().SessionId;
    internal static Target FromKnownHandle(long value)
    {
        var hwnd = new IntPtr(value);
        using (var p = Process.GetProcessById(Native.Pid(hwnd)))
        {
            if (!p.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase) || p.SessionId != Session)
                throw new InvalidOperationException("指定 HWND 不属于本会话的 QQ");
            return new Target(hwnd, p);
        }
    }
    internal bool SameWindow()
    {
        if (Native.Pid(Hwnd) != Pid || Native.Class(Hwnd) != WindowClass) return false;
        try { using (var process = Process.GetProcessById(Pid)) return process.StartTime == Start; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
    public override string ToString()
    {
        var r = Native.Bounds(Hwnd);
        return String.Format("{0}   QQ.exe PID {1}   HWND {2}   {3}×{4}   {5}",
            Title, Pid, Hwnd.ToInt64(), r.Width, r.Height,
            Native.IsIconic(Hwnd) ? "最小化" : Native.GetForegroundWindow() == Hwnd ? "前台" : "后台");
    }
}
