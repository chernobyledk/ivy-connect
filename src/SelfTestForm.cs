using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class SelfTestForm : Form
{
    private readonly string runId = Guid.NewGuid().ToString("N").Substring(0, 10).ToUpperInvariant();
    private readonly IntPtr originalForeground = Native.GetForegroundWindow();
    private readonly Label status = new Label { AutoSize = true, Text = "正在核对本人会话…" };
    private readonly Label counts = new Label { AutoSize = true, Text = "通过 0　失败 0" };
    private readonly Button stop = new Button { Text = "停止", AutoSize = true };
    private readonly Cover cover = new Cover();
    private readonly CancellationTokenSource cancel = new CancellationTokenSource();
    private readonly bool diagnostic;
    private readonly bool foregroundControl;
    private readonly bool keyboardControl;
    private readonly bool enterControl;
    private readonly bool stateMatrix;
    private readonly bool coveredReply;
    private readonly bool coveredSmall;
    private readonly string desktopMode;
    private readonly object logGate = new object();
    private readonly string logPath;
    private TypingPad pad;
    private IntPtr qqWindow;
    private bool finished;
    private int pass, fail;
    protected override bool ShowWithoutActivation { get { return true; } }

    internal static bool IsDesktopSingle(string mode)
    {
        return mode == "--single-foreground" || mode == "--single-visible" ||
            mode == "--single-covered" || mode == "--single-small";
    }

    internal SelfTestForm(string mode)
    {
        diagnostic = mode == "--diagnostic";
        stateMatrix = mode == "--verify-window-states";
        coveredReply = mode == "--verify-covered-reply";
        coveredSmall = mode == "--verify-covered-small" || mode == "--single-small";
        desktopMode = IsDesktopSingle(mode) ? mode : null;
        enterControl = mode == "--foreground-enter-control";
        keyboardControl = enterControl || mode == "--foreground-keyboard-control";
        foregroundControl = keyboardControl || mode == "--foreground-invoke-control";
        Text = "QQ 本人会话自测";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(302, 125);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(status);
        panel.Controls.Add(counts);
        panel.Controls.Add(stop);
        Controls.Add(panel);
        var runs = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs");
        Directory.CreateDirectory(runs);
        var prefix = desktopMode != null ? "R5-desktop-" :
            stateMatrix ? "R5-states-" : coveredReply ? "R5-reply-" : coveredSmall ? "R5-small-" : "R4-";
        logPath = Path.Combine(runs, prefix + runId + ".log");
        stop.Click += (s, e) => RequestStop();
        FormClosing += (s, e) => { if (!finished) { e.Cancel = true; RequestStop(); } };
        Shown += async (s, e) => await Execute(runs);
    }
    private void RequestStop()
    {
        cancel.Cancel();
        cover.Hide();
        stop.Enabled = false;
        status.Text = "正在停止并恢复";
    }
    private async Task Execute(string directory)
    {
        Target target = null;
        Native.Placement original = new Native.Placement();
        Rectangle window = Rectangle.Empty;
        var protocol = new SelfTestProtocol(runId);
        Mutex mutex = null;
        bool owned = false;
        try
        {
            target = SelfTestIdentity.OpenTarget();
            qqWindow = target.Hwnd;
            mutex = new Mutex(false, @"Local\QQWindowLab.Continuous." + target.Hwnd.ToInt64());
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("原连续验证还在运行");
            original = Native.GetPlacement(target.Hwnd);
            // Measure the original rectangle before any setup activation or restoring a minimized window.
            window = Native.Bounds(target.Hwnd);
            var work = Screen.FromHandle(target.Hwnd).WorkingArea;
            if (window.Left - work.Left >= Width) Location = new Point(work.Left + 8, work.Bottom - Height - 12);
            var budget = new SelfTestBudget(Path.Combine(directory,
                desktopMode != null ? "R5-desktop-budget.log" :
                    stateMatrix || coveredReply || coveredSmall ? "R5-budget-20261121.log" : "R4-budget-20261121.log"));
            if (budget.Used >= SelfTestBudget.Cap - 1) throw new InvalidOperationException("本轮发送预算不足");
            Log("run=" + runId + " local-only qq-hwnd=" + target.Hwnd.ToInt64() + " pid=" + target.Pid +
                " starting-budget=" + budget.Used + " cap=20");
            using (var trace = new ForegroundTrace())
            {
                await Task.Delay(850, cancel.Token);
                if (desktopMode != null) SelfTestIdentity.CheckProcess(target);
                if (Native.IsIconic(target.Hwnd))
                {
                    Log("setup A show QQ without activation result=" + Native.ShowWindowAsync(target.Hwnd, 4));
                    await Task.Delay(450, cancel.Token);
                }
                var identity = await Task.Run(() => SelfTestIdentity.Read(target), cancel.Token);
                Log("identity matched operator account-number verification, process/window/start and chat evidence");
                var runner = new SelfTestRunner(identity, protocol, budget, Log, cancel.Token, keyboardControl, enterControl);
                var testRect = Native.Bounds(target.Hwnd);
                if (stateMatrix)
                {
                    await VerifyWindowStates(runner, target, trace, work, testRect);
                    Log("final foreground-events " + trace.Summary());
                    return;
                }
                if (desktopMode == "--single-foreground" || desktopMode == "--single-visible")
                {
                    if (Native.GetForegroundWindow() != target.Hwnd)
                        Log("single-test setup before measurement, QQ activation-request=" +
                            Native.SetForegroundWindow(target.Hwnd));
                    await StableForeground(target.Hwnd);
                    var frontOnly = desktopMode == "--single-foreground";
                    if (!frontOnly) await OpenPad(work, testRect);
                    await Stage(runner, frontOnly ? "单次前台" : "单次可见失焦", 1,
                        frontOnly ? target.Hwnd : pad.Hwnd, IntPtr.Zero, trace, testRect,
                        sendOnly: true, requireExposed: true);
                    Log("final foreground-events " + trace.Summary());
                    return;
                }
                if (coveredReply || coveredSmall || desktopMode == "--single-covered")
                {
                    if (coveredSmall)
                    {
                        if (!Native.SetWindowPos(target.Hwnd, IntPtr.Zero, testRect.X, testRect.Y, 480, 360,
                                Native.NoActivate | Native.NoZOrder | Native.NoOwnerZOrder | Native.AsyncPosition))
                            throw new InvalidOperationException("小窗尺寸请求未受理");
                        await Task.Delay(320, cancel.Token);
                        testRect = Native.Bounds(target.Hwnd);
                        Log("historical size request 480x360; actual=" + testRect.Size +
                            " client=" + Native.ClientSize(target.Hwnd) + " DPI=" + Native.GetDpiForWindow(target.Hwnd));
                    }
                    await OpenPad(work, testRect);
                    cover.CoverRect(testRect);
                    await Task.Delay(800, cancel.Token);
                    var single = coveredSmall || desktopMode == "--single-covered";
                    var label = coveredSmall ? "F 小窗全遮挡" : single ? "单次完全遮挡" : "F 全遮挡固定回执";
                    await Stage(runner, label, single ? 1 : 2,
                        pad.Hwnd, cover.Handle, trace, testRect, sendOnly: single);
                    Log("final foreground-events " + trace.Summary());
                    return;
                }
                if (foregroundControl)
                {
                    if (Native.GetForegroundWindow() != target.Hwnd)
                        Log("setup foreground-only control activation-call QQ result=" + Native.SetForegroundWindow(target.Hwnd));
                    await StableForeground(target.Hwnd);
                    if (keyboardControl)
                    {
                        await Task.Run(() => {
                            var ui = identity.Scope.RequireMatch();
                            identity.Validate(ui.ReplyRows());
                            if (Native.GetForegroundWindow() != target.Hwnd || !ui.EmptyDraft())
                                throw new SelfTestHalt("前台键盘对照未确认 QQ 前台或空草稿");
                            if (!ui.EditorFocused()) { ui.Editor.SetFocus(); Log("setup QQ editor SetFocus"); }
                        }, cancel.Token);
                        await StableForeground(target.Hwnd);
                    }
                    var label = enterControl ? "前台 Enter 对照" : keyboardControl ? "前台键盘写入对照" : "前台 Invoke 对照";
                    await Stage(runner, label, 1,
                        target.Hwnd, IntPtr.Zero, trace, testRect);
                    return;
                }
                var front = await StableForeground(IntPtr.Zero);
                Log("setup A stable foreground=" + front.ToInt64() + " QQ=" + (front == target.Hwnd) +
                    " diagnostic=" + diagnostic);
                var a = await Stage(runner, "A 展开·冒烟", 1, front, IntPtr.Zero, trace, testRect);
                if (a.Passed != 1 && !diagnostic) return;
                if (a.Passed == 1 && !diagnostic && budget.Used <= SelfTestBudget.Cap - 2)
                    await Stage(runner, "A 展开·继续", 1, front, IntPtr.Zero, trace, testRect);
                var pairs = diagnostic ? 1 : 2;
                cover.CoverRect(testRect);
                await Task.Delay(800, cancel.Token);
                await Stage(runner, "B 完全遮挡", pairs, front, cover.Handle, trace, testRect);
                cover.Hide();
                await OpenPad(work, testRect);
                await Stage(runner, "C 便笺前台", pairs, pad.Hwnd, IntPtr.Zero, trace, testRect);
                cover.CoverRect(testRect);
                await Task.Delay(800, cancel.Token);
                await Stage(runner, "D 完全遮挡·便笺前台", pairs, pad.Hwnd, cover.Handle, trace, testRect);
                Log("final foreground-events " + trace.Summary());
            }
        }
        catch (OperationCanceledException) { Log("operator stop"); }
        catch (Exception ex) { fail++; Log("stop " + Experiment.Failure(ex)); }
        finally
        {
            protocol.Disable();
            var lastForeground = Native.GetForegroundWindow();
            var restoreForeground = lastForeground == Handle ||
                (cover.IsHandleCreated && lastForeground == cover.Handle) ||
                (pad != null && lastForeground == pad.Hwnd) ||
                ((foregroundControl || desktopMode == "--single-foreground") &&
                    target != null && lastForeground == target.Hwnd);
            cover.Hide();
            if (pad != null) pad.Dispose();
            if (target != null && target.SameWindow() && !window.IsEmpty)
            {
                var current = Native.GetPlacement(target.Hwnd);
                if (current.Show != original.Show || Native.Bounds(target.Hwnd) != window ||
                    current.Normal.ToRectangle() != original.Normal.ToRectangle())
                    Native.SetWindowPlacement(target.Hwnd, ref original);
                var restored = Native.GetPlacement(target.Hwnd);
                Log("restored QQ rect=" + (Native.Bounds(target.Hwnd) == window) +
                    " show=" + (restored.Show == original.Show) +
                    " normalBounds=" + (restored.Normal.ToRectangle() == original.Normal.ToRectangle()));
            }
            if (restoreForeground && originalForeground != IntPtr.Zero && originalForeground != Handle &&
                originalForeground != Native.GetForegroundWindow() && Native.Visible(originalForeground))
                Log("restore original foreground activation-call result=" + Native.SetForegroundWindow(originalForeground));
            Log("finished self-test=off passed=" + pass + " failed=" + fail + " log=" + logPath);
            if (owned) mutex.ReleaseMutex();
            if (mutex != null) mutex.Dispose();
            finished = true;
            cover.Dispose();
            Close();
        }
    }
    private async Task<IntPtr> StableForeground(IntPtr required)
    {
        var previous = IntPtr.Zero;
        int stable = 0;
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(160, cancel.Token);
            var current = Native.GetForegroundWindow();
            bool eligible = current != IntPtr.Zero && current != Handle &&
                (required == IntPtr.Zero || current == required);
            stable = eligible ? (current == previous ? stable + 1 : 1) : 0;
            if (stable == 4) return current;
            previous = current;
        }
        throw new InvalidOperationException("无法建立稳定的测试前台");
    }
    private async Task OpenPad(Rectangle work, Rectangle qq)
    {
        const int width = 288;
        var left = qq.Left - work.Left;
        var right = work.Right - qq.Right;
        if (left < width && right < width) throw new InvalidOperationException("QQ 两侧没有专用便笺空间");
        pad = new TypingPad(new Rectangle(left >= width ? qq.Left - width : qq.Right,
            work.Top + 25, width, 130), automaticMarker: "PAD_" + runId + "_");
        for (int n = 0; n < 40 && pad.Hwnd == IntPtr.Zero; n++) await Task.Delay(100, cancel.Token);
        if (pad.Hwnd == IntPtr.Zero) throw new InvalidOperationException("专用测试便笺未启动");
        if (Native.GetForegroundWindow() != pad.Hwnd)
            Log("setup C activation-call OWN-PAD result=" + Native.SetForegroundWindow(pad.Hwnd));
        await StableForeground(pad.Hwnd);
        Log("automatic input contrast: child-only textbox insertion pad=" + pad.Hwnd.ToInt64() +
            " pid=" + pad.Pid + " system-keys=no");
    }

    private async Task VerifyWindowStates(SelfTestRunner runner, Target target, ForegroundTrace trace,
        Rectangle work, Rectangle rect)
    {
        if (Native.GetForegroundWindow() != target.Hwnd)
            Log("state matrix setup, before background stages QQ activation-request=" + Native.SetForegroundWindow(target.Hwnd));
        await StableForeground(target.Hwnd);
        await OpenPad(work, rect);
        await Stage(runner, "V 可见·便笺前台", 1, pad.Hwnd, IntPtr.Zero, trace, rect, sendOnly: true, requireExposed: true);

        cover.CoverRect(rect);
        await Task.Delay(800, cancel.Token);
        await Stage(runner, "F 完全遮挡·便笺前台", 1, pad.Hwnd, cover.Handle, trace, rect, sendOnly: true);
        cover.Hide();
        await StableForeground(pad.Hwnd);

        var expanded = Native.GetPlacement(target.Hwnd);
        try
        {
            Log("G setup minimize without activation result=" + Native.ShowWindowAsync(target.Hwnd, Native.ShowMinimizedNoActivate));
            await Task.Delay(650, cancel.Token);
            if (!Native.IsIconic(target.Hwnd)) throw new InvalidOperationException("未进入真正最小化");
            await Stage(runner, "G 真正最小化·便笺前台", 1, pad.Hwnd, IntPtr.Zero, trace,
                Native.Bounds(target.Hwnd), sendOnly: true, minimized: true);
        }
        finally
        {
            if (target.SameWindow() && Native.IsIconic(target.Hwnd))
            {
                Log("G restore expanded result=" + Native.SetWindowPlacement(target.Hwnd, ref expanded));
                Thread.Sleep(500);
            }
            try { Task.Run(() => runner.RecoverMinimizedDraft()).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log("G draft recovery unavailable " + Experiment.Failure(ex)); }
        }
    }

    private async Task<SelfTestResult> Stage(SelfTestRunner runner, string label, int pairs, IntPtr front,
        IntPtr covered, ForegroundTrace trace, Rectangle rect, bool sendOnly = false, bool minimized = false,
        bool requireExposed = false)
    {
        cancel.Token.ThrowIfCancellationRequested();
        status.Text = label;
        var target = qqWindow;
        var inPad = pad != null && pad.Hwnd == front;
        var steps = inPad ? pad.AutomaticSteps : 0;
        var lost = inPad ? pad.AutomaticFocusLoss : 0;
        var mismatch = inPad ? pad.AutomaticMismatch : 0;
        Log("stage " + label + " planned=" + pairs + " expectedForeground=" + front.ToInt64() +
            " cover=" + covered.ToInt64() + " rect=" + rect + " iconic=" + Native.IsIconic(target));
        Func<bool> conditions = () => Native.GetForegroundWindow() == front &&
            (!inPad || Native.HasChildFocus(front)) && Native.Bounds(target) == rect && Native.IsIconic(target) == minimized &&
            (!requireExposed || (Native.RootAt(rect.Left + rect.Width / 2, rect.Top + 32) == target &&
                Native.RootAt(rect.Left + 3 * rect.Width / 4, rect.Top + rect.Height / 2) == target &&
                Native.RootAt(rect.Left + 3 * rect.Width / 4, rect.Bottom - 48) == target)) &&
            (covered == IntPtr.Zero || (Native.Visible(covered) && Native.Above(covered, target) &&
                Rectangle.Intersect(rect, Native.Bounds(covered)) == rect));
        var result = await Task.Run(() => runner.Stage(label, pairs, conditions, trace,
            inPad ? "PAD_" + runId + "_" : null, foregroundControl, sendOnly, minimized), cancel.Token);
        pass += result.Passed;
        fail += result.Failed;
        counts.Text = "通过 " + pass + "　失败 " + fail;
        if (inPad)
            Log(label + " automatic input steps=" + (pad.AutomaticSteps - steps) +
                " focus-loss=" + (pad.AutomaticFocusLoss - lost) + " text-mismatch=" + (pad.AutomaticMismatch - mismatch));
        if (result.Stopped) throw new InvalidOperationException("保护条件停止余下阶段");
        return result;
    }
    private void Log(string value)
    {
        lock (logGate)
            File.AppendAllText(logPath, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz") +
                " " + value + Environment.NewLine, new UTF8Encoding(false));
    }
}
