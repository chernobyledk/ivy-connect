using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ExternalPrivateScope
{
    internal readonly string Code, Scenario, Local, Peer, Display;
    internal readonly long Window;
    internal readonly long PeerWindow;
    internal readonly string PeerChatDisplay;
    internal readonly bool ObserveOnly;
    internal readonly int Limit;
    private string mappingFile;
    private Action<string> log;

    internal ExternalPrivateScope(string file) : this(File.ReadAllLines(file, Encoding.UTF8)) { }

    internal ExternalPrivateScope(string[] lines)
    {
        if (lines.Length != 7 && lines.Length != 9) throw new InvalidOperationException("测试配置应有七行或九行");
        Window = Int64.Parse(lines[0]);
        Code = lines[1];
        Scenario = lines[2];
        Local = lines[3];
        Peer = lines[4];
        Display = lines[5];
        if (lines[6] != "operator-verified-private-by-qq-number" && lines[6] != "read-only")
            throw new InvalidOperationException("缺少按 QQ 号核验私聊的记录");
        ObserveOnly = lines[6] == "read-only";
        if (!Regex.IsMatch(Code, "^[A-F0-9]{12}$") || (Scenario != "V" && Scenario != "F") ||
            !Regex.IsMatch(Local, "^[0-9]{5,12}$") || !Regex.IsMatch(Peer, "^[0-9]{5,12}$") ||
            Local == Peer || String.IsNullOrWhiteSpace(Display))
            throw new InvalidOperationException("请核对两个不同的 QQ 号和测试场景");
        Limit = Scenario == "V" ? 1 : 2;
        if (lines.Length == 9)
        {
            PeerWindow = Int64.Parse(lines[7]);
            PeerChatDisplay = lines[8];
            if (ObserveOnly || PeerWindow == Window || PeerWindow <= 0 || String.IsNullOrWhiteSpace(PeerChatDisplay))
                throw new InvalidOperationException("第二个测试客户端绑定无效");
        }
    }

    internal string Marker { get { return "私聊验证 " + Code + " " + Scenario + " 中文"; } }
    internal string Reply(int item) { return ContinuousReply.ReplyPrefix + " " + Code + " " + Scenario + "-" + item.ToString("D2") + " 已收到"; }
    internal bool IsInput(ReplyRow row) { return row.Text == MessageSnapshot.Normalize(Marker); }
    internal bool IsReply(ReplyRow row)
    {
        return Enumerable.Range(1, Limit).Any(n => row.Text == MessageSnapshot.Normalize(Reply(n)));
    }
    internal void Attach(string basePath, Action<string> writeLog)
    {
        mappingFile = basePath + ".identity.dpapi";
        log = writeLog;
    }
    internal void CheckHeader(Controls ui)
    {
        if (!String.Equals(ui.HeaderIdentity(), Display, StringComparison.Ordinal))
            throw new InvalidOperationException("QQ 会话头与已核验窗口的显示名不一致");
    }
    internal void VerifySource(Controls ui, Binding binding, ReplyRow row, int item, bool beforeWrite)
    {
        CheckHeader(ui);
        if (!ui.ReplyRows().Any(candidate => candidate.Anchor == row.Anchor && candidate.Own == false && IsInput(candidate)))
            throw new InvalidOperationException("原会话中的来信气泡无法复核");
        log("identity-check item=" + item + " phase=" + (beforeWrite ? "before-write" : "before-send") +
            " challenge-and-anchor=matched header-binding=" + binding.HeaderProof + " " +
            ui.ExternalFieldEvidence(row.Key, Local, Peer));
        if (!beforeWrite || item != 1) return;
        if (ObserveOnly)
        {
            log("read-only gate closed; no draft or action");
            throw new InvalidOperationException("只读验证结束，未授权写入");
        }
        var plaintext = Encoding.UTF8.GetBytes(String.Join("\n", new[] {
            "QQWindowLab external-private identity record v1", DateTimeOffset.UtcNow.ToString("O"),
            "proof=operator-verified-private-by-qq-number+run-specific-incoming-challenge",
            "local=" + Local, "peer=" + Peer, "header-label=" + Display, "header-runtime-proof=" + binding.HeaderProof,
            "target-process-start=" + binding.Target.Start.ToUniversalTime().ToString("O"), "window=" + Window,
            "run=" + Code, "anchor=" + MessageSnapshot.Hash(row.Anchor), "auto-reuse=false"
        }));
        var record = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        Array.Clear(plaintext, 0, plaintext.Length);
        using (var file = new FileStream(mappingFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            file.Write(record, 0, record.Length);
            file.Flush(true);
        }
        var verified = ProtectedData.Unprotect(File.ReadAllBytes(mappingFile), null, DataProtectionScope.CurrentUser);
        try
        {
            var saved = Encoding.UTF8.GetString(verified).Split('\n');
            if (!saved.Contains("local=" + Local) || !saved.Contains("peer=" + Peer) ||
                !saved.Contains("header-runtime-proof=" + binding.HeaderProof))
                throw new InvalidOperationException("无法回读已核验账号映射");
        }
        finally { Array.Clear(verified, 0, verified.Length); }
        log("mapping stored and read back dpapi=current-user provenance=operator+challenge autoReuse=no");
    }
}

internal sealed class ExternalPrivateForm : Form
{
    private readonly string settings;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Label status = new Label { Dock = DockStyle.Fill, Text = "正在建立测试条件", Padding = new Padding(8) };
    private readonly object logGate = new object();
    private string logPath;
    private bool running;

    internal ExternalPrivateForm(string settings)
    {
        this.settings = settings;
        Text = "QQ 单一私聊验证";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(320, 100);
        var button = new Button { Text = "停止并撤除遮挡", Dock = DockStyle.Bottom, Height = 30 };
        button.Click += (s, e) => stop.Cancel();
        Controls.Add(status);
        Controls.Add(button);
        Shown += async (s, e) => await Run();
        FormClosing += (s, e) => {
            if (!running) return;
            e.Cancel = true;
            stop.Cancel();
        };
        FormClosed += (s, e) => stop.Dispose();
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    private void Log(string message)
    {
        lock (logGate)
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            var data = Encoding.UTF8.GetBytes(DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
            stream.Write(data, 0, data.Length);
            stream.Flush(message.StartsWith("send-intent", StringComparison.Ordinal));
        }
    }

    private void ShowState(string message)
    {
        if (IsHandleCreated)
            BeginInvoke(new Action(() => status.Text = message));
    }

    private bool VisibleSample(IntPtr hwnd, Rectangle area)
    {
        return new[] {
            new Point(area.Left + area.Width / 2, area.Top + 32),
            new Point(area.Left + 3 * area.Width / 4, area.Top + area.Height / 2),
            new Point(area.Left + 3 * area.Width / 4, area.Bottom - 48)
        }.All(p => Native.RootAt(p.X, p.Y) == hwnd);
    }

    private async Task Run()
    {
        running = true;
        Target target = null;
        var original = Rectangle.Empty;
        using (var cover = new Cover())
        try
        {
            var scope = new ExternalPrivateScope(settings);
            var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs");
            Directory.CreateDirectory(folder);
            var basePath = Path.Combine(folder, "external-" + scope.Code + "-" + scope.Scenario);
            var path = basePath + ".log";
            using (File.Open(path, FileMode.CreateNew)) { }
            logPath = path;
            scope.Attach(basePath, Log);
            target = Target.FromKnownHandle(scope.Window);
            original = Native.Bounds(target.Hwnd);
            var binding = await Task.Run(() => {
                if (Native.IsIconic(target.Hwnd)) throw new InvalidOperationException("QQ 必须保持展开");
                var bound = new Binding(target, false);
                var ui = bound.RequireMatch();
                scope.CheckHeader(ui);
                if (ui.HasVisibleConnectionWarning() || !ui.EmptyDraft())
                    throw new InvalidOperationException("连接不可用或输入框有草稿");
                return bound;
            }, stop.Token);
            var driver = scope.PeerWindow == 0 ? null : await Task.Run(() =>
                new ExternalPeerDriver(scope, target, Log, stop.Token), stop.Token);
            var work = Screen.FromHandle(target.Hwnd).WorkingArea;
            const int sideWidth = 292;
            var left = original.Left - work.Left;
            var right = work.Right - original.Right;
            if (left < sideWidth && right < sideWidth)
                throw new InvalidOperationException("QQ 两侧没有专用便笺空间，请先移动 QQ 窗口");
            int x = left >= sideWidth ? original.Left - sideWidth : original.Right;
            Bounds = new Rectangle(x, work.Top + 220, sideWidth, Height);
            if (scope.Scenario == "F") cover.CoverRect(original);
            var coverHandle = scope.Scenario == "F" ? cover.Handle : IntPtr.Zero;

            using (var pad = new TypingPad(new Rectangle(x, work.Top + 25, sideWidth, 180), automaticMarker: scope.Code))
            {
                for (int n = 0; n < 60 && pad.Hwnd == IntPtr.Zero; n++) await Task.Delay(100, stop.Token);
                var padHandle = pad.Hwnd;
                if (padHandle == IntPtr.Zero) throw new InvalidOperationException("输入对照窗口未就绪");
                Native.SetForegroundWindow(padHandle);
                for (int n = 0; n < 60 && Native.GetForegroundWindow() != padHandle; n++) await Task.Delay(100, stop.Token);
                Func<IntPtr> foreground = () => {
                    if (Native.IsIconic(target.Hwnd) || Native.Bounds(target.Hwnd) != original ||
                        Native.GetForegroundWindow() != padHandle || !Native.HasChildFocus(padHandle)) return IntPtr.Zero;
                    if (coverHandle == IntPtr.Zero)
                        return VisibleSample(target.Hwnd, original) ? padHandle : IntPtr.Zero;
                    return Native.Visible(coverHandle) && Native.Above(coverHandle, target.Hwnd) &&
                        Rectangle.Intersect(Native.Bounds(coverHandle), original) == original ? padHandle : IntPtr.Zero;
                };
                await Task.Delay(500, stop.Token);
                if (foreground() == IntPtr.Zero) throw new InvalidOperationException("未建立指定遮挡条件或专用便笺的输入焦点");
                var steps = pad.AutomaticSteps;
                var focusLoss = pad.AutomaticFocusLoss;
                var mismatch = pad.AutomaticMismatch;
                Log("scenario=" + scope.Scenario + " target=" + target.Hwnd.ToInt64() + " pid=" + target.Pid +
                    " geometry=" + original + " dpi=" + Native.GetDpiForWindow(target.Hwnd) + " minimized=no" +
                    " foreground-pad=" + padHandle.ToInt64() + " setup-activation=pad-only" +
                    " requires-exact-inbound=" + scope.Marker + " expected=" + scope.Limit);
                using (var trace = new ForegroundTrace())
                using (var effects = new SideEffects())
                {
                    try
                    {
                        stop.CancelAfter(TimeSpan.FromMinutes(7));
                        var runner = new ContinuousReply(binding, scope.Code, scope.Limit, () => false, foreground,
                            trace, Log, ShowState, stop.Token, scope, driver);
                        await Task.Run(() => runner.Run(), stop.Token);
                        Log("result complete peer-receipt=separate");
                        status.Text = "本地验证完成，接收端证据单列";
                    }
                    finally
                    {
                        Log("foreground-window-events count=" + trace.Count + " " + trace.Summary());
                        Log("input-observation " + effects.Describe(false) + " automatic-pad-steps=" +
                            (pad.AutomaticSteps - steps) + " pad-focus-loss=" + (pad.AutomaticFocusLoss - focusLoss) +
                            " pad-text-mismatch=" + (pad.AutomaticMismatch - mismatch) + " human-typing=not-tested");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            status.Text = "测试窗口已停止或到期";
            if (logPath != null) Log("result stopped timeout-or-cancel");
        }
        catch (Exception ex)
        {
            status.Text = "已停止 " + Experiment.Failure(ex);
            if (logPath != null) Log("result halted " + Experiment.Failure(ex));
        }
        finally
        {
            cover.Hide();
            if (logPath != null)
                Log("restored cover=closed pad=closed QQ-geometry-unchanged=" +
                    (target != null && target.SameWindow() && Native.Bounds(target.Hwnd) == original));
            running = false;
            Close();
        }
    }
}
