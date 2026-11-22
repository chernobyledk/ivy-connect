using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal interface IPrivateTransport
{
    string Epoch { get; }
    void RequireReady();
    ReplyRow[] Read();
    void Submit(string text, Action verifyBefore, Action<Action> commit);
}

internal sealed class ReplyDraft
{
    internal readonly string Text, RecoveryTag;
    internal readonly int ModelContextLimit;
    internal ReplyDraft(string text, string recoveryTag, int modelContextLimit = 0)
    {
        Text = text;
        RecoveryTag = recoveryTag;
        ModelContextLimit = modelContextLimit;
    }
}

internal interface IPrivateReplyProvider
{
    Task<ReplyDraft> GenerateAsync(PrivateEvent incoming, CancellationToken cancellation);
}

internal sealed class FixedPrivateReply : IPrivateReplyProvider
{
    private readonly Func<PrivateEvent, string> text;
    internal FixedPrivateReply(Func<PrivateEvent, string> text) { this.text = text; }
    public Task<ReplyDraft> GenerateAsync(PrivateEvent incoming, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var tag = "[local:" + incoming.LocalId + "]";
        return Task.FromResult(new ReplyDraft(text(incoming) + " " + tag, tag));
    }
}

internal sealed class PrivateRunControl : IDisposable
{
    private readonly object gate = new object();
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private volatile bool paused;
    private int revision;
    internal CancellationToken Token { get { return stop.Token; } }
    internal bool Paused { get { return paused; } }
    internal int Revision { get { return Volatile.Read(ref revision); } }
    internal void Pause() { paused = true; Interlocked.Increment(ref revision); }
    internal void Resume() { Token.ThrowIfCancellationRequested(); paused = false; }
    internal void Stop() { stop.Cancel(); }
    internal void Check()
    {
        Token.ThrowIfCancellationRequested();
        if (paused) throw new PrivatePausedException();
    }
    internal void Check(int capturedRevision)
    {
        Check();
        if (capturedRevision != Revision) throw new PrivatePausedException();
    }
    internal void Commit(int capturedRevision, Action record, Action submit)
    {
        lock (gate)
        {
            Check(capturedRevision);
            record();
            Check(capturedRevision);
            submit();
        }
    }
    public void Dispose() { stop.Dispose(); }
}

internal sealed class PrivatePausedException : InvalidOperationException
{
    internal PrivatePausedException() : base("已暂停") { }
}

internal sealed class PrivateReplyCore
{
    private readonly PrivateStore store;
    private readonly IPrivateTransport wire;
    private readonly IPrivateReplyProvider provider;
    private readonly PrivateRunControl control;
    private readonly ReplyTimeline timeline = new ReplyTimeline();
    private readonly Predicate<ReplyRow> permitted;
    private readonly Action<string> log;
    private readonly int confirmationMilliseconds;
    internal bool RecoveryBlocked { get; private set; }
    internal int OwnIgnored { get; private set; }

    internal PrivateReplyCore(PrivateStore store, IPrivateTransport wire, IPrivateReplyProvider provider,
        PrivateRunControl control, Predicate<ReplyRow> permitted, Action<string> log, int confirmationMilliseconds = 14000)
    {
        this.store = store;
        this.wire = wire;
        this.provider = provider;
        this.control = control;
        this.permitted = permitted;
        this.log = log;
        this.confirmationMilliseconds = confirmationMilliseconds;
        if (store.State.Binding == null) throw new InvalidOperationException("请先核验并绑定 QQ 私聊");
    }

    internal void RequireRoute(string bot, string peer)
    {
        var proof = store.State.Binding;
        if (bot != proof.BotQq || peer != proof.PeerQq || wire.Epoch != proof.Epoch)
            throw new InvalidOperationException("请求账号与已核验的单私聊绑定不符");
    }

