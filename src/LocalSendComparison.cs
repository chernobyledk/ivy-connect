using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

internal static class LocalSendComparison
{
    [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsHungAppWindow(IntPtr hwnd);

    internal static int InspectWindow()
    {
        var target = SelfTestIdentity.OpenTarget();
        var identity = SelfTestIdentity.Read(target);
        var ui = Fresh(identity);
        var rect = Native.Bounds(target.Hwnd);
        var front = Native.GetForegroundWindow();
        var popup = GetLastActivePopup(target.Hwnd);
        Console.WriteLine("QQ enabled={0} iconic={1} visible={2} rect={3} foreground={4} popup={5} popupVisible={6} hung={7} WM_NULL={8}",
            Native.IsWindowEnabled(target.Hwnd), Native.IsIconic(target.Hwnd), Native.Visible(target.Hwnd), rect,
            DescribeHwnd(front), DescribeHwnd(popup), popup != IntPtr.Zero && Native.Visible(popup),
            IsHungAppWindow(target.Hwnd), Native.TimedMessage(target.Hwnd, 0, 0));
        Console.WriteLine("normal placement rectangle=" + Native.GetPlacement(target.Hwnd).Normal.ToRectangle());
        foreach (var point in new[] {
            new Point(rect.Left + rect.Width / 2, rect.Top + 32),
            new Point(rect.Left + 3 * rect.Width / 4, rect.Top + rect.Height / 2),
            new Point(rect.Left + 3 * rect.Width / 4, rect.Bottom - 48)
        })
            Console.WriteLine("screen hit ({0},{1}) {2}", point.X, point.Y, DescribeHwnd(Native.RootAt(point.X, point.Y)));
        Native.EnumWindows((hwnd, data) => {
            if (hwnd == target.Hwnd) return false;
            if (!Native.Visible(hwnd) || Native.IsIconic(hwnd)) return true;
            var overlap = Rectangle.Intersect(rect, Native.Bounds(hwnd));
            if (overlap.Width > 0 && overlap.Height > 0)
                Console.WriteLine("higher HWND {0} overlap={1}x{2}", DescribeHwnd(hwnd), overlap.Width, overlap.Height);
            return true;
        }, IntPtr.Zero);
        Console.WriteLine("emptyDraft={0} editorEnabled={1} buttonEnabled={2} visibleConnectionWarning={3}", ui.EmptyDraft(),
            ui.Editor != null && ui.Editor.Current.IsEnabled, ui.Button != null && ui.Button.Current.IsEnabled,
            ui.HasVisibleConnectionWarning());
        return 0;
    }

    internal static int Audit(string[] markers)
    {
        if (markers.Length == 0 || markers.Length > 30 || markers.Any(value => String.IsNullOrWhiteSpace(value))) return 2;
        var target = SelfTestIdentity.OpenTarget();
        var ui = Fresh(SelfTestIdentity.Read(target));
        var summary = ui.Messages(markers);
        Console.WriteLine("verified self chat; read-only; iconic={0} empty={1}", Native.IsIconic(target.Hwnd), ui.EmptyDraft());
        for (int i = 0; i < markers.Length; i++)
            Console.WriteLine("marker[{0}] evidence={1} matchingRows={2}", i + 1, summary.Evidence(markers[i]), summary.MarkerRows(markers[i]));
        return 0;
    }

    internal static int CaptureEmptyEditor()
    {
        var target = SelfTestIdentity.OpenTarget();
        var ui = Fresh(SelfTestIdentity.Read(target));
        if (!ui.EmptyDraft()) throw new SelfTestHalt("截图只限定在空白输入区");
        RequireForeground(target, ui.Editor);
        var area = ui.Editor.Current.BoundingRectangle;
        var width = Math.Min(280, (int)area.Width - 12);
        var height = Math.Min(100, (int)area.Height - 12);
        var origin = new Point((int)(area.Left + 6), (int)(area.Top + 6));
        var end = new Point(origin.X + width - 1, origin.Y + height - 1);
        if (width <= 10 || height <= 10 || Native.RootAt(origin.X, origin.Y) != target.Hwnd ||
            Native.RootAt(end.X, end.Y) != target.Hwnd || !ui.EmptyDraft())
            throw new SelfTestHalt("输入区未能复核至可安全截图");
        using (var bitmap = new Bitmap(width, height))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(origin, Point.Empty, bitmap.Size);
            var name = "R5-empty-editor-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".png";
            var filename = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs", name);
            using (var stream = new FileStream(filename, FileMode.CreateNew))
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("captured empty editor area {0}x{1}; no message history; file={2} centerColor={3}",
                width, height, name, bitmap.GetPixel(width / 2, height / 2).ToArgb().ToString("X8"));
        }
        return 0;
    }

