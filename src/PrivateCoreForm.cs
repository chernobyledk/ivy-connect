using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class PrivateCoreForm : Form
{
    private readonly string folder;
    private readonly TextBox[] fields = Enumerable.Range(0, 5).Select(i => new TextBox()).ToArray();
    private readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly CheckBox attested = new CheckBox { Text = "已从 QQ 资料卡核对两端号码，并打开此私聊", AutoSize = true };
    private readonly Button bind = new Button { Text = "核验并绑定", AutoSize = true };
    private readonly Button start = new Button { Text = "启动固定回复", AutoSize = true };
    private readonly Button pause = new Button { Text = "暂停", AutoSize = true };
    private readonly Button stop = new Button { Text = "停止", AutoSize = true };
    private PrivateRunControl run;
    private CancellationTokenSource enrollment;
    private bool closePending;

    internal PrivateCoreForm(string folder)
    {
        this.folder = folder;
        Text = "QQ 自动回复 · 单私聊";
        ClientSize = new Size(510, 335);
        MinimumSize = new Size(490, 330);
        StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 182, Padding = new Padding(8), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        var labels = new[] { "机器人登录 QQ 号", "允许回复的对象 QQ 号", "机器人的登录显示名",
            "私聊会话显示名", "QQ 窗口句柄（十进制）" };
        for (int i = 0; i < labels.Length; i++)
        {
            grid.Controls.Add(new Label { Text = labels[i], Anchor = AnchorStyles.Left, AutoSize = true }, 0, i);
            fields[i].Dock = DockStyle.Fill;
            grid.Controls.Add(fields[i], 1, i);
        }
        grid.Controls.Add(attested, 0, 5);
        grid.SetColumnSpan(attested, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 43, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { bind, start, pause, stop });
        Controls.Add(status);
        Controls.Add(buttons);
        Controls.Add(grid);
        status.Text = "未启动。请先核验绑定。";
        try
        {
            using (var store = new PrivateStore(folder))
            {
                var p = store.State.Binding;
                if (p != null)
                {
                    var saved = new[] { p.BotQq, p.PeerQq, p.BotDisplay, p.PeerDisplay, p.Window.ToString() };
                    for (int i = 0; i < saved.Length; i++) fields[i].Text = saved[i];
                    status.Text = Summary(store, "已保存绑定，尚未启动");
                }
            }
        }
        catch (Exception ex) { status.Text = "状态不可读取 · " + Experiment.Failure(ex); }
        bind.Click += async (s, e) => await Enroll();
        start.Click += async (s, e) => await StartCore();
        pause.Click += (s, e) => {
            if (run == null || run.Token.IsCancellationRequested) return;
            if (run.Paused) { run.Resume(); pause.Text = "暂停"; status.Text = "正在复核后恢复…"; }
            else { run.Pause(); pause.Text = "恢复"; status.Text = "已请求暂停。已提交动作继续核查。"; }
        };
        stop.Click += (s, e) => RequestStop();
        FormClosing += (s, e) => {
            if (run == null && enrollment == null) return;
            e.Cancel = true; closePending = true; RequestStop();
        };
        UpdateButtons();
    }

    private static string Summary(PrivateStore store, string head)
    {
        var events = store.State.Events;
        return head + Environment.NewLine + "观察 " + events.Length + " · 本地确认 " +
            events.Count(x => x.Phase == PrivatePhase.LocalConfirmed) + " · 待确认 " +
            events.Count(x => x.Phase == PrivatePhase.Uncertain || x.Phase == PrivatePhase.SubmitIntent);
    }
    private void UpdateButtons()
    {
        var idle = run == null && enrollment == null;
        bind.Enabled = start.Enabled = attested.Enabled = idle;
        foreach (var field in fields) field.Enabled = idle;
        pause.Enabled = run != null && !run.Token.IsCancellationRequested;
        stop.Enabled = !idle;
    }
    private void RequestStop()
    {
        if (enrollment != null) enrollment.Cancel();
        if (run != null) run.Stop();
        status.Text = "正在停止，禁止新的提交。";
        UpdateButtons();
    }
    private void Report(string text)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => {
            if (run != null && !run.Token.IsCancellationRequested || enrollment != null)
            {
                status.Text = text;
                if (run != null && run.Paused) pause.Text = "恢复";
            }
        }));
    }
    private void Log(string text)
    {
        File.AppendAllText(Path.Combine(folder, "app.log"), PrivateStore.Stamp() + " " + text + Environment.NewLine);
    }
    private void Finished() { UpdateButtons(); if (closePending) Close(); }

    private async Task Enroll()
    {
        long window;
        var a = fields.Select(x => x.Text.Trim()).ToArray();
        if (!attested.Checked || !Int64.TryParse(a[4], out window))
        {
            status.Text = "请核对两张资料卡，并填入已打开私聊的窗口句柄。";
            return;
        }
        using (var pending = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            enrollment = pending;
            status.Text = "正在建立只读基线…";
            UpdateButtons();
            try
            {
                await Task.Run(() => {
                    using (var store = new PrivateStore(folder))
                    {
                        var prior = store.State.Binding;
                        if (prior != null && (prior.BotQq != a[0] || prior.PeerQq != a[1]))
                            throw new InvalidOperationException("此状态目录只允许原核验账号对");
                        var marker = "QQ验证绑定 " + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
                        store.State.Binding = QqPrivateTransport.Enroll(window, a[0], a[1], a[2], a[3],
                            marker, pending.Token, text => { Log(text); Report("请由已核对的对象发送 " + marker); });
                        pending.Token.ThrowIfCancellationRequested();
                        store.Save();
                        Log("enrollment complete");
                    }
                });
                status.Text = "已保存核验绑定。点击启动后才会回复新消息。";
            }
            catch (OperationCanceledException) { status.Text = "核验已取消或超时"; }
            catch (Exception ex) { status.Text = "核验暂停 · " + Experiment.Failure(ex); }
            finally { enrollment = null; Finished(); }
        }
    }

    private async Task StartCore()
    {
        var local = fields[0].Text.Trim();
        var other = fields[1].Text.Trim();
        using (var active = new PrivateRunControl())
        {
            run = active;
            pause.Text = "暂停";
            status.Text = "复核绑定和启动基线…";
            UpdateButtons();
            try { status.Text = await Task.Run(() => Watch(local, other, active)); }
            catch (OperationCanceledException) { status.Text = "已停止"; }
            catch (Exception ex) { status.Text = "停止 · " + Experiment.Failure(ex); }
            finally { run = null; Finished(); }
        }
    }
    private string Watch(string local, string other, PrivateRunControl active)
    {
        using (var store = new PrivateStore(folder))
        {
            var proof = store.State.Binding;
            if (proof == null || proof.BotQq != local || proof.PeerQq != other)
                throw new InvalidOperationException("尚无这两个号码的已核验绑定");
            using (var transport = new QqPrivateTransport(proof, active.Token.ThrowIfCancellationRequested, Log))
            {
                var core = new PrivateReplyCore(store, transport, new FixedPrivateReply(item => "已收到，谢谢。"), active, row => true, Log);
                core.Start(local, other);
                if (core.RecoveryBlocked) return Summary(store, "上次已提交的回复待确认，发送保持停止");
                Report(Summary(store, "监听已绑定私聊"));
                try
                {
                    while (!active.Token.WaitHandle.WaitOne(400))
                    {
                        if (active.Paused) continue;
                        try { core.Tick(); Report(Summary(store, "监听已绑定私聊")); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            var reason = Experiment.Failure(ex);
                            Log("halt " + reason);
                            if (core.RecoveryBlocked) return Summary(store, "提交结果待确认 · " + reason);
                            active.Pause();
                            Report(Summary(store, "已暂停 · " + reason));
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally { core.StopUnsubmitted(); }
                return Summary(store, "已停止");
            }
        }
    }
}