    internal void Start(string bot, string peer)
    {
        control.Check();
        RequireRoute(bot, peer);
        var first = wire.Read();
        Wait(350);
        var current = wire.Read();
        timeline.Seed(first, current);
        var display = PrivateStore.Observe(current);
        var previous = store.State.Snapshot;
        var positionMatches = previous.Intersect(display).Count();
        log("baseline reset rows=" + display.Length + " priorRows=" + previous.Length +
            " exact-runtime-evidence-overlap=" + positionMatches + " offline-backfill=no");
        foreach (var item in store.State.Events)
        {
            if (item.Phase == PrivatePhase.SubmitIntent || item.Phase == PrivatePhase.Uncertain)
            {
                var match = !String.IsNullOrEmpty(item.RecoveryTag) && store.State.SnapshotEpoch == wire.Epoch &&
                    positionMatches != 0 && item.BotQq == bot && item.PeerQq == peer &&
                    current.Any(row => row.Own == false && row.Key == item.RuntimeKey && row.Digest == item.Digest) &&
                    HasReply(current, item);
                Change(item, match ? PrivatePhase.LocalConfirmed : PrivatePhase.Uncertain,
                    match ? "restart-found-unique-outgoing-tag" : "restart-no-reliable-match-no-resend");
                RecoveryBlocked |= !match;
            }
            else if (IsUnsubmitted(item))
                Change(item, PrivatePhase.Cancelled, "restart-unsubmitted-no-history-replay");
        }
        SaveSnapshot(display);
        if (!RecoveryBlocked) wire.RequireReady();
    }

    private void SaveSnapshot(string[] display)
    {
        store.State.Snapshot = display;
        store.State.SnapshotEpoch = wire.Epoch;
        store.Save();
    }

    private static bool IsUnsubmitted(PrivateEvent item)
    {
        return item.Phase == PrivatePhase.Observed || item.Phase == PrivatePhase.Queued ||
            item.Phase == PrivatePhase.Generated || item.Phase == PrivatePhase.Preparing;
    }

    internal void StopUnsubmitted()
    {
        foreach (var item in store.State.Events.Where(IsUnsubmitted))
            Change(item, PrivatePhase.Cancelled, "stop-before-submit");
    }

    private void Change(PrivateEvent item, PrivatePhase phase, string detail)
    {
        store.Set(item, phase, detail);
        log("local-id=" + item.LocalId + " phase=" + phase + " detail=" + detail);
    }

    internal void Tick()
    {
        control.Token.ThrowIfCancellationRequested();
        if (control.Paused || RecoveryBlocked) return;
        var rows = wire.Read();
        foreach (var row in timeline.Advance(rows))
        {
            if (row.Own == true) { OwnIgnored++; continue; }
            if (row.Own != false || row.Segments != 1 || String.IsNullOrWhiteSpace(row.RawText) || !permitted(row)) continue;
            var proof = store.State.Binding;
            var item = new PrivateEvent {
                LocalId = Guid.NewGuid().ToString("N"), BotQq = proof.BotQq, PeerQq = proof.PeerQq,
                ObservedUtc = PrivateStore.Stamp(), RuntimeKey = row.Key, Digest = row.Digest, Body = row.RawText
            };
            store.State.Events = store.State.Events.Concat(new[] { item }).ToArray();
            Change(item, PrivatePhase.Observed, "uia-new-incoming anchor=" + MessageSnapshot.Hash(row.Anchor));
            Change(item, PrivatePhase.Queued, "fixed-route");
        }
        var evidence = PrivateStore.Observe(rows);
        if (!evidence.SequenceEqual(store.State.Snapshot)) SaveSnapshot(evidence);
        var next = store.State.Events.FirstOrDefault(item =>
            item.Phase == PrivatePhase.Queued || item.Phase == PrivatePhase.Generated);
        if (next != null) Respond(next);
    }

    private ReplyRow[] Verify(PrivateEvent item, int revision, bool checkMatchingReply)
    {
        control.Check(revision);
        RequireRoute(item.BotQq, item.PeerQq);
        var rows = wire.Read();
        timeline.CheckSameConversation(rows);
        if (!rows.Any(row => row.Key == item.RuntimeKey && row.Digest == item.Digest && row.Own == false))
            throw new InvalidOperationException("来信锚点不在当前已核验会话");
        if (checkMatchingReply && item.RecoveryTag != null && rows.Any(row => row.Text == MessageSnapshot.Normalize(item.Reply)))
            throw new InvalidOperationException("发送前已有相同回复，已暂停");
        control.Check(revision);
        return rows;
    }

    private static bool HasReply(ReplyRow[] rows, PrivateEvent item)
    {
        if (String.IsNullOrEmpty(item.Reply)) return false;
        var matches = rows.Where(row => row.Text == MessageSnapshot.Normalize(item.Reply)).ToArray();
        return matches.Length == 1 && matches[0].Own == true;
    }

