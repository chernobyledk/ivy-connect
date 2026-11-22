using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class ContinuousForm : Form
{
    internal const string WitnessText = "abcdefghijklmnopqrstuvwxyz1234567890";
    private readonly ComboBox windows = new ComboBox { Width = 425, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label bindingStatus = new Label { Width = 425, Height = 26, Text = "未绑定" };
    private readonly Label live = new Label { Width = 425, Height = 44, Text = "等待开始" };
    private readonly Label lastError = new Label { Width = 425, Height = 38, Text = "最近异常：无" };
    private readonly Label runInfo = new Label { Width = 425, Height = 44 };
    private readonly NumericUpDown cap = new NumericUpDown { Minimum = 1, Maximum = 20, Value = 20, Width = 48 };
    private readonly CheckBox witness = new CheckBox { Checked = true, Text = "专用便笺前台见证", AutoSize = true };
    private readonly CheckBox fullCover = new CheckBox { Checked = true, Text = "QQ 完全遮挡", AutoSize = true };
    private readonly Button bind = new Button { Text = "绑定已核实自聊", AutoSize = true };
    private readonly Button start = new Button { Text = "开始", AutoSize = true, Enabled = false };
    private readonly Button pause = new Button { Text = "暂停", AutoSize = true, Enabled = false };
    private readonly Button stop = new Button { Text = "停止并恢复", AutoSize = true, Enabled = false };
    private readonly Button reveal = new Button { Text = "显示并恢复 QQ", AutoSize = true };
    private readonly Cover cover = new Cover();
    private readonly object logGate = new object();
    private readonly string logPath;
    private Binding binding;
    private CancellationTokenSource session;
    private TypingPad activePad;
    private volatile bool paused;
    private bool enrolling, closing;

    internal ContinuousForm()
    {
        Text = "QQ 连续验证";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(455, 294);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ShowInTaskbar = true;
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), AutoScroll = true };
        Controls.Add(controls);
        controls.Controls.Add(windows);
        controls.Controls.Add(bindingStatus);
        controls.Controls.Add(bind);
        controls.Controls.Add(start);
        controls.Controls.Add(pause);
        controls.Controls.Add(stop);
        controls.Controls.Add(reveal);
        controls.SetFlowBreak(reveal, true);
        controls.Controls.Add(witness);
        controls.Controls.Add(fullCover);
        controls.Controls.Add(new Label { Text = "上限", AutoSize = true, Margin = new Padding(5, 7, 0, 0) });
        controls.Controls.Add(cap);
        controls.Controls.Add(runInfo);
        controls.Controls.Add(live);
        controls.Controls.Add(lastError);
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs");
        Directory.CreateDirectory(path);
        logPath = Path.Combine(path, "R3-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" +
            Guid.NewGuid().ToString("N").Substring(0, 4) + ".log");
        RefreshTargets();
        windows.SelectedIndexChanged += (s, e) => { binding = null; bindingStatus.Text = "未绑定"; start.Enabled = false; };
        bind.Click += async (s, e) => await BindManual();
        start.Click += (s, e) => StartRun();
        pause.Click += (s, e) => { paused = !paused; pause.Text = paused ? "继续" : "暂停"; };
        stop.Click += (s, e) => RequestStop();
        reveal.Click += (s, e) => {
            var target = binding != null ? binding.Target : windows.SelectedItem as Target;
            RequestStop();
            if (target == null || !target.SameWindow()) return;
            if (Native.IsIconic(target.Hwnd)) Native.ShowWindowAsync(target.Hwnd, 9);
            Log("manual-display requested foregroundAccepted=" + Native.SetForegroundWindow(target.Hwnd));
        };
        FormClosing += (s, e) => {
            if (session != null) { e.Cancel = true; closing = true; RequestStop(); }
            else if (enrolling) e.Cancel = true;
            else cover.Dispose();
        };
        Log("tool-open log=" + logPath);
    }
    private void RefreshTargets()
    {
        windows.Items.Clear();
        foreach (var target in Target.List()) windows.Items.Add(target);
        if (windows.Items.Count == 1) windows.SelectedIndex = 0;
    }
    private async Task BindManual()
    {
        if (windows.SelectedItem == null) RefreshTargets();
        var target = windows.SelectedItem as Target;
        if (target == null) { live.Text = "请选择当前 QQ 窗口"; return; }
        if (MessageBox.Show(this, "请先核对当前 QQ 会话对方的 QQ 号和自己的 QQ 号一致。确认后有 5 秒切回该会话。",
            Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await Enroll(target, false);
    }
    internal async void EnrollPreviousSelf(long handle)
    {
        var target = windows.Items.Cast<Target>().FirstOrDefault(item => item.Hwnd.ToInt64() == handle);
        if (target == null) { live.Text = "原已核实窗口未找到"; return; }
        windows.SelectedItem = target;
        if (await Enroll(target, true)) StartRun();
    }
    private async Task<bool> Enroll(Target target, bool previous)
    {
        enrolling = true;
        windows.Enabled = bind.Enabled = start.Enabled = false;
        try
        {
            live.Text = "请切到已核实的 QQ 自聊";
            if (previous)
            {
                for (int n = 0; n < 95 && Native.GetForegroundWindow() != target.Hwnd; n++) await Task.Delay(400);
                await Task.Delay(450);
            }
            else await Task.Delay(5000);
            binding = await Task.Run(() => {
                for (int n = 0; ; n++)
                {
                    if (Native.GetForegroundWindow() != target.Hwnd)
                        throw new InvalidOperationException("绑定需要核实后的 QQ 窗口处于前台");
                    try
                    {
                        var scope = new Binding(target, true);
                        if (previous && !scope.RequireMatch().Messages("窗口测试 基准-01").Contains("窗口测试 基准-01"))
                            throw new InvalidOperationException("未看到原已核实自聊的基准编号");
                        return scope;
                    }
                    catch (ElementNotAvailableException) { if (n == 3) throw; Thread.Sleep(350); }
                }
            });
            bindingStatus.Text = "已绑定本人会话 • HWND " + target.Hwnd.ToInt64();
            Log("binding verified-self hwnd=" + target.Hwnd.ToInt64() + " pid=" + target.Pid);
            return true;
        }
        catch (Exception ex) { live.Text = "绑定失败：" + Experiment.Failure(ex); Log(live.Text); return false; }
        finally
        {
            enrolling = false;
            windows.Enabled = bind.Enabled = true;
            start.Enabled = binding != null;
        }
    }
    private async void StartRun()
    {
        if (session != null || binding == null) return;
        session = new CancellationTokenSource();
        paused = false;
        lastError.Text = "最近异常：无";
        start.Enabled = bind.Enabled = windows.Enabled = witness.Enabled = fullCover.Enabled = cap.Enabled = false;
        stop.Enabled = pause.Enabled = true;
        pause.Text = "暂停";
        var code = Guid.NewGuid().ToString("N").Substring(0, 5).ToUpperInvariant();
        runInfo.Text = "手机测试前缀：联测 " + code + " 01\r\n便笺校验串：" + WitnessText;
        await RunSession(binding, code, (int)cap.Value, witness.Checked, fullCover.Checked, session.Token);
    }
    private void RequestStop()
    {
        if (session == null) return;
        session.Cancel();
        cover.Hide();
        if (activePad != null) activePad.Dispose();
        live.Text = "已撤去遮挡，正在结束当前检查";
        Log("stop-requested cover=hidden");
    }
    private async Task RunSession(Binding scope, string code, int limit, bool usePad, bool useCover, CancellationToken token)
    {
        TypingPad pad = null;
        var target = scope.Target;
        var before = Rectangle.Empty;
        try
        {
            if (!target.SameWindow() || Native.IsIconic(target.Hwnd))
                throw new InvalidOperationException("请手动恢复 QQ，再开始");
            before = Native.Bounds(target.Hwnd);
            live.Text = "正在建立测试条件";
            var monitor = Screen.FromHandle(target.Hwnd).WorkingArea;
            if (before.Left - monitor.Left >= Width)
                Location = new Point(monitor.Left + 10, monitor.Bottom - Height - 10);
            else if (monitor.Right - before.Right >= Width)
                Location = new Point(monitor.Right - Width - 10, monitor.Bottom - Height - 10);
            if (useCover) cover.CoverRect(before);
            var coverHandle = useCover ? cover.Handle : IntPtr.Zero;
            if (usePad)
            {
                var work = Screen.FromHandle(target.Hwnd).WorkingArea;
                const int width = 345;
                int left = before.Left - work.Left, right = work.Right - before.Right;
                if (left < width && right < width)
                    throw new InvalidOperationException("QQ 两侧没有便笺空间，可关闭专用便笺后使用另一前台软件");
                pad = new TypingPad(new Rectangle(left >= width ? before.Left - width : before.Right,
                    work.Top + 25, width, 150), WitnessText, Log, code);
                activePad = pad;
                for (int n = 0; n < 70 && pad.Hwnd == IntPtr.Zero; n++) await Task.Delay(100, token);
                if (pad.Hwnd == IntPtr.Zero) throw new InvalidOperationException("便笺未就绪");
                Native.SetForegroundWindow(pad.Hwnd);
                for (int n = 0; n < 100 && Native.GetForegroundWindow() != pad.Hwnd; n++)
                    await Task.Delay(100, token);
                if (Native.GetForegroundWindow() != pad.Hwnd || !Native.HasChildFocus(pad.Hwnd))
                    throw new InvalidOperationException("便笺未保有输入焦点");
            }
            await Task.Delay(900, token);
            using (var trace = new ForegroundTrace())
            {
                var padHwnd = pad == null ? IntPtr.Zero : pad.Hwnd;
                var controlHwnd = Handle;
                Func<IntPtr> foreground = () => {
                    var handle = Native.GetForegroundWindow();
                    if (handle == controlHwnd || (padHwnd != IntPtr.Zero &&
                        (handle != padHwnd || !Native.HasChildFocus(padHwnd)))) return IntPtr.Zero;
                    if (coverHandle != IntPtr.Zero && (!Native.Visible(coverHandle) ||
                        !Native.Above(coverHandle, target.Hwnd) ||
                        Rectangle.Intersect(Native.Bounds(coverHandle), Native.Bounds(target.Hwnd)) != Native.Bounds(target.Hwnd)))
                        return IntPtr.Zero;
                    return handle;
                };
                Log("session-start code=" + code + " hwnd=" + target.Hwnd.ToInt64() + " QQ-outer=" + before.Size +
                    " client=" + Native.ClientSize(target.Hwnd) + " DPI=" + Native.GetDpiForWindow(target.Hwnd) +
                    " full-cover=" + useCover + " pad=" + padHwnd.ToInt64() + " pad-pid=" + (pad == null ? 0 : pad.Pid));
                if (usePad && foreground() == IntPtr.Zero) throw new InvalidOperationException("前台或全覆盖检查未通过");
                try
                {
                    var runner = new ContinuousReply(scope, code, limit, () => paused, foreground, trace, Log, ShowStatus, token);
                    await Task.Run(() => runner.Run(), token);
                }
                finally
                {
                    Log("foreground-window-events " + trace.Summary());
                    if (pad != null) Log("pad witness summary " + pad.WitnessSummary);
                }
            }
        }
        catch (OperationCanceledException) { live.Text = "已停止"; }
        catch (Exception ex) { live.Text = "已停止：" + Experiment.Failure(ex); Log(live.Text); }
        finally
        {
            cover.Hide();
            if (pad != null) pad.Dispose();
            activePad = null;
            Log("restored cover=hidden pad=closed QQ-geometry-unchanged=" +
                (!before.IsEmpty && target.SameWindow() && Native.Bounds(target.Hwnd) == before));
            session.Dispose();
            session = null;
            start.Enabled = bind.Enabled = windows.Enabled = witness.Enabled = fullCover.Enabled = cap.Enabled = true;
            stop.Enabled = pause.Enabled = false;
            if (closing) Close();
        }
    }
    private void ShowStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => {
            live.Text = text;
            if (text.StartsWith("暂停：", StringComparison.Ordinal) || text.Contains("待确认") || text.Contains("待核对"))
                lastError.Text = "最近异常：" + text.Split('|')[0].Trim();
        }));
    }
    private void Log(string text)
    {
        lock (logGate)
            File.AppendAllText(logPath, DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz") +
                " " + text + Environment.NewLine, new UTF8Encoding(false));
    }
}
