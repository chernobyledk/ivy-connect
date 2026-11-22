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
    private readonly CheckBox attested = new CheckBox { Text = "已从 QQ 资料卡核对双方号码，当前打开的是这个私聊", AutoSize = true };
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
    private readonly Button showCover = new Button { Text = "显示遮罩", AutoSize = true };
    private readonly Button closeCover = new Button { Text = "关闭遮罩", AutoSize = true };
    private readonly Button chooseCover = new Button { Text = "选择遮罩图片", AutoSize = true };
    private readonly Button resetCover = new Button { Text = "恢复默认图片", AutoSize = true };
    private readonly Button recheckReceipt = new Button { Text = "重新核查", AutoSize = true };
    private readonly Button markPeerReceipt = new Button { Text = "确认对方已收到", AutoSize = true };
    private readonly Label boundChat = new Label { Dock = DockStyle.Top, Height = 26, Padding = new Padding(8, 5, 0, 0) };
    private readonly Label coverStatus = new Label { Dock = DockStyle.Bottom, Height = 25, Padding = new Padding(8, 4, 0, 0) };
    private readonly BindingPromptPanel bindPrompt = new BindingPromptPanel();
    private readonly PictureCoverController pictureCover;
    private readonly Icon launcherIcon;
    private readonly QqPlacement placement = new QqPlacement();
    private readonly NotifyIcon tray = new NotifyIcon { Text = "Ivy connect" };
    private readonly ComboBox mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
    private readonly object logGate = new object();
    private PrivateRunControl run;
    private CancellationTokenSource enrollment;
    private CancellationTokenSource discovery;
    private bool closePending;
    private bool connecting;
    private bool regenerateEnrollment;
    private bool restoreAfterStop;
    private QqCandidate selectedCandidate;
    private PrivateBindingProof liveProof;

    internal PrivateCoreForm(string folder, Predicate<ReplyRow> acceptIncoming = null)
    {
        this.folder = folder;
        this.acceptIncoming = acceptIncoming ?? (_ => true);
        Text = "Ivy connect v" + Application.ProductVersion;
        launcherIcon = PresentationAssets.ReadIcon();
        Icon = launcherIcon;
        tray.Icon = launcherIcon;
        ClientSize = new Size(720, 665);
        MinimumSize = new Size(640, 680);
        StartPosition = FormStartPosition.CenterScreen;
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 177, Padding = new Padding(8),
            ColumnCount = 2, RowCount = 5, GrowStyle = TableLayoutPanelGrowStyle.FixedSize };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        for (int row = 0; row < 5; row++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        var labels = new[] { "自动回复账号 QQ 号（电脑端）", "聊天对象账号 QQ 号", "自动回复账号昵称",
            "聊天对象昵称" };
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
        var masks = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 37, Padding = new Padding(6, 1, 6, 0) };
        masks.Controls.AddRange(new Control[] { showCover, closeCover, chooseCover, resetCover });
        var receipts = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(6, 1, 6, 0) };
        receipts.Controls.AddRange(new Control[] { recheckReceipt, markPeerReceipt });
        Controls.Add(status);
        Controls.Add(buttons);
        Controls.Add(location);
        Controls.Add(masks);
        Controls.Add(receipts);
        Controls.Add(coverStatus);
        Controls.Add(bindPrompt);
        Controls.Add(diagnostics);
        Controls.Add(grid);
        Controls.Add(boundChat);
        Controls.Add(connectionState);
        pictureCover = new PictureCoverController(folder, text => coverStatus.Text = text);
        DisplayBinding(null, false);
        SetConnection("未连接", "请在自动回复账号中打开目标私聊，然后点击「连接 QQ」或「开始回复」。");
        try
        {
            PrivateProfile.Migrate(folder);
            using (var store = new PrivateStore(folder))
            {
                var p = store.State.Binding;
                if (p != null)
                {
                    ReadLegacySubmissionTime(store.State.Events.FirstOrDefault(PrivateStore.IsPending));
                    var saved = new[] { p.BotQq, p.PeerQq, p.BotDisplay, p.PeerDisplay };
                    for (int i = 0; i < saved.Length; i++) fields[i].Text = saved[i];
                    DisplayBinding(p, false);
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
        showCover.Click += async (s, e) => await ShowPictureCover();
        closeCover.Click += (s, e) => pictureCover.Close();
        chooseCover.Click += (s, e) => ChoosePicture();
        resetCover.Click += (s, e) => ChangePicture(() => pictureCover.Reset());
        recheckReceipt.Click += async (s, e) => await CheckPendingReceipt();
        markPeerReceipt.Click += (s, e) => ConfirmPeerReceipt();
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示控制窗", null, (s, e) => ShowControl());
        menu.Items.Add("停止自动回复", null, (s, e) => RequestStop());
        menu.Items.Add("显示 QQ", null, async (s, e) => await ArrangeQq("show"));
        menu.Items.Add("关闭遮罩", null, (s, e) => pictureCover.Close());
        menu.Items.Add("恢复原位置", null, async (s, e) => await ArrangeQq("restore"));
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowControl();
        Resize += (s, e) => {
            if (WindowState != FormWindowState.Minimized) return;
            tray.Visible = true;
            Hide();
        };
        bind.Click += async (s, e) => await Enroll();
        bindPrompt.Regenerate += async (s, e) => {
            bindPrompt.End("正在生成新验证码…");
            if (enrollment == null) await Enroll();
            else { regenerateEnrollment = true; enrollment.Cancel(); }
        };
        bindPrompt.Cancel += (s, e) => {
            regenerateEnrollment = false;
            bindPrompt.End();
            if (enrollment != null) enrollment.Cancel();
        };
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
            pictureCover.Dispose();
            try { if (placement.Restore()) Log("layout restored at close"); }
            catch (Exception ex) { Log("layout restore failed " + ModelConfigForm.DisplayError(ex)); }
            tray.Dispose();
            menu.Dispose();
            launcherIcon.Dispose();
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

    private void DisplayBinding(PrivateBindingProof proof, bool connected)
    {
        boundChat.Text = proof == null ? "已绑定会话 · 暂无" :
            (connected ? "已绑定会话 · " : "已保存会话（待连接复核） · ") + proof.PeerDisplay + "（" + proof.PeerQq + "）";
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
                if (result.Binding != null)
                {
                    fields[4].Text = result.Binding.Window.ToString();
                    DisplayBinding(result.Binding, true);
                }
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
        if (command == "show") pictureCover.Close();
        if (run != null)
        {
            if (command == "show")
            {
                try { QqPlacement.Show(QqConnection.Pinned(liveProof)); }
                catch (Exception ex) { coverStatus.Text = ModelConfigForm.DisplayError(ex); }
            }
            else if (command == "restore")
            {
                restoreAfterStop = true;
                RequestStop();
            }
            return;
        }
        if (connecting || enrollment != null) return;
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

    private async Task ShowPictureCover()
    {
        try
        {
            var proof = run != null ? liveProof : await LocateQq();
            if (proof != null && !IsDisposed && !closePending) pictureCover.Show(QqConnection.Pinned(proof));
        }
        catch (Exception ex) { coverStatus.Text = "遮罩未打开 · " + ModelConfigForm.DisplayError(ex); }
    }

    private void ChoosePicture()
    {
        using (var picker = new OpenFileDialog { Title = "选择遮罩图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*" })
            if (picker.ShowDialog(this) == DialogResult.OK) ChangePicture(() => pictureCover.UseCustom(picker.FileName));
    }

    private void ChangePicture(Action change)
    {
        try { change(); }
        catch (Exception) { coverStatus.Text = "图片无法读取或保存，保留原遮罩设置。"; }
    }

    private static string Summary(PrivateStore store, string head)
    {
        var events = store.State.Events;
        var pending = events.FirstOrDefault(PrivateStore.IsPending);
        return head + Environment.NewLine + "观察 " + events.Length + " · 本地确认 " +
            events.Count(x => x.Phase == PrivatePhase.LocalConfirmed) + " · 对方确认 " +
            events.Count(x => !String.IsNullOrEmpty(x.PeerConfirmedUtc)) + " · 待确认 " + events.Count(PrivateStore.IsPending) +
            " · 明确失败 " + events.Count(x => x.Phase == PrivatePhase.Failed) + " · 取消 " + events.Count(x => x.Phase == PrivatePhase.Cancelled) +
            (pending == null ? "" : Environment.NewLine + ReceiptDescription(store.State.Binding, pending) + Environment.NewLine +
            "请点「重新核查」，或到对方客户端核对后点「确认对方已收到」。");
    }

    private static string ReceiptDescription(PrivateBindingProof proof, PrivateEvent item)
    {
        var reply = (item.Reply ?? "").Trim().Replace('\r', ' ').Replace('\n', ' ');
        if (reply.Length > 110) reply = reply.Substring(0, 110) + "…";
        var time = item.Submission == null ? item.LegacySubmittedUtc : item.Submission.IntentUtc;
        DateTimeOffset stamp;
        var moment = DateTimeOffset.TryParse(time ?? item.ObservedUtc, out stamp) ? stamp.ToLocalTime().ToString("MM-dd HH:mm:ss") : "时间不详";
        string cause = item.Detail == "restart-no-reliable-match-no-resend" || item.Detail == "submission-started-no-retry" ?
            "旧版未找到足够的出站确认依据" : item.Detail == "durable-tail-before-default-action" || item.Detail == "durable-before-default-action" ?
            "提交窗口内程序退出，动作结果未核实" : item.Detail;
        var recipient = proof != null && proof.PeerQq == item.PeerQq ? proof.PeerDisplay + "（" + item.PeerQq + "）" : item.PeerQq;
        return "任务 " + item.LocalId + " · " + recipient + " · " + (time == null ? "观察 " : "提交前记录 ") + moment +
            "\r\n回复摘要　" + reply + "\r\n待核查原因　" + cause;
    }

    private void ReadLegacySubmissionTime(PrivateEvent item)
    {
        if (item == null || item.Submission != null || !String.IsNullOrEmpty(item.LegacySubmittedUtc)) return;
        try
        {
            var logPath = Path.Combine(folder, "app.log");
            if (!File.Exists(logPath)) return;
            var needle = "timing id=" + item.LocalId + " stage=submit ";
            var proof = File.ReadLines(logPath).Where(line => line.Contains(needle)).Take(2).ToArray();
            DateTimeOffset timestamp;
            if (proof.Length == 1 && DateTimeOffset.TryParse(proof[0].Split(' ')[0], out timestamp))
                item.LegacySubmittedUtc = timestamp.ToString("O");
        }
        catch (IOException) { }
    }

    private async Task CheckPendingReceipt()
    {
        connecting = true;
        using (var cancel = new CancellationTokenSource())
        {
            discovery = cancel;
            UpdateButtons();
            try
            {
                SetConnection("只读核查", "正在核验原私聊和待确认任务…");
                var message = await Task.Run(() => {
                    using (var store = new PrivateStore(folder))
                    {
                        var item = store.State.Events.FirstOrDefault(PrivateStore.IsPending);
                        if (item == null) return Summary(store, "没有待确认的任务。");
                        ReadLegacySubmissionTime(item);
                        var proof = store.State.Binding;
                        if (proof == null || proof.PeerQq != item.PeerQq || proof.BotQq != item.BotQq)
                            return Summary(store, "请恢复任务的原账号和原私聊，再核查。");
                        var result = QqConnection.Resolve(proof, QqConnection.Collect(proof, cancel.Token.ThrowIfCancellationRequested, Log), true);
                        if (result.Binding == null) return Summary(store, result.Detail);
                        using (var wire = new QqPrivateTransport(result.Binding, cancel.Token.ThrowIfCancellationRequested, Log, true))
                        {
                            string reason;
                            if (!ReceiptReview.Find(store, wire, item, cancel.Token, 14000,
                                text => BeginInvoke(new Action(() => status.Text = text)), out reason)) return Summary(store, reason);
                            cancel.Token.ThrowIfCancellationRequested();
                            store.ConfirmLocal(item, reason);
                            Log("local-id=" + item.LocalId + " phase=LocalConfirmed source=read-only-recheck detail=" + reason);
                            return Summary(store, "已核查到本地新出站气泡。可重新连接后点击「开始回复」。");
                        }
                    }
                }, cancel.Token);
                SetConnection("已完成核查", message);
            }
            catch (OperationCanceledException) { SetConnection("未连接", "已取消核查，原任务已保留。"); }
            catch (Exception ex) { SetConnection("核查暂停", ModelConfigForm.DisplayError(ex)); }
            finally { discovery = null; connecting = false; Finished(); }
        }
    }

    private void ConfirmPeerReceipt()
    {
        PrivateEvent pending;
        string evidence;
        try
        {
            using (var store = new PrivateStore(folder))
            {
                pending = store.State.Events.FirstOrDefault(PrivateStore.IsPending);
                if (pending == null) { SetConnection("未连接", "没有待确认的任务。"); return; }
                ReadLegacySubmissionTime(pending);
                evidence = ReceiptDescription(store.State.Binding, pending);
            }
            if (MessageBox.Show(this, evidence + "\r\n\r\n请确认你已在对方 QQ 客户端看到这条具体回复。确认后只登记收件记录。",
                "Ivy connect · 人工收件确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            using (var store = new PrivateStore(folder))
            {
                var item = store.State.Events.Single(row => row.LocalId == pending.LocalId);
                item.LegacySubmittedUtc = pending.LegacySubmittedUtc;
                store.ConfirmPeer(item);
                Log("local-id=" + item.LocalId + " receipt=peer-confirmed source=" + item.PeerReceiptSource);
                SetConnection("已登记对方收件", Summary(store, "已记录人工收件确认。请重新连接原私聊，再点击「开始回复」。"));
            }
        }
        catch (Exception ex) { SetConnection("收件登记未完成", ModelConfigForm.DisplayError(ex)); }
    }
    private string ModelPath { get { return Path.Combine(folder, "model-settings.dpapi"); } }
    private void UpdateButtons()
    {
        var idle = run == null && enrollment == null && !connecting;
        bind.Enabled = start.Enabled = attested.Enabled = configure.Enabled = clearContext.Enabled = mode.Enabled = idle;
        foreach (var field in fields) field.Enabled = idle;
        findQq.Enabled = pickQq.Enabled = sideQq.Enabled = idle;
        recheckReceipt.Enabled = markPeerReceipt.Enabled = idle;
        showQq.Enabled = restoreQq.Enabled = showCover.Enabled = !connecting && enrollment == null;
        pause.Enabled = run != null && !run.Token.IsCancellationRequested;
        stop.Enabled = !idle;
    }
    private void RequestStop()
    {
        if (enrollment != null)
        {
            regenerateEnrollment = false;
            bindPrompt.End();
            enrollment.Cancel();
        }
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
        do
        {
            regenerateEnrollment = false;
            using (var pending = new CancellationTokenSource())
            {
                enrollment = pending;
                SetConnection("核验中", "正在检查账号、窗口及会话，并建立本轮消息基线…");
                UpdateButtons();
                bool issued = false;
                try
                {
                    var verified = await Task.Run(() => {
                        QqPrivateTransport.CheckNumbers(a[0], a[1]);
                        using (var store = new PrivateStore(folder))
                        {
                            var prior = store.State.Binding;
                            if (prior != null && (prior.BotQq != a[0] || prior.PeerQq != a[1]))
                                throw new PrivateRouteLostException("此状态目录只允许原核验账号对");
                            var candidates = QqConnection.Collect(prior, pending.Token.ThrowIfCancellationRequested, Log)
                                .Where(candidate => candidate.CanVerify(a[2], a[3]) && (chosen == null || candidate.Epoch == chosen.Epoch)).ToArray();
                            if (candidates.Length != 1)
                                throw new PrivateRouteLostException(candidates.Length > 1 ?
                                    "请先使用「选择 QQ 窗口」，确认登录显示名与目标私聊" :
                                    "没有找到符合资料卡核对条件且草稿为空的窗口，请打开自动回复账号的原私聊");
                            var previousStamp = prior == null ? null : prior.RouteStamp();
                            var next = QqPrivateTransport.EnrollNumeric(candidates[0].Window, a[0], a[1], a[2], a[3], store,
                                pending.Token, Log, challenge => PostEnrollment(pending, () => {
                                    issued = true;
                                    bindPrompt.ShowCode(challenge, a[2], a[0], a[3], a[1]);
                                    status.Text = "等待来自当前私聊的新验证码。";
                                }), failures => PostEnrollment(pending, () => bindPrompt.RecordFailure(failures)));
                            return Tuple.Create(next, previousStamp);
                        }
                    });
                    pending.Token.ThrowIfCancellationRequested();
                    using (var store = new PrivateStore(folder))
                    {
                        var prior = store.State.Binding;
                        if ((prior == null ? null : prior.RouteStamp()) != verified.Item2)
                            throw new PrivateRouteLostException("核验记录同时发生变化，请重新核验");
                        store.ReplaceBinding(verified.Item1);
                    }
                    Log("numeric enrollment complete");
                    selectedCandidate = null;
                    fields[4].Text = verified.Item1.Window.ToString();
                    DisplayBinding(verified.Item1, true);
                    bindPrompt.End();
                    SetConnection("已就绪", "已保存核验绑定。点击「开始回复」后才会监听新消息。");
                }
                catch (OperationCanceledException)
                {
                    if (!regenerateEnrollment) { bindPrompt.End(); SetConnection("需要核验", "已取消本次核验。"); }
                }
                catch (Exception ex)
                {
                    bindPrompt.End(issued ? "请点击「重新生成」以重试。" : null);
                    SetConnection("需要核验", "核验暂停。" + ModelConfigForm.DisplayError(ex));
                }
                finally { enrollment = null; }
            }
        } while (regenerateEnrollment && !closePending);
        Finished();
    }

    private void PostEnrollment(CancellationTokenSource attempt, Action update)
    {
        BeginInvoke(new Action(() => {
            if (ReferenceEquals(enrollment, attempt) && !attempt.IsCancellationRequested) update();
        }));
    }

    private async Task StartCore()
    {
        var useModel = mode.SelectedIndex == 1;
        var connected = await LocateQq();
        if (connected == null || closePending || IsDisposed) return;
        using (var active = new PrivateRunControl())
        {
            liveProof = connected;
            run = active;
            pause.Text = "暂停";
            SetConnection("已就绪", "正在重新建立消息基线…");
            UpdateButtons();
            try { SetConnection("已停止", await Task.Run(() => Watch(connected, useModel, active))); }
            catch (OperationCanceledException) { SetConnection("已停止", "请点击「开始回复」再次核验并启动。"); }
            catch (Exception ex) { SetConnection("未连接", "启动已停止。" + ModelConfigForm.DisplayError(ex)); }
            finally { run = null; liveProof = null; Finished(); }
        }
        if (restoreAfterStop && !closePending && !IsDisposed)
        {
            restoreAfterStop = false;
            await ArrangeQq("restore");
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
                var core = new PrivateReplyCore(store, transport, reply, active, acceptIncoming, Log,
                    progress: text => Report(text, "核查中"));
                core.Start(proof.BotQq, proof.PeerQq);
                if (core.RecoveryBlocked) return Summary(store, "已有提交需要核实，新的发送保持暂停。");
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
                            if (core.BindingInvalidated)
                            {
                                BeginInvoke(new Action(() => pictureCover.Close()));
                                return Summary(store, "旧绑定的未提交任务已取消 · " + reason);
                            }
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
