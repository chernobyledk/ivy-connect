using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed partial class LabForm : Form
{
    private readonly ComboBox windows = new ComboBox { Width = 790, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox results = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
    private readonly FlowLayoutPanel tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, MaximumSize = new Size(850, 0), Padding = new Padding(8) };
    private readonly NumericUpDown width = new NumericUpDown { Minimum = 1, Maximum = 8192, Value = 500, Width = 65 };
    private readonly NumericUpDown height = new NumericUpDown { Minimum = 1, Maximum = 8192, Value = 400, Width = 65 };
    private readonly NumericUpDown watch = new NumericUpDown { Minimum = 0, Maximum = 120, Value = 0, Width = 65 };
    private readonly Button stop = new Button { Text = "停止并恢复", AutoSize = true };
    private readonly Cover cover = new Cover();
    private readonly string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs",
        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".log");
    private CancellationTokenSource job;
    private Target selected, changed;
    private Native.Placement original;
    private Rectangle originalBounds;
    private Binding binding;
    private TypingPad scratch;
    private bool closing;

    internal LabForm()
    {
        Text = "QQ 小窗 UIA 验证";
        Font = new Font("Microsoft YaHei UI", 9f);
        ClientSize = new Size(850, 530);
        MinimumSize = new Size(850, 420);
        Controls.Add(results);
        Controls.Add(tools);
        tools.Controls.Add(windows);
        Add("刷新窗口", RefreshTargets);
        Add("探测控件", () => Manual("P", false, false));
        Add("绑定指定会话（只读）", () => Enroll(false));
        Add("绑定已核实自聊", () => Enroll(true));
        Add("读消息 / 等待新消息", () => Manual("R", true, false));
        Add("写入并发送测试文字", () => Manual("S", true, true));
        Add("跑 A–G", () => Matrix(true));
        Add("前台收发基线", StartFrontBaseline);
        Add("第 2 轮对照矩阵", StartRound2);
        Add("尺寸与区域裁剪", StartRegionExperiment);
        tools.SetFlowBreak(tools.Controls[tools.Controls.Count - 1], true);
        tools.Controls.Add(new Label { Text = "宽 × 高", AutoSize = true, Margin = new Padding(4, 7, 4, 0) });
        tools.Controls.Add(width);
        tools.Controls.Add(height);
        Add("调整尺寸", () => Geometry("resize"));
        Add("部分遮挡", () => Geometry("partial"));
        Add("完全遮挡", () => Geometry("full"));
        tools.Controls.Add(new Label { Text = "每项等待新消息（秒）", AutoSize = true, Margin = new Padding(4, 7, 4, 0) });
        tools.Controls.Add(watch);
        tools.Controls.Add(stop);
        stop.Click += (s, e) => Stop();
        windows.SelectionChangeCommitted += (s, e) =>
        {
            Restore();
            selected = windows.SelectedItem as Target;
            binding = null;
        };
        FormClosing += (s, e) =>
        {
            if (job != null) { e.Cancel = true; closing = true; Stop(); }
            else { Restore(); cover.Dispose(); }
        };
        Shown += (s, e) => RefreshTargets();
        Write("日志 " + logPath);
    }
    private void Add(string label, Action action)
    {
        var button = new Button { Text = label, AutoSize = true };
        button.Click += (s, e) => {
            try { action(); }
            catch (Exception ex) { Write(Experiment.Failure(ex)); }
        };
        tools.Controls.Add(button);
    }
    private void RefreshTargets()
    {
        Restore();
        binding = null;
        windows.Items.Clear();
        foreach (var target in Target.List()) windows.Items.Add(target);
        windows.SelectedIndex = windows.Items.Count == 1 ? 0 : -1;
        selected = windows.SelectedItem as Target;
        Write("发现 QQ 窗口 " + windows.Items.Count);
    }
    internal void SelectTarget(long hwnd)
    {
        selected = Target.List().Single(t => t.Hwnd.ToInt64() == hwnd);
        for (int index = 0; index < windows.Items.Count; index++)
            if (((Target)windows.Items[index]).Hwnd == selected.Hwnd) windows.SelectedIndex = index;
    }
    private Target Selected()
    {
        if (selected == null || !selected.SameWindow()) throw new InvalidOperationException("请先选择 QQ 窗口");
        return selected;
    }
    private void Enroll(bool self)
    {
        var target = Selected();
        var consent = self
            ? "请核对 QQ 资料页。只有会话对方 QQ 号和此客户端当前登录 QQ 号相同，才选择「是」。绑定后只向当前核实的会话发送固定测试文本。请勿在测试期间切换此 QQ 窗口内的会话。"
            : "确认窗口正显示允许读取的会话内容。日志只记录数量和测试结果。";
        if (MessageBox.Show(this, consent, Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        Write("5 秒内切回已核实的 QQ 会话。");
        StartJob(async token => {
            await Task.Delay(5000, token);
            if (Native.GetForegroundWindow() != target.Hwnd) throw new InvalidOperationException("所选窗口未处于前台，绑定取消");
            var enrolled = await Task.Run(() => new Binding(target, self), token);
            binding = enrolled;
            Write(self ? "已绑定人工核实的自聊" : "已绑定只读会话");
        });
    }
    internal void CaptureAndRun(long expectedHwnd, bool watchF)
    {
        Write("等待切换到用户已按 QQ 号核实的自聊窗口。");
        StartJob(async token => {
            // Only the foreground window deliberately selected after this prompt can bind.
            for (int count = 0; count < 150; count++)
            {
                await Task.Delay(400, token);
                var foreground = Native.GetForegroundWindow();
                var target = Target.List().FirstOrDefault(t => t.Hwnd == foreground);
                if (target == null) continue;
                if (target.Hwnd.ToInt64() != expectedHwnd) continue;
                await Task.Delay(1200, token);
                if (Native.GetForegroundWindow() != target.Hwnd) continue;
                var enrolled = await Task.Run(() => new Binding(target, true), token);
                SelectTarget(target.Hwnd.ToInt64());
                binding = enrolled;
                Write("已绑定人工核实的自聊，开始 A–G");
                await MatrixCore(target, enrolled, true, 0, token, watchF);
                return;
            }
            Write("等待超时，未绑定或发送。");
        });
    }
    private void Manual(string name, bool read, bool send)
    {
        var target = Selected();
        if (read && binding == null) throw new InvalidOperationException("请先绑定已核实的会话");
        if (send && !binding.Self) throw new InvalidOperationException("发送仅限人工按 QQ 号核实的自聊");
        var scope = read ? binding : null;
        var seconds = (int)watch.Value;
        Write("3 秒后按届时的前台状态执行。");
        StartJob(token => Task.Run(() => {
            var experiment = new Experiment(target, scope, Write, token);
            experiment.Wait(3000);
            var foreground = Native.GetForegroundWindow();
            experiment.Check(name, () => {
                if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("前台窗口已变化");
            }, send, seconds);
        }, token));
    }
    private void CapturePlacement(Target target)
    {
        if (changed != null) return;
        original = Native.GetPlacement(target.Hwnd);
        originalBounds = Native.Bounds(target.Hwnd);
        changed = target;
    }
    private void Geometry(string action)
    {
        var target = Selected();
        var before = Native.GetForegroundWindow();
        if (Native.IsIconic(target.Hwnd) || Native.IsZoomed(target.Hwnd))
            throw new InvalidOperationException("请先手动恢复 QQ 到普通窗口状态");
        CapturePlacement(target);
        var r = Native.Bounds(target.Hwnd);
        if (action == "resize")
        {
            HideCover();
            ResizeTo(target, new Rectangle(r.Location, new Size((int)width.Value, (int)height.Value)));
        }
        else ShowCover(target, action == "full");
        var measured = Native.Bounds(target.Hwnd);
        Write(String.Format("{0}: measured={1}x{2} foregroundChanged={3}", action, measured.Width, measured.Height,
            Native.GetForegroundWindow() != before));
    }
    internal void Matrix(bool confirmed)
    {
        var target = Selected();
        var scope = binding;
        if (confirmed && MessageBox.Show(this, "A 明确尝试将选中窗口切到前台。E–G 会使用临时便笺作为另一个前台窗口。已绑定自聊时每项会发送一条固定测试文字。开始？",
                Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        var seconds = (int)watch.Value;
        StartJob(token => MatrixCore(target, scope, scope != null && scope.Self, seconds, token));
    }
    private async Task MatrixCore(Target target, Binding scope, bool send, int seconds, CancellationToken token, bool watchF = false)
    {
        CapturePlacement(target);
        Write(String.Format("target pid={0} hwnd={1} originalShow={2} scope={3}", target.Pid,
            target.Hwnd.ToInt64(), original.Show, scope == null ? "structure only" : send ? "verified self" : "read only"));
        var experiment = new Experiment(target, scope, Write, token);
        ExceptionDispatchInfo failure = null;
        try
        {
            // Only setup for A may explicitly foreground the QQ window.
            if (Native.IsIconic(target.Hwnd) || Native.IsZoomed(target.Hwnd))
                Native.ShowWindowAsync(target.Hwnd, Native.ShowNormal);
            if (Native.GetForegroundWindow() != target.Hwnd) Native.SetForegroundWindow(target.Hwnd);
            await Task.Delay(350, token);
            HideCover();
            var normal = Native.Bounds(target.Hwnd);
            if (normal.Width < 800 || normal.Height < 550)
            {
                ResizeTo(target, new Rectangle(normal.Location, new Size(Math.Max(800, normal.Width), Math.Max(550, normal.Height))));
                normal = Native.Bounds(target.Hwnd);
            }
            await Sample(experiment, target, "A", true, false, false, send, seconds, token);
            var small = Measure(target, normal);
            await Task.Delay(350, token);
            await Sample(experiment, target, "B", true, false, false, send, seconds, token);
            ResizeTo(target, normal);
            ShowCover(target, false);
            await Sample(experiment, target, "C", true, false, false, send, seconds, token);
            ShowCover(target, true);
            await Sample(experiment, target, "D", true, false, false, send, seconds, token);
            HideCover();
            await OpenScratch(target, token);
            await Sample(experiment, target, "E", false, false, true, send, seconds, token);
            ResizeTo(target, small);
            ShowCover(target, true);
            await Sample(experiment, target, "F", false, false, true, send,
                watchF ? 80 : seconds, token, watchF ? Experiment.LiveMarker : null);
            HideCover();
            Native.ShowWindowAsync(target.Hwnd, Native.ShowMinimizedNoActivate);
            await Task.Delay(450, token);
            await Sample(experiment, target, "G", false, true, true, send, seconds, token);
        }
        catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        finally { Restore(); }
        bool draftClear = await Task.Run(() => experiment.ClearProbe());
        Write("final test draft clear=" + draftClear);
        if (failure != null) failure.Throw();
    }
    private async Task Sample(Experiment experiment, Target target, string step, bool foreground,
        bool iconic, bool requiresScratch, bool send, int seconds, CancellationToken token, string incoming = null)
    {
        token.ThrowIfCancellationRequested();
        var scratchHwnd = requiresScratch ? scratch.Hwnd : IntPtr.Zero;
        var edits = requiresScratch ? scratch.Edits : 0;
        var coverHwnd = cover.Visible ? cover.Handle : IntPtr.Zero;
        if (requiresScratch) Write(step + " setup focus=temporary editor");
        await Task.Run(() => experiment.Check(step, () => {
            var fg = Native.GetForegroundWindow();
            if ((fg == target.Hwnd) != foreground || Native.IsIconic(target.Hwnd) != iconic ||
                    (requiresScratch && fg != scratchHwnd))
                throw new InvalidOperationException("实际前台或最小化状态与用例要求不符");
            if (coverHwnd != IntPtr.Zero)
            {
                var r = Native.Bounds(target.Hwnd);
                var overlap = Rectangle.Intersect(r, Native.Bounds(coverHwnd));
                if (!Native.Visible(coverHwnd) || !Native.Above(coverHwnd, target.Hwnd) ||
                    (step == "C" ? overlap.Width <= 0 || overlap.Width >= r.Width : overlap != r))
                    throw new InvalidOperationException("实际遮挡范围与用例不符");
            }
        }, send, seconds, incoming), token);
        if (requiresScratch) Write(String.Format("{0} temporary-editor activeAtEnd={1} textChanges={2}",
            step, Native.GetForegroundWindow() == scratchHwnd && Native.HasChildFocus(scratchHwnd), scratch.Edits - edits));
    }
    private Rectangle Measure(Target target, Rectangle initial)
    {
        Rectangle actual = initial;
        foreach (var size in new[] { new Size(720, 500), new Size(480, 360), new Size(320, 240),
            new Size(160, 120), new Size(32, 32), new Size(1, 1) })
        {
            job.Token.ThrowIfCancellationRequested();
            ResizeTo(target, new Rectangle(initial.Location, size));
            actual = Native.Bounds(target.Hwnd);
            Write(String.Format("size request={0}x{1} actual={2}x{3}", size.Width, size.Height, actual.Width, actual.Height));
        }
        Write("minimum measured outer px=" + actual.Width + "x" + actual.Height);
        return actual;
    }
    private static void ResizeTo(Target target, Rectangle r)
    {
        if (!target.SameWindow()) throw new InvalidOperationException("QQ 窗口失效");
        if (!Native.SetWindowPos(target.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Native.NoActivate | Native.NoZOrder | Native.NoOwnerZOrder | Native.AsyncPosition))
            throw new System.ComponentModel.Win32Exception();
        // Let the target apply WM_WINDOWPOSCHANGING constraints before reading its actual bounds.
        Thread.Sleep(220);
    }
    private void ShowCover(Target target, bool full)
    {
        var r = Native.Bounds(target.Hwnd);
        var intended = full ? r : new Rectangle(r.X + r.Width / 2, r.Y, r.Width - r.Width / 2, r.Height);
        var before = Native.GetForegroundWindow();
        cover.CoverRect(intended);
        var actual = Native.Bounds(cover.Handle);
        var overlap = Rectangle.Intersect(r, actual);
        Write(String.Format("cover={0} overlap={1}x{2}/{3}x{4} aboveQQ={5} foregroundChanged={6}", full ? "full" : "partial",
            overlap.Width, overlap.Height, r.Width, r.Height, Native.Above(cover.Handle, target.Hwnd), Native.GetForegroundWindow() != before));
    }
    private void HideCover() { if (cover.Visible) cover.Hide(); }
    private async Task OpenScratch(Target target, CancellationToken token)
    {
        var desktop = Screen.FromHandle(target.Hwnd).WorkingArea;
        var qq = Native.Bounds(target.Hwnd);
        var w = 280;
        int right = desktop.Right - qq.Right, left = qq.Left - desktop.Left;
        int x = right >= w ? qq.Right : left >= w ? qq.Left - w : desktop.Right - w;
        int y = right >= w || left >= w || desktop.Bottom - qq.Bottom < 125 ? desktop.Top : qq.Bottom;
        scratch = new TypingPad(new Rectangle(x, y, w, 125));
        for (int i = 0; i < 30 && scratch.Hwnd == IntPtr.Zero; i++) await Task.Delay(100, token);
        if (scratch.Hwnd == IntPtr.Zero) throw new InvalidOperationException("临时便笺未启动");
        Write("temporary-editor pid=" + scratch.Pid + " hwnd=" + scratch.Hwnd.ToInt64() +
            " rectangle overlaps QQ=" + Rectangle.Intersect(Native.Bounds(scratch.Hwnd), qq).Size);
        Native.SetForegroundWindow(scratch.Hwnd);
        if (Native.GetForegroundWindow() != scratch.Hwnd)
        {
            Write("等待临时便笺获得前台，必要时点击便笺。");
            for (int i = 0; i < 240 && Native.GetForegroundWindow() != scratch.Hwnd; i++) await Task.Delay(125, token);
        }
    }
    private void Stop()
    {
        HideCover();
        if (job != null) job.Cancel();
        else Restore();
    }
    private void Restore()
    {
        HideCover();
        if (changed != null)
        {
            var target = changed;
            changed = null;
            if (target.SameWindow())
            {
                bool restored = Native.SetWindowPlacement(target.Hwnd, ref original);
                var placement = Native.GetPlacement(target.Hwnd);
                Write(String.Format("restore accepted={0} placementMatches={1} actualRectMatches={2}",
                    restored, placement.Show == original.Show && placement.Normal.ToRectangle() == original.Normal.ToRectangle(),
                    Native.Bounds(target.Hwnd) == originalBounds));
            }
            else Write("restore unavailable: target window closed");
        }
        if (scratch != null) { scratch.Dispose(); scratch = null; }
    }
    private async void StartJob(Func<CancellationToken, Task> action)
    {
        if (job != null) return;
        job = new CancellationTokenSource();
        foreach (Control control in tools.Controls) control.Enabled = control == stop;
        try { await action(job.Token); }
        catch (OperationCanceledException) { Write("测试已停止"); }
        catch (Exception ex) { Write(Experiment.Failure(ex)); }
        finally
        {
            job.Dispose();
            job = null;
            foreach (Control control in tools.Controls) control.Enabled = true;
            if (closing) Close();
        }
    }
    private void Write(string text)
    {
        if (InvokeRequired) { Invoke(new Action<string>(Write), text); return; }
        var line = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(9)).ToString("yyyy-MM-dd HH:mm:ss.fff zzz") + " " + text;
        Directory.CreateDirectory(Path.GetDirectoryName(logPath));
        File.AppendAllText(logPath, line + Environment.NewLine, new UTF8Encoding(false));
        results.AppendText(line + Environment.NewLine);
    }
}
