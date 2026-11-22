using System;
using System.Diagnostics;

internal static class ExternalWindowSetup
{
    private static bool IsClient(IntPtr hwnd)
    {
        if (Native.Class(hwnd) != "Chrome_WidgetWin_1" || Native.Title(hwnd) != "QQ") return false;
        try
        {
            using (var process = Process.GetProcessById(Native.Pid(hwnd)))
                return process.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase) && process.SessionId == Target.Session;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static int Inventory()
    {
        Native.EnumWindows((hwnd, data) => {
            if (!IsClient(hwnd)) return true;
            Console.WriteLine("QQ main candidate hwnd={0} pid={1} visible={2} minimized={3} enabled={4} outer={5}",
                hwnd.ToInt64(), Native.Pid(hwnd), Native.Visible(hwnd), Native.IsIconic(hwnd),
                Native.IsWindowEnabled(hwnd), Native.Bounds(hwnd));
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    internal static int Restore(long value)
    {
        var hwnd = new IntPtr(value);
        if (!IsClient(hwnd) || !Native.IsWindowEnabled(hwnd)) return 2;
        // Setup only. The experiment never reaches this command.
        return Native.ShowWindowAsync(hwnd, 4) ? 0 : 2; // SW_SHOWNOACTIVATE
    }

    internal static int Hide(long value)
    {
        var hwnd = new IntPtr(value);
        return IsClient(hwnd) && Native.ShowWindowAsync(hwnd, 0) ? 0 : 2;
    }

    internal static int Minimize(long value)
    {
        var hwnd = new IntPtr(value);
        return IsClient(hwnd) && Native.ShowWindowAsync(hwnd, Native.ShowMinimizedNoActivate) ? 0 : 2;
    }
}
