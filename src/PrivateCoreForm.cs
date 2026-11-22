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
    private readonly Button start = new Button { Text = "启动", AutoSize = true };
    private readonly Button pause = new Button { Text = "暂停", AutoSize = true };
    private readonly Button stop = new Button { Text = "停止", AutoSize = true };
    private readonly Button configure = new Button { Text = "模型设置", AutoSize = true };
    private readonly Button clearContext = new Button { Text = "清空上下文", AutoSize = true };
    private readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly object logGate = new object();
    private PrivateRunControl run;
    private CancellationTokenSource enrollment;
    private bool closePending;

    internal PrivateCoreForm(string folder)
    {
        this.folder = folder;
        Text = "QQ 自动回复 · 单私聊 AI";
        ClientSize = new Size(605, 432);
        MinimumSize = new Size(590, 446);
        StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 214, Padding = new Padding(8),
            ColumnCount = 2, RowCount = 6, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        for (int row = 0; row < 6; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var labels = new[] { "机器人登录 QQ 号", "允许回复的对象 QQ 号", "机器人的登录显示名",
            "私聊会话显示名", "QQ 窗口句柄（十进制）" };
        for (int i = 0; i < labels.Length; i++)
        {
            grid.Controls.Add(new Label { Text = labels[i], Anchor = AnchorStyles.Left, AutoSize = true }, 0, i);
            fields[i].Dock = DockStyle.Fill;
            grid.Controls.Add(fields[i], 1, i);
        }
        attested.Anchor = AnchorStyles.Left;
        grid.Controls.Add(attested, 0, 5);
        grid.SetColumnSpan(attested, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(6) };
        mode.Items.AddRange(new[] { "固定回复", "模型回复" });
        mode.SelectedIndex = 0;
        buttons.Controls.AddRange(new Control[] { bind, mode, start, pause, stop, configure, clearContext });
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
        configure.Click += (s, e) => { using (var dialog = new ModelConfigForm(ModelPath, Log)) dialog.ShowDialog(this); };
        clearContext.Click += (s, e) => {
            if (MessageBox.Show(this, "清空此私聊的模型上下文？收发状态和核验绑定会保留。", "清空上下文",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            try
            {
                using (var store = new PrivateStore(folder)) store.ClearModelContext();
                status.Text = "已清空模型上下文。";
            }
            catch (Exception ex) { status.Text = "清空失败 · " + Experiment.Failure(ex); }
        };
        mode.SelectedIndexChanged += (s, e) => {
            status.Text = mode.SelectedIndex == 0 ? "固定回复模式。点击启动后开始监听。" :
                "模型回复模式。启动后，已绑定私聊的新文字和设置范围内的上下文会发送给所配置的 API。";
        };
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
    private string ModelPath { get { return Path.Combine(folder, "model-settings.dpapi"); } }
    private void UpdateButtons()
    {
        var idle = run == null && enrollment == null;
        bind.Enabled = start.Enabled = attested.Enabled = configure.Enabled = clearContext.Enabled = mode.Enabled = idle;
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
        Directory.CreateDirectory(folder);
        lock (logGate)
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
                            marker, pending.Token, text => {
                                Log(text);
                                if (text == "enrollment baseline established") Report("请由已核对的对象发送 " + marker);
                            });
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
        var useModel = mode.SelectedIndex == 1;
        using (var active = new PrivateRunControl())
        {
            run = active;
            pause.Text = "暂停";
            status.Text = "复核绑定和启动基线…";
            UpdateButtons();
            try { status.Text = await Task.Run(() => Watch(local, other, useModel, active)); }
            catch (OperationCanceledException) { status.Text = "已停止"; }
            catch (Exception ex) { status.Text = "停止 · " + ModelConfigForm.DisplayError(ex); }
            finally { run = null; Finished(); }
        }
    }
    private string Watch(string local, string other, bool useModel, PrivateRunControl active)
    {
        using (var store = new PrivateStore(folder))
        {
            var proof = store.State.Binding;
            if (proof == null || proof.BotQq != local || proof.PeerQq != other)
                throw new InvalidOperationException("尚无这两个号码的已核验绑定");
            using (var modelReply = useModel ? new DeepSeekPrivateReply(ModelSettingsFile.Load(ModelPath),
                () => store.State.ModelTurns, Log) : null)
            using (var transport = new QqPrivateTransport(proof, active.Token.ThrowIfCancellationRequested, Log))
            {
                IPrivateReplyProvider reply = modelReply == null ? (IPrivateReplyProvider)new FixedPrivateReply(item => "已收到，谢谢。") : modelReply;
                var core = new PrivateReplyCore(store, transport, reply, active, row => true, Log);
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
                            var reason = ModelConfigForm.DisplayError(ex);
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
