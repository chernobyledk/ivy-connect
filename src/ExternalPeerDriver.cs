using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

// Second logged-in QQ client. Only the test marker can ever be submitted from this endpoint.
internal sealed class ExternalPeerDriver
{
    private readonly ExternalPrivateScope scope;
    private readonly Binding endpoint;
    private readonly Action<string> log;
    private readonly CancellationToken cancel;
    private readonly Func<int, string> replyForItem;
    private readonly Stopwatch receiptWait = Stopwatch.StartNew();
    private int probes, receipts;
    private string[] sentKeys = new string[0];
    internal int Receipts { get { return receipts; } }

    internal ExternalPeerDriver(ExternalPrivateScope scope, Target receiver, Action<string> log, CancellationToken cancel,
        Func<int, string> replyForItem = null, string expectedHeaderProof = null)
    {
        this.scope = scope;
        this.log = log;
        this.cancel = cancel;
        this.replyForItem = replyForItem ?? scope.Reply;
        var target = Target.FromKnownHandle(scope.PeerWindow);
        if (target.Pid == receiver.Pid) throw new InvalidOperationException("两个 QQ 客户端须来自独立 QQ 进程");
        endpoint = Observe(() => new Binding(target, false));
        if (expectedHeaderProof != null && endpoint.HeaderProof != expectedHeaderProof)
            throw new InvalidOperationException("测试发件端的会话证据已变化");
        var rows = Observe(() => {
            var ui = Scan();
            if (!ui.EmptyDraft()) throw new InvalidOperationException("发件端输入框非空");
            return ui.ReplyRows();
        });
        if (rows.Any(row => scope.IsInput(row) || scope.IsReply(row)))
            throw new InvalidOperationException("发件端已存在本轮的测试编号");
        log("peer-window bound process-distinct=yes hwnd=" + endpoint.Target.Hwnd.ToInt64() +
            " pid=" + endpoint.Target.Pid + " header-binding=" + endpoint.HeaderProof + " baseline-test-markers=0");
    }

    private Controls Scan()
    {
        var target = endpoint.Target;
        if (!target.SameWindow() || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd) ||
            !Native.IsWindowEnabled(target.Hwnd))
            throw new InvalidOperationException("发件端窗口失效、隐藏或最小化");
        var ui = endpoint.RequireMatch();
        if (ui.HeaderIdentity() != scope.PeerChatDisplay || ui.HasVisibleConnectionWarning())
            throw new InvalidOperationException("发件端私聊身份或连接状态已变化");
        return ui;
    }

    private T Observe<T>(Func<T> read)
    {
        for (int n = 0; ; n++)
        {
            cancel.ThrowIfCancellationRequested();
            try { return read(); }
            catch (ElementNotAvailableException)
            {
                if (n == 2) throw;
                log("peer transient UIA node: retry read only");
                if (cancel.WaitHandle.WaitOne(260)) cancel.ThrowIfCancellationRequested();
            }
        }
    }

    internal void Poll(int localConfirmed, Action guard)
    {
        var rows = Observe(() => Scan().ReplyRows());
        while (receipts < localConfirmed)
        {
            var marker = MessageSnapshot.Normalize(replyForItem(receipts + 1));
            var received = rows.Where(row => row.Text == marker).ToArray();
            if (received.Length == 0)
            {
                if (receiptWait.Elapsed.TotalSeconds >= 22)
                    throw new InvalidOperationException("对端未在时限内确认收到回执，未重发");
                return;
            }
            if (received.Length != 1 || received[0].Own != false)
                throw new InvalidOperationException("对端回执重复或气泡方向不明");
            receipts++;
            log("peer-receipt-confirmed item=" + receipts + " direction=incoming exactRows=1 bubble=" +
                MessageSnapshot.Hash(received[0].Key) + " endpoint=distinct-logged-in-desktop-client");
        }
        if (probes != receipts || probes >= scope.Limit) return;
        using (var lease = new Mutex(false, @"Local\QQWindowLab.Continuous." + endpoint.Target.Hwnd.ToInt64()))
        {
            bool owned;
            try { owned = lease.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("发件端窗口已有其他验证");
            try { Submit(guard); }
            finally { lease.ReleaseMutex(); }
        }
    }

    private static bool NewOccurrence(string[] before, string[] after)
    {
        return after.Length == before.Length + 1 && before.All(after.Contains) && after.Distinct().Count() == after.Length;
    }

    private void Submit(Action guard)
    {
        guard();
        var mapped = Observe(() => {
            var current = Scan();
            if (!current.EmptyDraft()) throw new InvalidOperationException("发件端已有草稿，已停发");
            return MsaaProbe.FindMappedEditor(endpoint.Target, current, log);
        });
        if (mapped == null) throw new InvalidOperationException("第二客户端 MSAA 编辑器未匹配");
        int n = probes + 1;
        log("peer-input-write-attempt item=" + n);
        var result = MsaaProbe.PutValue(mapped, scope.Marker);
        if (result != 0) throw new InvalidOperationException("第二客户端 MSAA 写入失败，需核对测试草稿");
        for (int attempt = 0; ; attempt++)
        {
            if (cancel.WaitHandle.WaitOne(200)) cancel.ThrowIfCancellationRequested();
            var draft = Observe(() => {
                var current = Scan();
                return Tuple.Create(current.Draft(), current.EmptyDraft());
            });
            if (draft.Item1 == scope.Marker) break;
            if (!draft.Item2 || attempt >= 4) throw new InvalidOperationException("第二客户端草稿回读不一致");
        }
        log("peer-input-write-confirmed item=" + n);
        var action = Observe(() => {
            var current = Scan();
            var button = current.Button;
            var legacy = current.Legacy(button);
            if (button == null || !button.Current.IsEnabled || legacy == null || legacy.CurrentRole() != 0x2B ||
                (legacy.CurrentState() & 1) != 0 || String.IsNullOrWhiteSpace(legacy.CurrentDefaultAction()))
                throw new InvalidOperationException("第二客户端发送按钮默认动作不可用");
            return legacy;
        });
        guard();
        var safe = Observe(() => {
            var fresh = Scan();
            return fresh.Draft() == scope.Marker &&
                sentKeys.SequenceEqual(fresh.ReplyRows().Where(scope.IsInput).Select(row => row.Key));
        });
        if (!safe)
            throw new InvalidOperationException("第二客户端发送前草稿或消息列表变化");
        guard();
        probes++;
        log("send-intent peer-input item=" + n + " method=MSAA.DoDefaultAction");
        result = action.DoDefaultAction();
        log("peer-input-send-return item=" + n + " hr=0x" + result.ToString("X8"));
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 14)
        {
            if (cancel.WaitHandle.WaitOne(240)) cancel.ThrowIfCancellationRequested();
            var observed = Observe(() => { var current = Scan(); return Tuple.Create(current.ReplyRows(), current.EmptyDraft()); });
            var rows = observed.Item1.Where(scope.IsInput).ToArray();
            var keys = rows.Select(row => row.Key).ToArray();
            if (!NewOccurrence(sentKeys, keys) || rows.Any(row => row.Own != true) || !observed.Item2) continue;
            log("peer-input-local-bubble item=" + n + " exactRows=" + keys.Length + " direction=outgoing bubble=" +
                MessageSnapshot.Hash(keys.Single(key => !sentKeys.Contains(key))));
            sentKeys = keys;
            receiptWait.Restart();
            return;
        }
        throw new InvalidOperationException("测试来信已提交但第二客户端本地未确认，未重发");
    }
}
