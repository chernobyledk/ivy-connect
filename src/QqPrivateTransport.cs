using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

internal sealed class QqPrivateTransport : IPrivateTransport, ICheckedPrivateTransport, IDisposable
{
    private readonly Target target;
    private readonly Binding binding;
    private readonly PrivateBindingProof proof;
    private readonly Action guard;
    private readonly Action<string> log;
    private readonly Mutex windowLease;

    internal static string EpochOf(Target target)
    {
        return target.Pid + "/" + target.Start.ToUniversalTime().ToString("O") + "/" + target.Hwnd.ToInt64();
    }
    public string Epoch { get { return EpochOf(target); } }

    internal QqPrivateTransport(PrivateBindingProof proof, Action guard, Action<string> log)
    {
        this.proof = proof;
        this.guard = guard;
        this.log = log;
        CheckNumbers(proof.BotQq, proof.PeerQq);
        target = Target.FromKnownHandle(proof.Window);
        if (Epoch != proof.Epoch) throw new PrivateRouteLostException("QQ 窗口或进程代际已变化，请重新按号核验");
        binding = ReadStable(() => new Binding(target, false, true));
        Read();
        windowLease = new Mutex(false, @"Local\QQWindowLab.Continuous." + target.Hwnd.ToInt64());
        bool owns;
        try { owns = windowLease.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }
        if (!owns) { windowLease.Dispose(); throw new InvalidOperationException("该 QQ 窗口已有另一个收发进程"); }
    }