    internal static int RefreshDisplay()
    {
        var target = SelfTestIdentity.OpenTarget();
        var identity = SelfTestIdentity.Read(target);
        var ui = Fresh(identity);
        if (!Native.IsWindowEnabled(target.Hwnd) || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
            throw new SelfTestHalt("QQ 当前未展开或不可操作，暂停显示刷新");
        if (!ui.EmptyDraft()) throw new SelfTestHalt("有草稿，暂停显示刷新");
        var placement = Native.GetPlacement(target.Hwnd);
        var original = Native.Bounds(target.Hwnd);
        try
        {
            if (!Native.ShowWindowAsync(target.Hwnd, Native.ShowMinimizedNoActivate))
                throw new InvalidOperationException("QQ 最小化请求未受理");
            for (int i = 0; i < 10 && !Native.IsIconic(target.Hwnd); i++) Thread.Sleep(50);
            Console.WriteLine("temporary minimize observed=" + Native.IsIconic(target.Hwnd));
            Thread.Sleep(250);
        }
        finally
        {
            if (target.SameWindow())
                Console.WriteLine("restore window placement=" + Native.SetWindowPlacement(target.Hwnd, ref placement));
        }
        Thread.Sleep(550);
        Console.WriteLine("QQ restored: iconic={0} sameRect={1} front={2}", Native.IsIconic(target.Hwnd),
            Native.Bounds(target.Hwnd) == original, Native.GetForegroundWindow() == target.Hwnd);
        return 0;
    }

    private static string DescribeHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        var pid = Native.Pid(hwnd);
        var owner = "unavailable";
        try { using (var process = Process.GetProcessById(pid)) owner = process.ProcessName; }
        catch (ArgumentException) { }
        return hwnd + "/" + owner + "/" + Native.Class(hwnd);
    }

