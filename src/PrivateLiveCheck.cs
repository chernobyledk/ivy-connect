using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

// Controlled test entry. App launches never take this path by default.
internal sealed class PrivateLiveCheck : Form
{
    private readonly string settings;
    private readonly PrivateRunControl control = new PrivateRunControl();
    private string logFile;
    private readonly object logGate = new object();
    private readonly QqPlacement placement = new QqPlacement();
    private PrivateBindingProof movedBinding;
    private Rectangle beforeMove;
    private bool finished;

    internal PrivateLiveCheck(string settings)
    {
        this.settings = settings;
        Text = "QQ 单私聊核心验证";
        ClientSize = new Size(284, 64);
        StartPosition = FormStartPosition.Manual;
        var stop = new Button { Text = "停止真机验证", Dock = DockStyle.Fill };
        stop.Click += (s, e) => control.Stop();
        Controls.Add(stop);
        Shown += async (s, e) => await Run();
        FormClosing += (s, e) => { if (!finished) { e.Cancel = true; control.Stop(); } };
        FormClosed += (s, e) => control.Dispose();
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    private void Log(string line)
    {
        lock (logGate)
            File.AppendAllText(logFile, DateTimeOffset.Now.ToString("O") + " " + line + Environment.NewLine, new UTF8Encoding(false));
    }

    private async Task Run()
    {
        try
        {
            var s = File.ReadAllLines(settings, Encoding.UTF8);
            if (s.Length < 8 || !Regex.IsMatch(s[6], "^[A-F0-9]{12}$") ||
                !new[] { "send", "send-side", "restart", "restart-real", "recover", "cleanup", "send-model-mock", "send-model-real" }.Contains(s[7]) ||
                s.Length != (s[7] == "send-model-mock" || s[7] == "send-model-real" ? 9 : 8))
                throw new InvalidOperationException("真机验证配置无效");
            if (s[7] == "send-model-mock")
            {
                Uri mock;
                if (!Uri.TryCreate(s[8], UriKind.Absolute, out mock) || !mock.IsLoopback || mock.Scheme != "http")
                    throw new InvalidOperationException("模拟接口必须使用本机回环地址");
            }
            var output = Path.GetFullPath(s[5]);
            Directory.CreateDirectory(output);
            logFile = Path.Combine(output, "live-" + s[6] + "-" + s[7] + ".log");
            using (File.Open(logFile, FileMode.CreateNew)) { }
            var isRestart = !s[7].StartsWith("send", StringComparison.Ordinal);
            PrivateBindingProof proof;
            using (var store = new PrivateStore(output))
            {
                proof = store.State.Binding;
                if (proof == null && !isRestart)
                {
                    proof = await Task.Run(() => QqPrivateTransport.ImportVerifiedRun(s[0], s[1]), control.Token);
                    store.State.Binding = proof;
                    store.Save();
                    Log("identity previous-demo-proof imported process/header/challenge confirmed");
                }
                if (proof == null) throw new InvalidOperationException("缺少重启试验的已验证绑定");
            }
            var target = Target.FromKnownHandle(proof.Window);
            if (isRestart)
            {
                var foreground = Native.GetForegroundWindow();
                using (var trace = new ForegroundTrace())
                {
                    var effects = new SideEffects();
                    Log("run pid=" + Process.GetCurrentProcess().Id + " mode=" + s[7] +
                        (s[7] == "cleanup" ? " exact-unsent-draft-cleanup" : " expanded-reader read-only"));
                    try
                    {
                        await Task.Run(() => Verify(s, output, true, proof, target, control.Token.ThrowIfCancellationRequested), control.Token);
                    }
                    finally
                    {
                        effects.Dispose();
                        Log("foreground-events count=" + trace.Count + " " + trace.Summary());
                        Log("input-evidence " + effects.Describe(false) + " original-foreground=" + foreground.ToInt64());
                    }
                }
                return;
            }
            await PrepareEditor(Target.FromKnownHandle(Int64.Parse(s[2])), s[3], s[4], "peer", control.Token, Log);
            await PrepareEditor(target, proof.PeerDisplay, proof.HeaderProof, "bot", control.Token, Log);
            if (s[7] == "send-side" || s[7] == "send-model-real")
            {
                await Task.Run(() => QqConnection.VerifyUnique(proof, control.Token.ThrowIfCancellationRequested, Log), control.Token);
                beforeMove = Native.Bounds(target.Hwnd);
                placement.Side(target);
                movedBinding = proof;
                await Task.Delay(350, control.Token);
                await Task.Run(() => QqConnection.VerifyUnique(proof, control.Token.ThrowIfCancellationRequested, Log), control.Token);
                Log("layout side applied and identity checked before=" + beforeMove + " after=" + Native.Bounds(target.Hwnd) +
                    " size-preserved=" + (beforeMove.Size == Native.Bounds(target.Hwnd).Size) + " measurement-not-started");
            }
            var outer = Native.Bounds(target.Hwnd);
            var work = Screen.FromHandle(target.Hwnd).WorkingArea;
            const int width = 285;
            var x = outer.Left - width;
            if (x < work.Left) x = outer.Right;
            if (x + width > work.Right) throw new InvalidOperationException("目标旁边没有专用便笺空间");
            Bounds = new Rectangle(x, work.Top + 215, width, 64);
            if (Native.GetForegroundWindow() != Handle) Activate(); // test-owned control, before measurement
            using (var cover = new PictureCoverController(output, Log))
            using (var pad = new TypingPad(new Rectangle(x, work.Top + 20, width, 175), automaticMarker: s[6]))
            {
                cover.Show(target);
                for (int i = 0; i < 60 && pad.Hwnd == IntPtr.Zero; i++) await Task.Delay(100, control.Token);
                var padWindow = pad.Hwnd;
                if (padWindow == IntPtr.Zero) throw new InvalidOperationException("专用便笺未启动");
                Native.SetForegroundWindow(padWindow);
                for (int i = 0; i < 60 &&
                    (Native.GetForegroundWindow() != padWindow || !Native.HasChildFocus(padWindow) || pad.AutomaticSteps == 0); i++)
                    await Task.Delay(100, control.Token);
                var coverWindow = cover.Window;
                if (coverWindow == IntPtr.Zero || Native.IsTopmost(coverWindow))
                    throw new InvalidOperationException("图片遮罩未建立为普通非置顶窗口");
                await Task.Delay(900, control.Token);
                using (var trace = new ForegroundTrace())
                {
                    var effects = new SideEffects();
                    var steps = pad.AutomaticSteps;
                    var losses = pad.AutomaticFocusLoss;
                    var changes = pad.AutomaticMismatch;
                    Log("run pid=" + Process.GetCurrentProcess().Id + " mode=" + s[7] + " condition=expanded-fully-covered" +
                        " geometry=" + outer + " dpi=" + Native.GetDpiForWindow(target.Hwnd) + " setup-activation=pad-only");
                    Action guard = () => {
                        control.Token.ThrowIfCancellationRequested();
                        bool front = Native.GetForegroundWindow() == padWindow, focus = Native.HasChildFocus(padWindow),
                            opened = !Native.IsIconic(target.Hwnd), shown = Native.Visible(coverWindow),
                            above = Native.Above(coverWindow, target.Hwnd), unchanged = Native.Bounds(target.Hwnd) == outer,
                            full = Rectangle.Intersect(Native.Bounds(coverWindow), outer) == outer;
                        if (!front || !focus || trace.Count != 0 || !opened || !shown || !above || !unchanged || !full)
                            throw new InvalidOperationException("验证条件变化 pad-front=" + front + " pad-focus=" + focus +
                                " focus-events=" + trace.Count + " open=" + opened + " cover-visible=" + shown +
                                " cover-above=" + above + " bounds-unchanged=" + unchanged + " full-cover=" + full);
                    };
                    try { await Task.Run(() => Verify(s, output, isRestart, proof, target, guard), control.Token); }
                    finally
                    {
                        effects.Dispose();
                        Log("foreground-events count=" + trace.Count + " " + trace.Summary());
                        Log("input-evidence " + effects.Describe(false) + " automated-pad-steps=" + (pad.AutomaticSteps - steps) +
                            " pad-focus-loss=" + (pad.AutomaticFocusLoss - losses) + " pad-text-mismatch=" +
                            (pad.AutomaticMismatch - changes) + " human-typing=not-tested");
                    }
                }
            }
            Log("restored cover=closed pad=closed qq-geometry-unchanged=" + (outer == Native.Bounds(target.Hwnd)));
        }
        catch (Exception ex) { if (logFile != null && File.Exists(logFile)) Log("halt " + ModelConfigForm.DisplayError(ex)); }
        finally
        {
            if (movedBinding != null)
            {
                try
                {
                    var target = QqConnection.Pinned(movedBinding);
                    QqPlacement.Show(target);
                    Thread.Sleep(230);
                    Log("layout shown identity hwnd=" + Task.Run(() => QqConnection.VerifyUnique(movedBinding, () => { }, Log)).GetAwaiter().GetResult());
                    if (placement.Restore())
                    {
                        Thread.Sleep(230);
                        Log("layout restored exact=" + (Native.Bounds(target.Hwnd) == beforeMove) + " identity hwnd=" +
                            Task.Run(() => QqConnection.VerifyUnique(movedBinding, () => { }, Log)).GetAwaiter().GetResult());
                    }
                }
                catch (Exception ex)
                {
                    try { placement.Restore(); } catch (Exception) { }
                    Log("layout restoration check halted " + ModelConfigForm.DisplayError(ex));
                }
            }
            finished = true;
            Close();
        }
    }

    internal static async Task PrepareEditor(Target target, string display, string header, string endpoint, CancellationToken token, Action<string> log)
    {
        var available = await Task.Run(() => {
            for (int attempt = 0; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var b = new Binding(target, false, true);
                    var ui = b.RequireMatch();
                    if (b.HeaderProof != header || ui.HeaderIdentity() != display || !ui.EmptyDraft() || ui.HasVisibleConnectionWarning())
                        throw new InvalidOperationException("测试准备阶段无法核验 " + endpoint + " 会话及空草稿");
                    return MsaaProbe.FindMappedEditor(target, ui, log) != null;
                }
                catch (ElementNotAvailableException)
                {
                    if (attempt == 2) throw;
                    log("setup " + endpoint + " transient UIA node: retry read only");
                    Thread.Sleep(250);
                }
                catch (System.Runtime.InteropServices.COMException ex)
                {
                    if (attempt == 2) throw;
                    log("setup " + endpoint + " COM read retry only hr=0x" + ex.HResult.ToString("X8"));
                    Thread.Sleep(250);
                }
            }
        }, token);
        if (available)
        {
            log("setup " + endpoint + " editor usable without activation");
            return;
        }
        if (Native.GetForegroundWindow() != target.Hwnd)
        {
            log("setup " + endpoint + " activation-call QQ result=" + Native.SetForegroundWindow(target.Hwnd));
            for (int i = 0; i < 12 && Native.GetForegroundWindow() != target.Hwnd; i++) await Task.Delay(100, token);
        }
        if (Native.GetForegroundWindow() != target.Hwnd)
            throw new InvalidOperationException("测试准备阶段 QQ 未成为前台 " + endpoint);
        await Task.Delay(850, token);
        await Task.Run(() => {
            for (int retry = 0; ; retry++)
            {
                try
                {
                    var binding = new Binding(target, false);
                    var ui = binding.RequireMatch();
                    if (binding.HeaderProof != header || ui.HeaderIdentity() != display || !ui.EmptyDraft())
                        throw new InvalidOperationException("测试准备阶段重新核对编辑区未通过 " + endpoint);
                    if (MsaaProbe.FindMappedEditor(target, ui) != null) return;
                    var bounds = ui.Editor.Current.BoundingRectangle;
                    if (bounds.IsEmpty) throw new InvalidOperationException("无法复核空编辑区边界");
                    Native.ClickVerifiedPoint(target.Hwnd, (int)(bounds.X + bounds.Width / 2),
                        (int)(bounds.Y + bounds.Height / 2), false);
                    log("setup " + endpoint + " click on verified empty editor; before measurement");
                    Thread.Sleep(280);
                    ui = binding.RequireMatch();
                    if (MsaaProbe.FindMappedEditor(target, ui, log) != null) return;
                    MsaaProbe.Describe(target, ui, log);
                    throw new InvalidOperationException("测试准备后仍未匹配 " + endpoint + " 编辑对象");
                }
                catch (ElementNotAvailableException)
                {
                    if (retry == 2) throw;
                    log("setup " + endpoint + " transient UIA element rebuilt");
                    Thread.Sleep(320);
                }
            }
        }, token);
    }