    private Controls Scan()
    {
        guard();
        if (!target.SameWindow() || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd) ||
            !Native.IsWindowEnabled(target.Hwnd))
            throw new PrivateRouteLostException("QQ 窗口已关闭、隐藏、最小化或被重建，请恢复展开后重新核验");
        var ui = Controls.Scan(target, true);
        RequireLiveIdentity(ui);
        if (ui.HasVisibleConnectionWarning()) throw new InvalidOperationException("QQ 界面显示断线或重新连接");
        if (!ui.HasMessageArea || ui.Editor == null || !ui.Editor.Current.IsEnabled || ui.Button == null)
            throw new PrivateRouteLostException("QQ 消息或编辑控件已失效，请重新核验");
        guard();
        return ui;
    }

    private void RequireLiveIdentity(Controls ui)
    {
        if (!binding.Matches(ui) || binding.HeaderProof != proof.HeaderProof || ui.HeaderIdentity() != proof.PeerDisplay ||
            String.IsNullOrEmpty(proof.LoginProof) || ui.LoginEvidence(proof.BotDisplay) != proof.LoginProof)
            throw new PrivateRouteLostException("登录账号或私聊界面证据变化，旧任务已停发");
    }

    private ReplyRow[] BoundRows(Controls ui)
    {
        var rows = ui.ReplyRows();
        if (String.IsNullOrEmpty(proof.ChallengeAnchor) ||
            rows.Count(row => row.Own == false && MessageSnapshot.Hash(row.Anchor) == proof.ChallengeAnchor) != 1)
            throw new PrivateRouteLostException("当前消息列表未找到唯一的原会话入站核验锚点，请重新核验");
        return rows;
    }

    private T ReadStable<T>(Func<T> read)
    {
        try { return ReadStable(read, guard, log); }
        catch (ElementNotAvailableException) { throw new PrivateRouteLostException("QQ 界面节点重建，有限重读后仍无法核验原会话"); }
        catch (COMException ex) { throw new PrivateRouteLostException("QQ 无障碍读取不稳定，请重新核验，错误码 0x" + ex.HResult.ToString("X8")); }
    }

    private static T ReadStable<T>(Func<T> read, Action guard, Action<string> log)
    {
        for (int attempt = 0; ; attempt++)
        {
            guard();
            try { return read(); }
            catch (ElementNotAvailableException)
            {
                if (attempt == 2) throw;
                log("UIA transient node: retry read only");
                Thread.Sleep(250);
            }
            catch (COMException ex)
            {
                if (attempt == 2) throw;
                log("UIA COM read retry only hr=0x" + ex.HResult.ToString("X8"));
                Thread.Sleep(250);
            }
        }
    }

    public ReplyRow[] Read() { return ReadStable(() => BoundRows(Scan())); }

    public ReplyRow[] ReadAfterSubmit(out bool draftEmpty)
    {
        var observed = ReadStable(() => {
            var scene = Scan();
            return Tuple.Create(BoundRows(scene), scene.EmptyDraft());
        });
        draftEmpty = observed.Item2;
        return observed.Item1;
    }

    public void RequireReady()
    {
        if (!ReadStable(() => Scan().EmptyDraft())) throw new InvalidOperationException("QQ 输入框有草稿或无法确认空白");
    }

    void IPrivateTransport.Submit(string text, Action verifyBefore, Action<Action> commit)
    {
        throw new InvalidOperationException("QQ 发送必须走完整的写入与提交核验");
    }

    public void SubmitChecked(string text, Action<ReplyRow[]> verify, Action<Action, Action> commit, Action<string> checkpoint)
    {
        var prepared = ReadStable(() => {
            var scene = Scan();
            var rows = BoundRows(scene);
            verify(rows);
            RequireBackground();
            if (!scene.EmptyDraft()) throw new InvalidOperationException("QQ 输入框有人工草稿，已暂停");
            var nativeEditor = MsaaProbe.FindMappedEditor(target, scene, log);
            if (nativeEditor == null) throw new InvalidOperationException("MSAA 编辑对象未可靠匹配");
            // These are live reads from the scene whose native editor was just mapped.
            RequireLiveIdentity(scene);
            if (!scene.EmptyDraft()) throw new InvalidOperationException("写入前输入框出现草稿，已暂停");
            return Tuple.Create(nativeEditor, rows);
        });
        checkpoint("identity-before-write");
        RequireBackground();
        verify(prepared.Item2);
        checkpoint("write-start");
        if (MsaaProbe.PutValue(prepared.Item1, text) != 0) throw new InvalidOperationException("MSAA 写入失败，请核对草稿");
        var expected = text.Replace("\r\n", "\n");
        for (int attempt = 0; ; attempt++)
        {
            guard();
            var draft = ReadStable(() => Scan().Draft());
            if (draft == expected) break;
            if (attempt >= 2) throw new InvalidOperationException("写入后草稿回读不一致，草稿已保留");
            // Renderer can replace the editor after the write. Reacquire it without writing again.
            log("write-verification waiting for a fresh UIA read");
            Thread.Sleep(120);
        }
        checkpoint("write-confirm");
        LegacyEditor.ILegacy action = null;
        commit(() => {
            // Acquire the action only after durable intent and the final full, live route check.
            action = ReadStable(() => {
                var current = Scan();
                verify(BoundRows(current));
                RequireBackground();
                var button = current.Button;
                var legacy = current.Legacy(button);
                if (current.Draft() != expected || button == null || !button.Current.IsEnabled ||
                    legacy == null || legacy.CurrentRole() != 0x2b || (legacy.CurrentState() & 1) != 0 ||
                    String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()))
                    throw new InvalidOperationException("提交前草稿或发送按钮已变化，未发送");
                RequireLiveIdentity(current);
                return legacy;
            });
            checkpoint("identity-before-submit");
            RequireBackground();
        }, () => {
            guard();
            var hr = action.DoDefaultAction();
            log("send-return hr=0x" + hr.ToString("X8"));
        });
    }

    private void RequireBackground()
    {
        guard();
        if (!target.SameWindow() || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
            throw new PrivateRouteLostException("窗口已变化或未展开");
        if (Native.Pid(Native.GetForegroundWindow()) == target.Pid)
            throw new InvalidOperationException("机器人 QQ 在前台，等待人工操作结束");
    }

    internal void ClearExactTestDraft(PrivateEvent item)
    {
        if (item.Phase != PrivatePhase.Failed || item.Detail != "blocked-before-submit" ||
            item.BotQq != proof.BotQq || item.PeerQq != proof.PeerQq ||
            !item.Reply.StartsWith("核心验证 ", StringComparison.Ordinal))
            throw new InvalidOperationException("非本轮已确认未提交的测试草稿");
        var ui = ReadStable(Scan);
        if (ui.EmptyDraft()) { log("cleanup editor already empty"); return; }
        if (ui.Draft() != item.Reply ||
            !ui.ReplyRows().Any(row => row.Own == false && row.Key == item.RuntimeKey && row.Digest == item.Digest))
            throw new InvalidOperationException("草稿不等于本测试写入的完整文本，已保留");
        var mapped = MsaaProbe.FindMappedEditor(target, ui, log);
        if (mapped == null || ReadStable(() => Scan().Draft()) != item.Reply)
            throw new InvalidOperationException("清理前无法复核测试草稿");
        if (MsaaProbe.PutValue(mapped, "") != 0) throw new InvalidOperationException("测试草稿清理失败");
        for (int i = 0; i < 5; i++)
        {
            Thread.Sleep(250);
            if (ReadStable(() => Scan().EmptyDraft())) { log("cleanup exact unsubmitted test draft removed"); return; }
        }
        throw new InvalidOperationException("未确认草稿已清理");
    }

    public void Dispose() { windowLease.ReleaseMutex(); windowLease.Dispose(); }

    internal static void CheckNumbers(string bot, string peer)
    {
        if (!Regex.IsMatch(bot ?? "", "^[0-9]{5,12}$") || !Regex.IsMatch(peer ?? "", "^[0-9]{5,12}$") || bot == peer)
            throw new InvalidOperationException("请输入两个不同的 QQ 号");
    }

    internal static PrivateBindingProof Enroll(long hwnd, string bot, string peer, string botDisplay, string peerDisplay,
        string challenge, CancellationToken token, Action<string> log)
    {
        CheckNumbers(bot, peer);
        var target = Target.FromKnownHandle(hwnd);
        if (!Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
            throw new InvalidOperationException("请打开机器人的展开私聊窗口");
        var binding = ReadStable(() => new Binding(target, false), token.ThrowIfCancellationRequested, log);
        var initial = ReadStable(() => {
            var ui = binding.RequireMatch();
            var login = ui.LoginEvidence(botDisplay);
            if (String.IsNullOrEmpty(login) || ui.HeaderIdentity() != peerDisplay ||
                ui.HasVisibleConnectionWarning() || !ui.EmptyDraft())
                throw new InvalidOperationException("登录头像、聊天名称、网络或空草稿核验未通过");
            return Tuple.Create(login, ui.ReplyRows());
        }, token.ThrowIfCancellationRequested, log);
        var timeline = new ReplyTimeline();
        var before = initial.Item2;
        if (before.Any(row => row.Text == MessageSnapshot.Normalize(challenge)))
            throw new InvalidOperationException("验证编号已出现在历史中");
        if (token.WaitHandle.WaitOne(350)) token.ThrowIfCancellationRequested();
        timeline.Seed(before, ReadStable(() => binding.RequireMatch().ReplyRows(), token.ThrowIfCancellationRequested, log));
        log("enrollment baseline established");
        while (true)
        {
            if (token.WaitHandle.WaitOne(340)) token.ThrowIfCancellationRequested();
            if (!Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
                throw new InvalidOperationException("核验期间 QQ 窗口状态变化");
            var snapshot = ReadStable(() => {
                var ui = binding.RequireMatch();
                if (ui.LoginEvidence(botDisplay) != initial.Item1 || ui.HeaderIdentity() != peerDisplay ||
                    ui.HasVisibleConnectionWarning() || !ui.EmptyDraft())
                    throw new InvalidOperationException("核验期间账号、会话、草稿或连接状态变化");
                return ui.ReplyRows();
            }, token.ThrowIfCancellationRequested, log);
            var hits = timeline.Advance(snapshot).Where(row => row.Text == MessageSnapshot.Normalize(challenge)).ToArray();
            if (hits.Length == 0) continue;
            if (hits.Length != 1 || hits[0].Own != false) throw new InvalidOperationException("来信核验编号不唯一或方向错误");
            return Capture(target, binding, bot, peer, botDisplay, peerDisplay, initial.Item1, MessageSnapshot.Hash(hits[0].Anchor));
        }
    }

    private static PrivateBindingProof Capture(Target target, Binding binding, string bot, string peer,
        string botDisplay, string peerDisplay, string login, string anchor)
    {
        return new PrivateBindingProof { Window = target.Hwnd.ToInt64(), Epoch = EpochOf(target), Version = Guid.NewGuid().ToString("N"),
            BotQq = bot, PeerQq = peer, BotDisplay = botDisplay, PeerDisplay = peerDisplay,
            HeaderProof = binding.HeaderProof, LoginProof = login, ChallengeAnchor = anchor,
            VerifiedUtc = PrivateStore.Stamp() };
    }

    internal static PrivateBindingProof ImportVerifiedRun(string file, string botDisplay)
    {
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try
        {
            var lines = Encoding.UTF8.GetString(clear).Split('\n');
            if (lines[0] != "QQWindowLab external-private identity record v1" ||
                !lines.Contains("proof=operator-verified-private-by-qq-number+run-specific-incoming-challenge"))
                throw new InvalidDataException("无上一轮经核验的双账号记录");
            var fields = lines.Skip(2).Select(line => line.Split(new[] { '=' }, 2)).ToDictionary(x => x[0], x => x[1]);
            CheckNumbers(fields["local"], fields["peer"]);
            var target = Target.FromKnownHandle(Int64.Parse(fields["window"]));
            if (target.Start.ToUniversalTime().ToString("O") != fields["target-process-start"])
                throw new InvalidOperationException("上一轮 QQ 进程已换代，需重新核验");
            var binding = new Binding(target, false);
            var ui = binding.RequireMatch();
            var avatar = ui.LoginEvidence(botDisplay);
            if (binding.HeaderProof != fields["header-runtime-proof"] || ui.HeaderIdentity() != fields["header-label"] ||
                String.IsNullOrEmpty(avatar) || !ui.ReplyRows().Any(row => row.Own == false &&
                    MessageSnapshot.Hash(row.Anchor) == fields["anchor"]))
                throw new InvalidOperationException("上一轮按号核验锚点或登录头像已不可复核");
            return Capture(target, binding, fields["local"], fields["peer"], botDisplay, fields["header-label"],
                avatar, fields["anchor"]);
        }
        finally { Array.Clear(clear, 0, clear.Length); }
    }
}
