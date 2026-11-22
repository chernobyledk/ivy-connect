using System;
using System.Linq;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class NativeChatLayout
{
    internal static int Change(Target panel, string mode, Action<string> log, Rectangle? restore)
    {
        if (mode != "classic" && mode != "efficiency") return 2;
        if (restore.HasValue && (mode != "efficiency" || restore.Value.Width < 60 || restore.Value.Height < 60)) return 2;
        if (!panel.SameWindow()) throw new InvalidOperationException("QQ 主面板失效");
        var expected = mode == "classic" ? "切换为经典模式" : "切换为效率模式";
        var root = AutomationElement.FromHandle(panel.Hwnd);
        var found = root.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, expected),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (found.Count != 1 || !found[0].Current.IsEnabled)
        {
            log("QQ native layout button not uniquely available; call=no");
            return 3;
        }
        var invoke = Controls.Pattern<InvokePattern>(found[0], InvokePattern.Pattern);
        if (invoke == null) { log("QQ native layout InvokePattern unavailable; call=no"); return 3; }
        if (mode == "classic" && Native.GetForegroundWindow() != panel.Hwnd)
            throw new InvalidOperationException("切换经典模式前需要 QQ 主面板在前台");
        invoke.Invoke();
        log("QQ toggled via native button InvokePattern target=" + mode + " hwnd=" + panel.Hwnd.ToInt64());
        if (restore.HasValue)
        {
            Thread.Sleep(700);
            var r = restore.Value;
            if (!panel.SameWindow() || !Native.SetWindowPos(panel.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Native.NoActivate | Native.NoZOrder | Native.AsyncPosition))
                throw new InvalidOperationException("原 QQ 面板尺寸恢复失败");
            Thread.Sleep(350);
            log("QQ original rectangle restored=" + (Native.Bounds(panel.Hwnd) == r) + " outer=" + Native.Bounds(panel.Hwnd));
        }
        return 0;
    }
    internal static int OpenKnown(Target panel, string heldMarker, Action<string> log, bool pointer, bool context)
    {
        if (String.IsNullOrWhiteSpace(heldMarker) || heldMarker.Length < 10) return 2;
        var panelBounds = Native.Bounds(panel.Hwnd);
        var work = Screen.FromHandle(panel.Hwnd).WorkingArea;
        if (!Native.Visible(panel.Hwnd) || !work.IntersectsWith(panelBounds))
        {
            Native.ShowWindowAsync(panel.Hwnd, 4);
            Native.SetWindowPos(panel.Hwnd, IntPtr.Zero, work.Left + 100, work.Top + 100,
                panelBounds.Width, Math.Min(panelBounds.Height, work.Height - 120),
                Native.NoActivate | Native.NoZOrder | Native.AsyncPosition);
            Thread.Sleep(450);
            log("native classic panel restored on screen for setup; visible=" + Native.Visible(panel.Hwnd) +
                " rect=" + Native.Bounds(panel.Hwnd));
        }
        var root = AutomationElement.FromHandle(panel.Hwnd);
        var matches = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
            .Where(el => el.Current.ControlType == ControlType.Text &&
                el.Current.Name.IndexOf(heldMarker, StringComparison.Ordinal) >= 0).Take(2).ToArray();
        if (matches.Length != 1) { log("known self preview not unique; call=no"); return 3; }
        var item = matches[0];
        for (int i = 0; i < 7 && item != null; i++, item = TreeWalker.RawViewWalker.GetParent(item))
        {
            if (!item.Current.ClassName.Split(' ').Contains("recent-contact-item")) continue;
            if (pointer)
            {
                var box = item.Current.BoundingRectangle;
                if (box.IsEmpty) return 3;
                int x = (int)(box.X + box.Width / 2), y = (int)(box.Y + box.Height / 2);
                var at = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                var id = item.GetRuntimeId();
                for (int n = 0; at != null && n < 10; n++, at = TreeWalker.RawViewWalker.GetParent(at))
                {
                    if (!at.GetRuntimeId().SequenceEqual(id)) continue;
                    Native.ClickVerifiedPoint(panel.Hwnd, x, y, context);
                    log("native QQ setup: " + (context ? "right-click" : "double-click") + " on verified self row");
                    Thread.Sleep(600);
                    return 0;
                }
                log("native self row hit-test unavailable; call=no");
                return 3;
            }
            var invoke = Controls.Pattern<InvokePattern>(item, InvokePattern.Pattern);
            if (invoke == null || !item.Current.IsEnabled) return 3;
            invoke.Invoke();
            log("native recent-chat row InvokePattern called on exact test preview");
            Thread.Sleep(600);
            return 0;
        }
        log("no safe native recent-chat row match; call=no");
        return 3;
    }
    internal static int Inspect(Target panel, string heldMarker, Action<string> log)
    {
        log(String.Format("QQ panel hwnd={0} pid={1} visible={2} titleLength={3} foreground={4} bounds={5}",
            panel.Hwnd.ToInt64(), panel.Pid, Native.Visible(panel.Hwnd), Native.Title(panel.Hwnd).Length,
            Native.GetForegroundWindow() == panel.Hwnd, Native.Bounds(panel.Hwnd)));
        var root = AutomationElement.FromHandle(panel.Hwnd);
        var elements = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
        var all = elements.Where(el => el.Current.ControlType == ControlType.Button &&
            (el.Current.Name == "切换为经典模式" || el.Current.Name == "切换为效率模式"));
        foreach (var toggle in all) log("layout button=" + toggle.Current.Name);
        Native.EnumWindows((hwnd, data) => {
            if (!Native.Visible(hwnd)) return true;
            var pid = Native.Pid(hwnd);
            try
            {
                using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                    if (proc.ProcessName.Equals("QQ", StringComparison.OrdinalIgnoreCase))
                        log("visible QQ top-level hwnd=" + hwnd.ToInt64() + " titleLength=" + Native.Title(hwnd).Length +
                            " class=" + Native.Class(hwnd) + " rect=" + Native.Bounds(hwnd));
            }
            catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
        var commands = elements.Where(el => el.Current.ControlType == ControlType.MenuItem);
        foreach (var command in commands)
        {
            var name = command.Current.Name;
            if (name.Contains("窗口") && name.Length < 25) log("native window menu item=" + name);
        }
        var matching = elements.Where(el => el.Current.Name.IndexOf(heldMarker, StringComparison.Ordinal) >= 0).Take(4).ToArray();
        log("known self preview matches=" + matching.Length + " nodes=" + elements.Length);
        foreach (var item in matching)
        {
            var node = item;
            for (int level = 0; node != null && level < 5; level++, node = TreeWalker.RawViewWalker.GetParent(node))
            {
                var p = node.Current;
                log("self-preview ancestry " + level + " type=" + p.ControlType.ProgrammaticName +
                    " class=" + p.ClassName + " invoke=" + (Controls.Pattern<InvokePattern>(node, InvokePattern.Pattern) != null) +
                    " rect=" + p.BoundingRectangle);
            }
        }
        return 0;
    }
}