    private void Verify(string[] s, string output, bool restart, PrivateBindingProof proof, Target target, Action guard)
    {
        var realModel = s[7] == "send-model-real" || s[7] == "restart-real";
        using (var store = new PrivateStore(output))
        using (var transport = new QqPrivateTransport(proof, guard, Log))
        using (var model = s[7] == "send-model-real" ? new DeepSeekPrivateReply(ModelSettingsFile.Load(s[8]), () => store.State.ModelTurns, Log) :
            s[7] == "send-model-mock" ? new DeepSeekPrivateReply(new PrivateModelSettings {
            Endpoint = s[8], Model = "mock-qq", ApiKey = "test-only-placeholder-never-real",
            TimeoutSeconds = 8, ContextTurns = 2, RolePrompt = "Use short answers."
        }, () => store.State.ModelTurns, Log) : null)
        {
            if (s[7] == "cleanup")
            {
                transport.ClearExactTestDraft(store.State.Events.Single());
                return;
            }
            var scope = new ExternalPrivateScope(new[] { proof.Window.ToString(), s[6], "F", proof.BotQq, proof.PeerQq,
                proof.PeerDisplay, "operator-verified-private-by-qq-number", s[2], s[3] });
            IPrivateReplyProvider provider = model == null ? (IPrivateReplyProvider)new FixedPrivateReply(item => "核心验证 " + s[6] + " 已收到") : model;
            IPrivateTransport selected = restart ? (IPrivateTransport)new ReadOnlyTransport(transport) : transport;
            Func<int, string> prompts = number => number == 1 ?
                "请记住暗号「梅雨36」，只回复「记住了」。编号" + s[6] :
                "刚才的暗号是什么？只回复暗号。编号" + s[6];
            var core = new PrivateReplyCore(store, selected, provider, control,
                row => realModel ? row.Text == MessageSnapshot.Normalize(prompts(1)) ||
                    row.Text == MessageSnapshot.Normalize(prompts(2)) : scope.IsInput(row), Log);
            var recovering = s[7] == "recover";
            if (restart && !recovering &&
                (store.State.Events.Length != 2 || store.State.Events.Any(x => x.Phase != PrivatePhase.LocalConfirmed)))
                throw new InvalidOperationException("重启前的状态未满足两条确认收发");
            if (recovering && (store.State.Events.Length != 1 || store.State.Events[0].Phase != PrivatePhase.Uncertain))
                throw new InvalidOperationException("缺少待确认的单条真机事件");
            var driver = restart ? null : new ExternalPeerDriver(scope, target, Log, control.Token,
                number => store.State.Events[number - 1].Reply, expectedHeaderProof: s[4],
                inputForItem: realModel ? prompts : null);
            core.Start(proof.BotQq, proof.PeerQq);
            Log("baseline READY probe=" + scope.Marker);
            if (recovering)
            {
                if (core.RecoveryBlocked) throw new InvalidOperationException("回执在本地仍待确认，没有重发");
                var item = store.State.Events[0];
                var verifiedPeer = new Binding(Target.FromKnownHandle(Int64.Parse(s[2])), false);
                if (verifiedPeer.HeaderProof != s[4]) throw new InvalidOperationException("发件端的绑定变化");
                var peerUi = verifiedPeer.RequireMatch();
                var matches = peerUi.ReplyRows().Where(row => row.Text == MessageSnapshot.Normalize(item.Reply)).ToArray();
                if (peerUi.HeaderIdentity() != s[3] || matches.Length != 1 || matches[0].Own != false)
                    throw new InvalidOperationException("未能在独立账号确认这条回执");
                Log("PASS real uncertain recovery locally-confirmed=1 peer-receipt=1 new-submit=0");
                return;
            }
            try
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < (restart ? 9 : 110))
                {
                    guard();
                    if (driver != null)
                        driver.Poll(store.State.Events.Count(x => x.Phase == PrivatePhase.LocalConfirmed), guard);
                    core.Tick();
                    if (restart && store.State.Events.Length != 2)
                        throw new InvalidOperationException("重启后重复创建了事件");
                    if (driver != null && driver.Receipts == 2 && core.OwnIgnored == 2) break;
                    if (control.Token.WaitHandle.WaitOne(330)) control.Token.ThrowIfCancellationRequested();
                }
                if (restart)
                    Log("PASS real-process-restart baseline protected preserved-events=" + store.State.Events.Length + " new-submit=0");
                else
                {
                    var events = store.State.Events;
                    if (driver.Receipts != 2 || core.OwnIgnored != 2 || events.Length != 2 ||
                        events.Any(x => x.Phase != PrivatePhase.LocalConfirmed) || (!realModel && events[0].Digest != events[1].Digest) ||
                        events[0].RuntimeKey == events[1].RuntimeKey || events[0].LocalId == events[1].LocalId)
                        throw new InvalidOperationException("没有满足双端确认、本人过滤与同文去重");
                    if (realModel)
                    {
                        bool context = store.State.ModelTurns.Length == 2 && events[1].Reply.Trim(' ', '。', '.', '「', '」') == "梅雨36";
                        Log("real-model context-expected=" + context + " context-turns=" + store.State.ModelTurns.Length);
                    }
                    else if (model != null && (store.State.ModelTurns.Length != 2 || events.Any(item => item.RecoveryTag != null) ||
                        events[0].Reply != "模拟首轮确认 " + s[6] ||
                        events[1].Reply != "模拟第二轮，已带入上一轮 " + s[6]))
                        throw new InvalidOperationException("模型回执或已确认上下文不符");
                    Log("PASS incoming=2 confirmed=2 peer=2 own-filtered=2" +
                        (realModel ? " model=real" : model == null ? " model=fixed" : " model=mock-http context-turns=2"));
                }
            }
            finally { core.StopUnsubmitted(); }
        }
    }

    private sealed class ReadOnlyTransport : IPrivateTransport
    {
        private readonly IPrivateTransport inner;
        internal ReadOnlyTransport(IPrivateTransport inner) { this.inner = inner; }
        public string Epoch { get { return inner.Epoch; } }
        public ReplyRow[] Read() { return inner.Read(); }
        public void RequireReady() { inner.RequireReady(); }
        public void Submit(string text, Action verifyBefore, Action<Action> commit)
        {
            throw new InvalidOperationException("重启只读验证发现意外发送尝试");
        }
    }
}
