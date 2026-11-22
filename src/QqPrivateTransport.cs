using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

internal sealed class QqPrivateTransport : IPrivateTransport, IDisposable
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
        if (Epoch != proof.Epoch) throw new InvalidOperationException("QQ 窗口或进程代际已变化，请重新按号核验");
        binding = ReadStable(() => new Binding(target, false));
        ReadStable(Scan);
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
            throw new InvalidOperationException("QQ 窗口隐藏、真正最小化或不可用");
        var ui = binding.RequireMatch();
        if (binding.HeaderProof != proof.HeaderProof || ui.HeaderIdentity() != proof.PeerDisplay ||
            ui.LoginEvidence(proof.BotDisplay) != proof.LoginProof)
            throw new InvalidOperationException("登录界面或私聊绑定已变化，需重新核验");
        if (ui.HasVisibleConnectionWarning()) throw new InvalidOperationException("QQ 界面显示断线或重新连接");
        if (!ui.HasMessageArea || ui.Editor == null || !ui.Editor.Current.IsEnabled || ui.Button == null)
            throw new InvalidOperationException("QQ 消息或编辑控件已失效");
        guard();
        return ui;
    }

    private T ReadStable<T>(Func<T> read)
    {
        return ReadStable(read, guard, log);
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
        }
    }

    public ReplyRow[] Read() { return ReadStable(() => Scan().ReplyRows()); }

    public void RequireReady()
    {
        if (!ReadStable(() => Scan().EmptyDraft())) throw new InvalidOperationException("QQ 输入框有草稿或无法确认空白");
    }

    public void Submit(string text, Action verifyBefore, Action<Action> commit)
    {
        verifyBefore();
        var mapped = ReadStable(() => {
            var current = Scan();
            if (!current.EmptyDraft()) throw new InvalidOperationException("QQ 输入框存在草稿");
            return MsaaProbe.FindMappedEditor(target, current, log);
        });
        if (mapped == null) throw new InvalidOperationException("MSAA 编辑对象未可靠匹配");
        verifyBefore();
        RequireReady();
        guard();
        log("write-attempt method=MSAA.put_accValue");
        var result = MsaaProbe.PutValue(mapped, text);
        log("write-return hr=0x" + result.ToString("X8"));
        if (result != 0) throw new InvalidOperationException("MSAA 写入失败，请核对草稿");
        var expected = text.Replace("\r\n", "\n");
        for (int attempt = 0; ; attempt++)
        {
            Thread.Sleep(220);
            verifyBefore();
            var draft = ReadStable(() => {
                var current = Scan();
                return Tuple.Create(current.Draft(), current.EmptyDraft());
            });
            if (draft.Item1 == expected) break;
            if (attempt >= 3 || !draft.Item2) throw new InvalidOperationException("写入后草稿回读不一致");
        }
        log("write-confirmed exact=yes");
        verifyBefore();
        var action = ReadStable(() => {
            var current = Scan();
            var button = current.Button;
            var legacy = current.Legacy(button);
            if (current.Draft() != expected || button == null || !button.Current.IsEnabled ||
                legacy == null || legacy.CurrentRole() != 0x2b || (legacy.CurrentState() & 1) != 0 ||
                String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()))
                throw new InvalidOperationException("草稿或发送按钮默认动作变化，已停发");
            return legacy;
        });
        verifyBefore();
        if (ReadStable(() => Scan().Draft()) != expected)
            throw new InvalidOperationException("提交前草稿内容变化");
        guard();
        commit(() => {
            guard();
            log("send-action method=LegacyIAccessible.DoDefaultAction");
            var hr = action.DoDefaultAction();
            log("send-return hr=0x" + hr.ToString("X8"));
        });
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
        return new PrivateBindingProof { Window = target.Hwnd.ToInt64(), Epoch = EpochOf(target),
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
