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
    private readonly object logGate = new object();
    private readonly string logPath;
    private TypingPad pad;
    private bool finished;
    private int pass, fail;
    protected override bool ShowWithoutActivation { get { return true; } }

    internal SelfTestForm(string mode)
    {
        diagnostic = mode == "--diagnostic";
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
        logPath = Path.Combine(runs, "R4-" + runId + ".log");
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
            target = Target.FromKnownHandle(397068);
            mutex = new Mutex(false, @"Local\QQWindowLab.Continuous." + target.Hwnd.ToInt64());
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("原连续验证还在运行");
            original = Native.GetPlacement(target.Hwnd);
            // Measure the original rectangle before any setup activation or restoring a minimized window.
            window = Native.Bounds(target.Hwnd);
            var work = Screen.FromHandle(target.Hwnd).WorkingArea;
            if (window.Left - work.Left >= Width) Location = new Point(work.Left + 8, work.Bottom - Height - 12);
            var budget = new SelfTestBudget(Path.Combine(directory, "R4-budget-20261121.log"));
            if (budget.Used >= SelfTestBudget.Cap - 1) throw new InvalidOperationException("本轮发送预算不足");
            Log("run=" + runId + " local-only qq-hwnd=" + target.Hwnd.ToInt64() + " pid=" + target.Pid +
                " starting-budget=" + budget.Used + " cap=20");
            using (var trace = new ForegroundTrace())
            {
                await Task.Delay(850, cancel.Token);
                if (Native.IsIconic(target.Hwnd))
                {
                    Log("setup A show QQ without activation result=" + Native.ShowWindowAsync(target.Hwnd, 4));
                    await Task.Delay(450, cancel.Token);
                }
                var identity = await Task.Run(() => ReadIdentity(target), cancel.Token);
                Log("identity matched prior operator account-number equality, same authenticated process/window/start and three chat proofs");
                var runner = new SelfTestRunner(identity, protocol, budget, Log, cancel.Token, keyboardControl, enterControl);
                var testRect = Native.Bounds(target.Hwnd);
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
                (foregroundControl && target != null && lastForeground == target.Hwnd);
            cover.Hide();
            if (pad != null) pad.Dispose();
            if (target != null && target.SameWindow() && !window.IsEmpty)
            {
                var current = Native.GetPlacement(target.Hwnd);
                if (current.Show != original.Show || Native.Bounds(target.Hwnd) != window)
                    Native.SetWindowPlacement(target.Hwnd, ref original);
                Log("restored QQ rect=" + (Native.Bounds(target.Hwnd) == window) +
                    " show=" + (Native.GetPlacement(target.Hwnd).Show == original.Show));
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
    private static SelfTestIdentity ReadIdentity(Target target)
    {
        for (int n = 0; ; n++)
        {
            try { return new SelfTestIdentity(target); }
            catch (ElementNotAvailableException) { if (n == 3) throw; Thread.Sleep(260); }
        }
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
    private async Task<SelfTestResult> Stage(SelfTestRunner runner, string label, int pairs, IntPtr front,
        IntPtr covered, ForegroundTrace trace, Rectangle rect)
    {
        cancel.Token.ThrowIfCancellationRequested();
        status.Text = label;
        var target = Target.FromKnownHandle(397068).Hwnd;
        var inPad = pad != null && pad.Hwnd == front;
        var steps = inPad ? pad.AutomaticSteps : 0;
        var lost = inPad ? pad.AutomaticFocusLoss : 0;
        var mismatch = inPad ? pad.AutomaticMismatch : 0;
        Log("stage " + label + " planned=" + pairs + " expectedForeground=" + front.ToInt64() +
            " cover=" + covered.ToInt64() + " rect=" + rect);
        Func<bool> conditions = () => Native.GetForegroundWindow() == front &&
            (!inPad || Native.HasChildFocus(front)) && Native.Bounds(target) == rect &&
            (covered == IntPtr.Zero || (Native.Visible(covered) && Native.Above(covered, target) &&
                Rectangle.Intersect(rect, Native.Bounds(covered)) == rect));
        var result = await Task.Run(() => runner.Stage(label, pairs, conditions, trace,
            inPad ? "PAD_" + runId + "_" : null, foregroundControl), cancel.Token);
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
