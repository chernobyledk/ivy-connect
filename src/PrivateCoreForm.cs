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
    private readonly Predicate<ReplyRow> acceptIncoming;
    private readonly TextBox[] fields = Enumerable.Range(0, 5).Select(i => new TextBox()).ToArray();
    private readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly Label connectionState = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 6, 0, 0) };
    private readonly Panel diagnostics = new Panel { Dock = DockStyle.Top, Height = 29, Visible = false };
    private readonly CheckBox attested = new CheckBox { Text = "已从 QQ 资料卡核对两端号码，并打开此私聊", AutoSize = true };
    private readonly Button bind = new Button { Text = "核验并绑定", AutoSize = true };
    private readonly Button start = new Button { Text = "开始回复", AutoSize = true };
    private readonly Button pause = new Button { Text = "暂停", AutoSize = true };
    private readonly Button stop = new Button { Text = "停止", AutoSize = true };
    private readonly Button configure = new Button { Text = "模型设置", AutoSize = true };
    private readonly Button clearContext = new Button { Text = "清空上下文", AutoSize = true };
    private readonly Button findQq = new Button { Text = "连接 QQ", AutoSize = true };
    private readonly Button pickQq = new Button { Text = "选择 QQ 窗口", AutoSize = true };
    private readonly Button advanced = new Button { Text = "高级诊断", AutoSize = true };
    private readonly Button showQq = new Button { Text = "显示 QQ", AutoSize = true };
    private readonly Button sideQq = new Button { Text = "侧边摆放", AutoSize = true };
    private readonly Button restoreQq = new Button { Text = "恢复原位置", AutoSize = true };
    private readonly QqPlacement placement = new QqPlacement();
    private readonly NotifyIcon tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "QQ 单私聊回复" };
    private readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly object logGate = new object();
    private PrivateRunControl run;
    private CancellationTokenSource enrollment;
    private CancellationTokenSource discovery;
    private bool closePending;
    private bool connecting;
    private QqCandidate selectedCandidate;

    internal PrivateCoreForm(string folder, Predicate<ReplyRow> acceptIncoming = null)
    {
        this.folder = folder;
        this.acceptIncoming = acceptIncoming ?? (_ => true);
        Text = "QQ 自动回复 · 单私聊 AI v" + Application.ProductVersion;
        ClientSize = new Size(680, 466);
        MinimumSize = new Size(620, 480);
        StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 177, Padding = new Padding(8),
            ColumnCount = 2, RowCount = 5, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        for (int row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var labels = new[] { "机器人登录 QQ 号", "允许回复的对象 QQ 号", "机器人的登录显示名",
            "私聊会话显示名" };
        for (int i = 0; i < labels.Length; i++)
        {
            grid.Controls.Add(new Label { Text = labels[i], Anchor = AnchorStyles.Left, AutoSize = true }, 0, i);
            fields[i].Dock = DockStyle.Fill;
            grid.Controls.Add(fields[i], 1, i);
        }
        attested.Anchor = AnchorStyles.Left;
        grid.Controls.Add(attested, 0, 4);
        grid.SetColumnSpan(attested, 2);
        fields[4].ReadOnly = true;
        fields[4].Width = 175;
        fields[4].Location = new Point(148, 2);
        diagnostics.Controls.Add(new Label { Text = "当前窗口句柄（十进制）", AutoSize = true, Location = new Point(8, 6) });
        diagnostics.Controls.Add(fields[4]);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(6) };
        mode.Items.AddRange(new[] { "固定回复", "模型回复" });
        mode.SelectedIndex = 0;
        buttons.Controls.AddRange(new Control[] { bind, mode, start, pause, stop, configure, clearContext });
        var location = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(6, 4, 6, 0) };
        location.Controls.AddRange(new Control[] { findQq, pickQq, showQq, sideQq, restoreQq, advanced });
        Controls.Add(status);
        Controls.Add(buttons);
        Controls.Add(location);
        Controls.Add(diagnostics);
        Controls.Add(grid);
        Controls.Add(connectionState);
        SetConnection("未连接", "请在机器人 QQ 打开目标私聊，然后点击「连接 QQ」或「开始回复」。");
        try
        {
            PrivateProfile.Migrate(folder);
            using (var store = new PrivateStore(folder))
            {
                var p = store.State.Binding;
                if (p != null)
                {
                    var saved = new[] { p.BotQq, p.PeerQq, p.BotDisplay, p.PeerDisplay };
                    for (int i = 0; i < saved.Length; i++) fields[i].Text = saved[i];
                    status.Text = Summary(store, "已有核验记录。在机器人 QQ 打开原私聊后，点击「连接 QQ」或「开始回复」。");
                }
            }
            Log("startup version=" + Application.ProductVersion + " pid=" + System.Diagnostics.Process.GetCurrentProcess().Id);
        }
        catch (Exception ex) { SetConnection("未连接", "状态不可读取。" + ModelConfigForm.DisplayError(ex)); }
        findQq.Click += async (s, e) => await LocateQq();
        pickQq.Click += async (s, e) => await SelectCandidate();
        advanced.Click += (s, e) => {
            diagnostics.Visible = !diagnostics.Visible;
            advanced.Text = diagnostics.Visible ? "收起诊断" : "高级诊断";
        };
        showQq.Click += async (s, e) => await ArrangeQq("show");
        sideQq.Click += async (s, e) => await ArrangeQq("side");
        restoreQq.Click += async (s, e) => await ArrangeQq("restore");
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示控制窗", null, (s, e) => ShowControl());
        menu.Items.Add("停止自动回复", null, (s, e) => RequestStop());
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowControl();
        Resize += (s, e) => {
            if (WindowState != FormWindowState.Minimized) return;
            tray.Visible = true;
            Hide();
        };
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
            if (!connecting && run == null && enrollment == null) return;
            e.Cancel = true; closePending = true; RequestStop();
        };
        FormClosed += (s, e) => {
            try { if (placement.Restore()) Log("layout restored at close"); }
            catch (Exception ex) { Log("layout restore failed " + ModelConfigForm.DisplayError(ex)); }
            tray.Dispose();
            menu.Dispose();
        };
        UpdateButtons();
    }

    private void ShowControl()
    {
        Show();
        WindowState = FormWindowState.Normal;
        tray.Visible = false;
        Activate();
    }

    private PrivateBindingProof SavedBinding()
    {
        using (var store = new PrivateStore(folder)) return store.State.Binding;
    }

    private void SetConnection(string state, string detail)
    {
        connectionState.Text = "连接状态  " + state;
        status.Text = detail;
    }

    private void ReportConnection(QqConnectionResult result)
    {
        string state;
        switch (result.State)
        {
            case QqConnectState.Ready: state = "已就绪"; break;
            case QqConnectState.NeedsLogin: state = "需要登录"; break;
            case QqConnectState.NeedsChat: state = "需要打开目标私聊"; break;
            case QqConnectState.NeedsVerification: state = "需要核验"; break;
            case QqConnectState.ChooseWindow: state = "需要选择窗口"; break;
            case QqConnectState.Hidden: state = "QQ 在托盘隐藏"; break;
            case QqConnectState.Minimized: state = "QQ 已最小化"; break;
            case QqConnectState.Dialog: state = "QQ 有待处理提示"; break;
            case QqConnectState.Reconnecting: state = "QQ 未连通"; break;
            case QqConnectState.Draft: state = "需要处理草稿"; break;
            default: state = "未连接"; break;
        }
        SetConnection(state, result.Detail);
    }

    private async Task<PrivateBindingProof> LocateQq()
    {
        var local = fields[0].Text.Trim();
        var peer = fields[1].Text.Trim();
        connecting = true;
        SetConnection("检查中", "正在查找本机 QQ 窗口并核验原私聊…");
        fields[4].Clear();
        using (var pending = new CancellationTokenSource())
        {
            discovery = pending;
            UpdateButtons();
            try
            {
                var result = await Task.Run(() => {
                    var proof = SavedBinding();
                    if (proof != null && (proof.BotQq != local || proof.PeerQq != peer))
                        return new QqConnectionResult(QqConnectState.NeedsVerification,
                            "当前填写的双方账号与已核验记录不同。请依据 QQ 资料卡检查机器人与对端账号。",
                            new QqCandidate[0]);
                    pending.Token.ThrowIfCancellationRequested();
                    if (QqConnection.LaunchIfNotRunning())
                        return new QqConnectionResult(QqConnectState.NeedsLogin,
                            "已打开 QQ。请登录或完成验证，在机器人账号中打开目标私聊后重试。", new QqCandidate[0]);
                    var found = QqConnection.Connect(proof, pending.Token.ThrowIfCancellationRequested, Log);
                    if (found.Binding != null && !ReferenceEquals(proof, found.Binding))
                    {
                        using (var store = new PrivateStore(folder))
                        {
                            pending.Token.ThrowIfCancellationRequested();
                            if (store.State.Binding == null || proof.RouteStamp() != store.State.Binding.RouteStamp())
                                throw new PrivateRouteLostException("核验记录同时发生变化，请重新连接");
                            store.ReplaceBinding(found.Binding);
                            Log("route window relocated; previous unsubmitted tasks cancelled");
                        }
                    }
                    return found;
                }, pending.Token);
                pending.Token.ThrowIfCancellationRequested();
                ReportConnection(result);
                if (result.Binding != null) fields[4].Text = result.Binding.Window.ToString();
                return result.Binding;
            }
            catch (OperationCanceledException) { SetConnection("未连接", "连接检查已取消。"); }
            catch (Exception ex) { SetConnection("未连接", "连接未完成。" + ModelConfigForm.DisplayError(ex)); }
            finally { discovery = null; connecting = false; Finished(); }
        }
        return null;
    }

    private async Task SelectCandidate()
    {
        connecting = true;
        selectedCandidate = null;
        using (var pending = new CancellationTokenSource())
        {
            discovery = pending;
            UpdateButtons();
            try
            {
                var list = await Task.Run(() => QqConnection.Collect(SavedBinding(), pending.Token.ThrowIfCancellationRequested, Log), pending.Token);
                if (closePending || pending.IsCancellationRequested) return;
                if (list.Length == 0) { SetConnection("未连接", "未发现可选择的 QQ 窗口。请打开并登录 QQ。"); return; }
                using (var dialog = new Form { Text = "选择机器人所在的 QQ 窗口", Width = 760, Height = 290, StartPosition = FormStartPosition.CenterParent })
                {
                    var info = new Label { Dock = DockStyle.Top, Height = 40, Padding = new Padding(8),
                        Text = "按登录显示名和当前会话选择窗口。核验时请以 QQ 资料卡上的号码为准。" };
                    var picker = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true };
                    picker.Items.AddRange(list);
                    var ok = new Button { Dock = DockStyle.Bottom, Text = "选择并继续核验", DialogResult = DialogResult.OK };
                    dialog.Controls.Add(picker);
                    dialog.Controls.Add(ok);
                    dialog.Controls.Add(info);
                    dialog.AcceptButton = ok;
                    if (dialog.ShowDialog(this) != DialogResult.OK || picker.SelectedIndex < 0) return;
                    selectedCandidate = list[picker.SelectedIndex];
                    fields[4].Text = selectedCandidate.Window.ToString();
                    attested.Checked = false;
                    SetConnection("需要核验", "请核对双方 QQ 资料卡，保持原私聊展开，勾选确认后点击「核验并绑定」。");
                }
            }
            catch (OperationCanceledException) { SetConnection("未连接", "窗口查找已取消。"); }
            catch (Exception ex) { SetConnection("未连接", ModelConfigForm.DisplayError(ex)); }
            finally { discovery = null; connecting = false; Finished(); }
        }
    }

    private async Task ArrangeQq(string command)
    {
        connecting = true;
        UpdateButtons();
        try
        {
            var proof = SavedBinding();
            var result = await Task.Run(() => {
                if (command == "restore" && !placement.Restore()) return "没有待恢复的摆放记录。";
                if (command == "show") QqPlacement.Show(QqConnection.Pinned(proof));
                if (command == "side")
                {
                    QqConnection.VerifyUnique(proof, () => { }, Log);
                    placement.Side(QqConnection.Pinned(proof));
                }
                Log("layout " + command + " applied; running=no");
                try { QqConnection.VerifyUnique(proof, () => { }, Log); }
                catch
                {
                    if (command == "side") placement.Restore();
                    throw;
                }
                return "窗口已整理，原绑定复核通过。";
            });
            status.Text = result;
        }
        catch (Exception ex) { status.Text = "窗口检查未通过 · " + ModelConfigForm.DisplayError(ex); }
        finally { connecting = false; Finished(); }
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
        var idle = run == null && enrollment == null && !connecting;
        bind.Enabled = start.Enabled = attested.Enabled = configure.Enabled = clearContext.Enabled = mode.Enabled = idle;
        foreach (var field in fields) field.Enabled = idle;
        findQq.Enabled = pickQq.Enabled = showQq.Enabled = sideQq.Enabled = restoreQq.Enabled = idle;
        pause.Enabled = run != null && !run.Token.IsCancellationRequested;
        stop.Enabled = !idle;
    }
    private void RequestStop()
    {
        if (enrollment != null) enrollment.Cancel();
        if (discovery != null) discovery.Cancel();
        if (run != null) run.Stop();
        status.Text = "正在停止，禁止新的提交。";
        UpdateButtons();
    }
    private void Report(string text, string state = null)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(() => {
            if (run != null && !run.Token.IsCancellationRequested || enrollment != null)
            {
                if (state != null) SetConnection(state, text);
                else status.Text = text;
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
        var a = fields.Select(x => x.Text.Trim()).ToArray();
        var chosen = selectedCandidate;
        if (!attested.Checked)
        {
            SetConnection("需要核验", "请核对两张 QQ 资料卡，再勾选确认。无需输入窗口句柄。");
            return;
        }
        using (var pending = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
        {
            enrollment = pending;
            SetConnection("核验中", "正在重新查找窗口并建立只读基线…");
            UpdateButtons();
            try
            {
                var verified = await Task.Run(() => {
                    QqPrivateTransport.CheckNumbers(a[0], a[1]);
                    using (var store = new PrivateStore(folder))
                    {
                        var prior = store.State.Binding;
                        if (prior != null && (prior.BotQq != a[0] || prior.PeerQq != a[1]))
                            throw new InvalidOperationException("此状态目录只允许原核验账号对");
                        var candidates = QqConnection.Collect(prior, pending.Token.ThrowIfCancellationRequested, Log)
                            .Where(candidate => candidate.CanVerify(a[2], a[3]) && (chosen == null || candidate.Epoch == chosen.Epoch)).ToArray();
                        if (candidates.Length != 1)
                            throw new InvalidOperationException(candidates.Length > 1 ?
                                "请先使用「选择 QQ 窗口」，确认登录显示名与目标私聊" :
                                "当前未找到符合已核对显示名、私聊和空草稿条件的窗口。请打开机器人 QQ 主窗中的原私聊");
                        var marker = "QQ验证绑定 " + Guid.NewGuid().ToString("N").Substring(0, 12).ToUpperInvariant();
                        var next = QqPrivateTransport.Enroll(candidates[0].Window, a[0], a[1], a[2], a[3],
                            marker, pending.Token, text => {
                                Log(text);
                                if (text == "enrollment baseline established") Report("请由已核对的对象发送 " + marker);
                            });
                        pending.Token.ThrowIfCancellationRequested();
                        store.ReplaceBinding(next);
                        Log("enrollment complete");
                        return next;
                    }
                });
                selectedCandidate = null;
                fields[4].Text = verified.Window.ToString();
                SetConnection("已就绪", "已保存核验绑定。点击「开始回复」后才会监听新消息。");
            }
            catch (OperationCanceledException) { SetConnection("需要核验", "核验已取消或超时。"); }
            catch (Exception ex) { SetConnection("需要核验", "核验暂停。" + ModelConfigForm.DisplayError(ex)); }
            finally { enrollment = null; Finished(); }
        }
    }

    private async Task StartCore()
    {
        var useModel = mode.SelectedIndex == 1;
        var connected = await LocateQq();
        if (connected == null || closePending || IsDisposed) return;
        using (var active = new PrivateRunControl())
        {
            run = active;
            pause.Text = "暂停";
            SetConnection("已就绪", "正在重新建立消息基线…");
            UpdateButtons();
            try { SetConnection("已停止", await Task.Run(() => Watch(connected, useModel, active))); }
            catch (OperationCanceledException) { SetConnection("已停止", "请点击「开始回复」再次核验并启动。"); }
            catch (Exception ex) { SetConnection("未连接", "启动已停止。" + ModelConfigForm.DisplayError(ex)); }
            finally { run = null; Finished(); }
        }
    }
    private string Watch(PrivateBindingProof connected, bool useModel, PrivateRunControl active)
    {
        using (var store = new PrivateStore(folder))
        {
            var proof = store.State.Binding;
            if (proof == null || proof.RouteStamp() != connected.RouteStamp())
                throw new PrivateRouteLostException("连接后核验记录已变更，请重新连接原私聊");
            using (var modelReply = useModel ? new DeepSeekPrivateReply(ModelSettingsFile.Load(ModelPath),
                () => store.State.ModelTurns, Log) : null)
            using (var transport = new QqPrivateTransport(proof, active.Token.ThrowIfCancellationRequested, Log))
            {
                IPrivateReplyProvider reply = modelReply == null ? (IPrivateReplyProvider)new FixedPrivateReply(item => "已收到，谢谢。") : modelReply;
                var core = new PrivateReplyCore(store, transport, reply, active, acceptIncoming, Log);
                core.Start(proof.BotQq, proof.PeerQq);
                if (core.RecoveryBlocked) return Summary(store, "上次已提交的回复待确认，发送保持停止");
                Report(Summary(store, "监听已绑定私聊"), "运行中");
                try
                {
                    while (!active.Token.WaitHandle.WaitOne(400))
                    {
                        if (active.Paused) continue;
                        try { core.Tick(); Report(Summary(store, "监听已绑定私聊"), "运行中"); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            var reason = ModelConfigForm.DisplayError(ex);
                            Log("halt " + reason);
                            if (core.RecoveryBlocked) return Summary(store, "提交结果待确认 · " + reason);
                            if (core.BindingInvalidated) return Summary(store, "旧绑定的未提交任务已取消 · " + reason);
                            active.Pause();
                            Report(Summary(store, "已暂停 · " + reason), "暂停");
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