    internal static int Run(string action, string marker)
    {
        if (!Regex.IsMatch(marker, @"\AR5-[ABC]-[0-9A-F]{4,8}\z") ||
            (action == "fill" && marker[3] != 'B') || (action == "submit" && marker[3] != 'C')) return 2;
        var runs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs");
        Directory.CreateDirectory(runs);
        var observation = action == "observe" ? "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) : "";
        var path = Path.Combine(runs, marker + "-" + action + observation + ".log");
        var target = SelfTestIdentity.OpenTarget();
        using (var mutex = new Mutex(false, @"Local\QQWindowLab.Continuous." + target.Hwnd.ToInt64()))
        {
            bool held;
            try { held = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return 2;
            try
            {
                using (var file = new StreamWriter(new FileStream(path, FileMode.CreateNew), new UTF8Encoding(false)) { AutoFlush = true })
                {
                    Action<string> log = line => {
                        file.WriteLine(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture) + " " + line);
                        Console.WriteLine(line);
                    };
                    try { return Measure(target, action, marker, log); }
                    catch (Exception ex) { log("stopped " + Experiment.Failure(ex)); return 1; }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static Controls Fresh(SelfTestIdentity identity)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                var ui = identity.Scope.RequireMatch();
                identity.Validate(ui.ReplyRows());
                return ui;
            }
            catch (ElementNotAvailableException)
            {
                if (i == 3) throw;
                Thread.Sleep(240);
            }
        }
    }

    private static int Measure(Target target, string action, string marker, Action<string> log)
    {
        if (action != "observe" && Native.IsIconic(target.Hwnd))
        {
            SelfTestIdentity.CheckProcess(target);
            log("foreground-only setup restore request=" + Native.ShowWindowAsync(target.Hwnd, 9));
            for (int i = 0; i < 25 && Native.IsIconic(target.Hwnd); i++) Thread.Sleep(100);
            Thread.Sleep(500);
        }
        var identity = SelfTestIdentity.Read(target);
        var ui = Fresh(identity);
        log("self-chat checked; qq=" + target.Hwnd + " rect=" + Native.Bounds(target.Hwnd) +
            " visible=" + Native.Visible(target.Hwnd) + " iconic=" + Native.IsIconic(target.Hwnd) +
            " foreground=" + (Native.GetForegroundWindow() == target.Hwnd) + " marker=" + marker);
        var baseline = ui.Messages(marker).MarkerRows(marker);
        log("initial matchingRows=" + baseline + " empty=" + ui.EmptyDraft());
        if (action == "observe") return Observe(identity, marker, baseline, log, 180);
        if (baseline != 0 || !ui.EmptyDraft()) throw new SelfTestHalt("输入框非空或编号已存在");
        if (ui.HasVisibleConnectionWarning()) throw new SelfTestHalt("QQ 显示连接断开或正在重连，停止发送");
        if (Native.GetForegroundWindow() != target.Hwnd)
        {
            log("foreground-only setup activation-request=" + Native.SetForegroundWindow(target.Hwnd));
            Thread.Sleep(850);
            ui = Fresh(identity);
        }
        RequireForeground(target, ui.Editor);
        var editor = MsaaProbe.FindMappedEditor(target, ui);
        if (editor == null)
        {
            log("foreground-only setup editor SetFocus");
            ui.Editor.SetFocus();
            Thread.Sleep(250);
            ui = Fresh(identity);
            RequireForeground(target, ui.Editor);
            editor = MsaaProbe.FindMappedEditor(target, ui);
        }
        if (editor == null || Native.GetForegroundWindow() != target.Hwnd)
            throw new SelfTestHalt("QQ 输入区未映射，请先点击本人会话输入框");
        if (!ui.EmptyDraft() || ui.HasVisibleConnectionWarning()) throw new SelfTestHalt("草稿或连接状态已变化");
        log("write-intent MSAA.put_accValue");
        var write = MsaaProbe.PutValue(editor, marker);
        log("write-return hr=0x" + write.ToString("X8"));
        Thread.Sleep(240);
        ui = Fresh(identity);
        if (ui.Draft() != marker) throw new SelfTestHalt("未确认精确测试草稿");
        log("write-confirmed exact=true");
        if (action == "fill")
        {
            log("submit=none; manual click required");
            return 0;
        }
        ui = Fresh(identity);
        var button = ui.Button;
        var legacy = ui.Legacy(button);
        if (ui.HasVisibleConnectionWarning() || ui.Draft() != marker || ui.Messages(marker).MarkerRows(marker) != 0 || button == null ||
            !button.Current.IsEnabled || legacy == null || legacy.CurrentRole() != 0x2B ||
            (legacy.CurrentState() & 1) != 0 || String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()) ||
            Native.GetForegroundWindow() != target.Hwnd)
            throw new SelfTestHalt("发送前草稿、按钮或前台发生变化");
        RequireForeground(target, button);
        log("submit-intent LegacyIAccessible.DoDefaultAction");
        try { log("submit-return hr=0x" + legacy.DoDefaultAction().ToString("X8")); }
        catch (Exception ex) { log("submit-return " + Experiment.Failure(ex)); }
        var result = Observe(identity, marker, 0, log, 12);
        if (result != 0) identity.ClearUnsubmittedDraft(marker, log);
        return result;
    }

    private static void RequireForeground(Target target, AutomationElement element)
    {
        if (!Native.IsWindowEnabled(target.Hwnd) || Native.GetForegroundWindow() != target.Hwnd ||
            Native.IsIconic(target.Hwnd) || element == null)
            throw new SelfTestHalt("前台对照要求 QQ 展开、可操作并且已经在前台");
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || Native.RootAt((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2)) != target.Hwnd)
            throw new SelfTestHalt("控件被其他窗口挡住，不能进行此项前台对照");
    }

    private static int Observe(SelfTestIdentity identity, string marker, int baseline, Action<string> log, int seconds)
    {
        var timer = Stopwatch.StartNew();
        bool exactDraftSeen = false;
        do
        {
            var ui = Fresh(identity);
            if (!exactDraftSeen && ui.Draft() == marker)
            {
                exactDraftSeen = true;
                log("observed exact draft; QQ foreground=" + (Native.GetForegroundWindow() == identity.Scope.Target.Hwnd));
            }
            var count = ui.Messages(marker).MarkerRows(marker);
            if (count > 0)
            {
                log("observed marker rows=" + count + " initial=" + baseline + " empty=" + ui.EmptyDraft() +
                    " receiver=unverified");
                return count == 1 ? 0 : 1;
            }
            Thread.Sleep(300);
        } while (timer.Elapsed.TotalSeconds < seconds);
        log("message-not-observed; exactDraftSeen=" + exactDraftSeen + "; no automated retry");
        return 1;
    }
}
