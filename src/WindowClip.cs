using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

internal sealed class WindowClip : IDisposable
{
    private readonly Target target;
    private IntPtr original;
    private bool applied;
    internal WindowClip(Target target, Rectangle local)
    {
        this.target = target;
        if (!target.SameWindow()) throw new InvalidOperationException("裁剪前目标窗口失效");
        original = CreateRectRgn(0, 0, 0, 0);
        if (original == IntPtr.Zero) throw new Win32Exception();
        if (GetWindowRgn(target.Hwnd, original) == 0)
        {
            DeleteObject(original);
            original = IntPtr.Zero;
        }
        var region = CreateRectRgn(local.Left, local.Top, local.Right, local.Bottom);
        if (region == IntPtr.Zero || SetWindowRgn(target.Hwnd, region, true) == 0)
        {
            if (region != IntPtr.Zero) DeleteObject(region);
            Dispose();
            throw new Win32Exception();
        }
        applied = true; // Windows owns region after SetWindowRgn succeeds.
    }
    internal Rectangle LocalBounds()
    {
        Native.Rect box;
        if (GetWindowRgnBox(target.Hwnd, out box) == 0) throw new InvalidOperationException("无法读取实际裁剪区域");
        return box.ToRectangle();
    }
    internal bool Restore()
    {
        if (!applied) return true;
        if (!target.SameWindow()) return false;
        if (SetWindowRgn(target.Hwnd, original, true) == 0) return false;
        var hadRegion = original != IntPtr.Zero;
        original = IntPtr.Zero;
        applied = false;
        Native.Rect box;
        return (GetWindowRgnBox(target.Hwnd, out box) != 0) == hadRegion;
    }
    public void Dispose()
    {
        Restore();
        if (original != IntPtr.Zero) { DeleteObject(original); original = IntPtr.Zero; }
    }
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("user32.dll")] private static extern int GetWindowRgnBox(IntPtr hwnd, out Native.Rect box);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool repaint);
}
