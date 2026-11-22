using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

internal static partial class Native
{
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr wp, IntPtr lp,
        uint flags, uint timeout, out UIntPtr response);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint value);
    internal delegate void WinEvent(IntPtr hook, uint id, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public Keyboard Keyboard;
        [FieldOffset(0)] public Mouse Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    internal static IntPtr FocusWindow(IntPtr target)
    {
        uint pid;
        var thread = GetWindowThreadProcessId(target, out pid);
        var info = new ThreadInfo { Size = Marshal.SizeOf(typeof(ThreadInfo)) };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }
    internal static bool IsDedicatedEdit(IntPtr target, IntPtr handle)
    {
        if (handle == IntPtr.Zero || !IsChild(target, handle) || Pid(handle) != Pid(target)) return false;
        var name = Class(handle);
        return name.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
    }
    internal static IntPtr RootAt(int x, int y)
    {
        return GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), 2);
    }
    internal static Size ClientSize(IntPtr hwnd)
    {
        Rect rect;
        if (!GetClientRect(hwnd, out rect)) throw new Win32Exception();
        return rect.ToRectangle().Size;
    }
    internal static string TimedMessage(IntPtr receiver, uint msg, ushort value)
    {
        UIntPtr response;
        SetLastError(0);
        var result = SendMessageTimeout(receiver, msg, new UIntPtr(value), new IntPtr(msg == 0x102 ? 1 : 0),
            0x23, 300, out response); // SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT
        var error = Marshal.GetLastWin32Error();
        return String.Format("delivered={0} lresult=0x{1:X} win32={2}", result != IntPtr.Zero, response.ToUInt64(),
            result == IntPtr.Zero ? error : 0);
    }
    private static Input Key(ushort vk, ushort scan, uint flags)
    {
        return new Input { Type = 1, Data = new InputData { Keyboard = new Keyboard { Vk = vk, Scan = scan, Flags = flags } } };
    }
    private static void Inject(IntPtr target, Input[] keys)
    {
        if (GetForegroundWindow() != target || IsIconic(target))
            throw new InvalidOperationException("系统输入仅允许发往当前 QQ 前台窗口");
        foreach (var modifier in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
            if (GetAsyncKeyState(modifier) < 0) throw new InvalidOperationException("检测到用户正在按住修饰键");
        var count = SendInput((uint)keys.Length, keys, Marshal.SizeOf(typeof(Input)));
        if (count != keys.Length) throw new InvalidOperationException("SendInput accepted=" + count + "/" + keys.Length);
    }
    internal static void TypeUnicode(IntPtr target, string text)
    {
        var keys = new Input[2 * text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            if (Char.IsControl(text[index])) throw new ArgumentException("control character");
            keys[2 * index] = Key(0, text[index], 4);
            keys[2 * index + 1] = Key(0, text[index], 6);
        }
        Inject(target, keys);
    }
    internal static void PressEnter(IntPtr target, ushort modifier)
    {
        var enter = new[] { Key(13, 0, 0), Key(13, 0, 2) };
        Inject(target, modifier == 0 ? enter : new[] {
            Key(modifier, 0, 0), enter[0], enter[1], Key(modifier, 0, 2) });
    }
    internal static void Backspace(IntPtr target, int count)
    {
        var keys = new Input[2 * count];
        for (int i = 0; i < count; i++) { keys[2*i] = Key(8, 0, 0); keys[2*i+1] = Key(8, 0, 2); }
        Inject(target, keys);
    }
    internal static void SelectAllText(IntPtr target)
    {
        Inject(target, new[] { Key(0x11, 0, 0), Key(0x41, 0, 0), Key(0x41, 0, 2), Key(0x11, 0, 2) });
    }
    internal static void ClickVerifiedPoint(IntPtr target, int x, int y, bool context)
    {
        if (GetForegroundWindow() != target || RootAt(x, y) != target)
            throw new InvalidOperationException("自聊行未在前台 QQ 的可点击区域");
        Point original;
        if (!GetCursorPos(out original) || !SetCursorPos(x, y)) throw new Win32Exception();
        try
        {
            if (RootAt(x, y) != target) throw new InvalidOperationException("光标下目标发生变化");
            var down = new Input { Data = new InputData { Mouse = new Mouse { Flags = context ? 8u : 2u } } };
            var up = new Input { Data = new InputData { Mouse = new Mouse { Flags = context ? 16u : 4u } } };
            Inject(target, context ? new[] { down, up } : new[] { down, up, down, up });
            Thread.Sleep(230);
        }
        finally
        {
            Point now;
            if (GetCursorPos(out now) && now.X == x && now.Y == y) SetCursorPos(original.X, original.Y);
        }
    }
}

internal sealed class ForegroundTrace : IDisposable
{
    private readonly Native.WinEvent callback;
    private readonly IntPtr hook;
    private readonly ConcurrentQueue<Tuple<IntPtr, uint, int>> events = new ConcurrentQueue<Tuple<IntPtr, uint, int>>();
    internal ForegroundTrace()
    {
        callback = (h, id, hwnd, obj, child, thread, time) => events.Enqueue(Tuple.Create(hwnd, time, Native.Pid(hwnd)));
        hook = Native.SetWinEventHook(3, 3, IntPtr.Zero, callback, 0, 0, 0); // WINEVENT_OUTOFCONTEXT
        if (hook == IntPtr.Zero) throw new Win32Exception();
    }
    internal int Count { get { return events.Count; } }
    internal string Summary()
    {
        return String.Join(",", System.Linq.Enumerable.Select(events,
            e => e.Item1.ToInt64() + "@" + e.Item2 + "/pid=" + e.Item3));
    }
    public void Dispose() { Native.UnhookWinEvent(hook); GC.KeepAlive(callback); }
}