    private static bool HasNewReply(ReplyRow[] before, ReplyRow[] current, PrivateEvent item)
    {
        int tail = Array.FindIndex(current, row => row.Anchor == before[before.Length - 1].Anchor);
        int overlap = Math.Min(tail + 1, before.Length);
        if (overlap < Math.Min(2, before.Length))
            throw new InvalidOperationException("提交前的消息尾部已变化，不能确认新气泡");
        for (int i = 0; i < overlap; i++)
            if (current[tail - i].Anchor != before[before.Length - 1 - i].Anchor)
                throw new InvalidOperationException("提交前后的气泡顺序无法核对");
        var expected = MessageSnapshot.Normalize(item.Reply);
        var hits = current.Skip(tail + 1).Where(row => row.Own == true && row.Text == expected).ToArray();
        if (hits.Length > 1 || hits.Any(hit => before.Any(row => row.Key == hit.Key)))
            throw new InvalidOperationException("新出站气泡不唯一或重复使用标识");
        return hits.Length == 1;
    }

    private ReplyDraft Generate(PrivateEvent item, int revision)
    {
        using (var request = CancellationTokenSource.CreateLinkedTokenSource(control.Token))
        {
            var pending = provider.GenerateAsync(item, request.Token);
            var lastCheck = Stopwatch.StartNew();
            try
            {
                while (!pending.IsCompleted)
                {
                    Wait(180);
                    control.Check(revision);
                    if (lastCheck.ElapsedMilliseconds < 1000) continue;
                    Verify(item, revision, false);
                    lastCheck.Restart();
                }
                control.Check(revision);
                return pending.GetAwaiter().GetResult();
            }
            finally
            {
                if (!pending.IsCompleted)
                {
                    request.Cancel();
                    pending.ContinueWith(task => GC.KeepAlive(task.Exception), CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
    }

    private void Respond(PrivateEvent item)
    {
        bool crossed = false;
        bool generationStarted = false;
        int revision = control.Revision;
        try
        {
            Verify(item, revision, false);
            int contextLimit = 0;
            if (item.Phase == PrivatePhase.Queued)
            {
                generationStarted = true;
                var draft = Generate(item, revision);
                control.Check(revision);
                if (draft == null || String.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 2000)
                    throw new InvalidOperationException("回复正文无效或超出长度");
                item.Reply = draft.Text;
                item.RecoveryTag = draft.RecoveryTag == "[local:" + item.LocalId + "]" &&
                    draft.Text.EndsWith(draft.RecoveryTag, StringComparison.Ordinal) ? draft.RecoveryTag : null;
                contextLimit = draft.ModelContextLimit;
                Change(item, PrivatePhase.Generated, "reply-provider");
            }
            var before = Verify(item, revision, true);
            wire.RequireReady();
            Change(item, PrivatePhase.Preparing, "before-editor-write");
            wire.Submit(item.Reply, () => before = Verify(item, revision, true), submit => control.Commit(revision,
                () => Change(item, PrivatePhase.SubmitIntent, "durable-before-default-action"),
                () => { crossed = true; submit(); }));
            var until = System.Diagnostics.Stopwatch.StartNew();
            do
            {
                var rows = wire.Read();
                timeline.CheckSameConversation(rows);
                if (HasNewReply(before, rows, item))
                {
                    wire.RequireReady();
                    if (contextLimit > 0)
                        store.State.ModelTurns = ModelReplyContext.Recent(store.State.ModelTurns.Concat(new[] {
                            new PrivateModelTurn { User = item.Body, Assistant = item.Reply }
                        }), contextLimit).ToArray();
                    Change(item, PrivatePhase.LocalConfirmed, "unique-new-outgoing-bubble-and-empty-draft");
                    return;
                }
                Wait(230);
            } while (until.ElapsedMilliseconds < confirmationMilliseconds);
            throw new InvalidOperationException("已提交但没有确认唯一的回执气泡");
        }
        catch (PrivatePausedException)
        {
            if (item.Phase == PrivatePhase.Queued || item.Phase == PrivatePhase.Generated)
            {
                if (generationStarted) Change(item, PrivatePhase.Cancelled, "pause-invalidated-provider-result");
                return;
            }
            Change(item, PrivatePhase.Cancelled, "pause-before-submit-check-draft");
            throw;
        }
        catch (Exception ex)
        {
            if (crossed)
            {
                Change(item, PrivatePhase.Uncertain, "submission-started-no-retry");
                RecoveryBlocked = true;
            }
            else
                Change(item, ex is OperationCanceledException ? PrivatePhase.Cancelled : PrivatePhase.Failed,
                    ex is OperationCanceledException ? "stop-before-submit" :
                    ex is ModelReplyException ? "model-" + ((ModelReplyException)ex).Code : "blocked-before-submit");
            throw;
        }
    }

    private void Wait(int ms)
    {
        if (control.Token.WaitHandle.WaitOne(ms)) control.Token.ThrowIfCancellationRequested();
    }
}
