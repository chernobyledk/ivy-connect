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

internal interface ICheckedPrivateTransport
{
    void SubmitChecked(string text, Action<ReplyRow[]> verify, Action<Action, Action> commit, Action<string> checkpoint);
    ReplyRow[] ReadAfterSubmit(out bool draftEmpty);
}

internal sealed class PrivateRouteLostException : InvalidOperationException
{
    internal PrivateRouteLostException(string detail) : base(detail) { }
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
    internal void Commit(int capturedRevision, Action record, Action prepare, Action submit)
    {
        lock (gate)
        {
            Check(capturedRevision);
            prepare();
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
    private readonly Action<string> progress;
    private readonly string bindingVersion;
    private readonly Stopwatch sessionClock = Stopwatch.StartNew();
    internal bool RecoveryBlocked { get; private set; }
    internal bool BindingInvalidated { get; private set; }
    internal int OwnIgnored { get; private set; }

    internal PrivateReplyCore(PrivateStore store, IPrivateTransport wire, IPrivateReplyProvider provider,
        PrivateRunControl control, Predicate<ReplyRow> permitted, Action<string> log, int confirmationMilliseconds = 18000,
        Action<string> progress = null)
    {
        this.store = store;
        this.wire = wire;
        this.provider = provider;
        this.control = control;
        this.permitted = permitted;
        this.log = log;
        this.confirmationMilliseconds = confirmationMilliseconds;
        this.progress = progress ?? (_ => { });
        if (store.State.Binding == null) throw new InvalidOperationException("请先核验并绑定 QQ 私聊");
        bindingVersion = store.State.Binding.RouteStamp();
    }

    internal void RequireRoute(string bot, string peer)
    {
        var proof = store.State.Binding;
        if (proof == null || bot != proof.BotQq || peer != proof.PeerQq || wire.Epoch != proof.Epoch ||
            proof.RouteStamp() != bindingVersion)
            throw new PrivateRouteLostException("请求账号、窗口代际或绑定版本发生变化，请重新核验");
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
            if (PrivateStore.IsPending(item))
            {
                string reason;
                var match = ReceiptReview.Find(store, wire, item, control.Token, 0, progress, out reason);
                if (match) ConfirmLocal(item, reason);
                else if (item.Phase == PrivatePhase.SubmitIntent) Change(item, PrivatePhase.Uncertain, "重启后仍未核实提交。" + reason);
                RecoveryBlocked |= !match;
            }
            else if (PrivateStore.IsUnsubmitted(item))
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

    internal void StopUnsubmitted()
    {
        foreach (var item in store.State.Events.Where(PrivateStore.IsUnsubmitted))
            Change(item, PrivatePhase.Cancelled, "stop-before-submit");
    }

    private void Change(PrivateEvent item, PrivatePhase phase, string detail)
    {
        store.Set(item, phase, detail);
        log("local-id=" + item.LocalId + " phase=" + phase + " detail=" + detail);
    }

    private void ConfirmLocal(PrivateEvent item, string detail)
    {
        store.ConfirmLocal(item, detail);
        log("local-id=" + item.LocalId + " phase=LocalConfirmed detail=" + detail);
        Timing(item, "local-confirm");
    }

    internal void Tick()
    {
        try { TickBound(); }
        catch (PrivateRouteLostException)
        {
            BindingInvalidated = true;
            control.Pause();
            foreach (var item in store.State.Events.Where(PrivateStore.IsUnsubmitted))
                Change(item, PrivatePhase.Cancelled, "binding-invalidated-before-submit");
            throw;
        }
    }

    private void TickBound()
    {
        control.Token.ThrowIfCancellationRequested();
        if (control.Paused || RecoveryBlocked || BindingInvalidated) return;
        var current = store.State.Binding;
        if (current == null) throw new PrivateRouteLostException("原直播绑定已撤销");
        RequireRoute(current.BotQq, current.PeerQq);
        var scanning = sessionClock.ElapsedMilliseconds;
        var rows = wire.Read();
        var readFinished = sessionClock.ElapsedMilliseconds;
        ReplyRow[] added;
        try { added = timeline.Advance(rows); }
        catch (InvalidOperationException) { throw new PrivateRouteLostException("消息列表连续性已丢失，请重新核验原私聊"); }
        foreach (var row in added)
        {
            if (row.Own == true) { OwnIgnored++; continue; }
            if (row.Own != false || row.Segments != 1 || String.IsNullOrWhiteSpace(row.RawText) ||
                store.IsEnrollmentCode(row.RawText) || !permitted(row)) continue;
            var proof = store.State.Binding;
            var item = new PrivateEvent {
                LocalId = Guid.NewGuid().ToString("N"), BotQq = proof.BotQq, PeerQq = proof.PeerQq,
                ObservedUtc = PrivateStore.Stamp(), RuntimeKey = row.Key, Digest = row.Digest, Body = row.RawText,
                BindingVersion = bindingVersion
            };
            log("timing id=" + item.LocalId + " stage=read-and-discover mono-ms=" + readFinished + " scan-ms=" + (readFinished - scanning));
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
        VerifyRows(item, revision, checkMatchingReply, rows);
        return rows;
    }

    private void VerifyRows(PrivateEvent item, int revision, bool checkMatchingReply, ReplyRow[] rows)
    {
        control.Check(revision);
        RequireRoute(item.BotQq, item.PeerQq);
        if (item.BindingVersion != bindingVersion) throw new PrivateRouteLostException("任务所属的绑定已经失效");
        try { timeline.CheckSameConversation(rows); }
        catch (InvalidOperationException) { throw new PrivateRouteLostException("当前消息列表已失去原会话的连续锚点"); }
        if (!rows.Any(row => row.Key == item.RuntimeKey && row.Digest == item.Digest && row.Own == false))
            throw new PrivateRouteLostException("来信锚点不在当前已核验会话");
        if (checkMatchingReply && item.RecoveryTag != null && rows.Any(row => row.Text == MessageSnapshot.Normalize(item.Reply)))
            throw new InvalidOperationException("发送前已有相同回复，已暂停");
        control.Check(revision);
    }

    private void Timing(PrivateEvent item, string stage)
    {
        log("timing id=" + item.LocalId + " stage=" + stage + " mono-ms=" + sessionClock.ElapsedMilliseconds);
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
            // Tick has just verified the live route and observed this turn. Do not take another full snapshot here.
            control.Check(revision);
            RequireRoute(item.BotQq, item.PeerQq);
            Timing(item, "identity-before-model");
            if (item.Phase == PrivatePhase.Queued)
            {
                generationStarted = true;
                Timing(item, "provider-start");
                var draft = Generate(item, revision);
                Timing(item, "provider-end");
                control.Check(revision);
                if (draft == null || String.IsNullOrWhiteSpace(draft.Text) || draft.Text.Length > 2000)
                    throw new InvalidOperationException("回复正文无效或超出长度");
                item.Reply = draft.Text;
                item.RecoveryTag = draft.RecoveryTag == "[local:" + item.LocalId + "]" &&
                    draft.Text.EndsWith(draft.RecoveryTag, StringComparison.Ordinal) ? draft.RecoveryTag : null;
                item.ContextLimit = draft.ModelContextLimit;
                Change(item, PrivatePhase.Generated, "reply-provider");
            }
            ReplyRow[] before = null;
            Change(item, PrivatePhase.Preparing, "before-editor-write");
            var checkedWire = wire as ICheckedPrivateTransport;
            Action<Action, Action> submitChecked = (prepare, submit) => control.Commit(revision,
                () => {
                    item.Submission = ReceiptReview.Capture(wire.Epoch, before);
                    Change(item, PrivatePhase.SubmitIntent, "durable-tail-before-default-action");
                },
                prepare, () => {
                    crossed = true;
                    Timing(item, "submit");
                    submit();
                    item.Submission.ReturnedUtc = PrivateStore.Stamp();
                });
            if (checkedWire != null)
                checkedWire.SubmitChecked(item.Reply, rows => { VerifyRows(item, revision, true, rows); before = rows; },
                    submitChecked, checkpoint => Timing(item, checkpoint));
            else
            {
                before = Verify(item, revision, true);
                wire.RequireReady();
                wire.Submit(item.Reply, () => before = Verify(item, revision, true), submit => submitChecked(() => { }, submit));
            }
            string reason;
            if (!ReceiptReview.Find(store, wire, item, control.Token, confirmationMilliseconds, progress, out reason))
                throw new PrivateRouteLostException(reason);
            ConfirmLocal(item, reason);
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
            if (item.Phase == PrivatePhase.LocalConfirmed) throw;
            if (crossed)
            {
                RecoveryBlocked = true;
                Change(item, PrivatePhase.Uncertain, ex is PrivateRouteLostException ? ex.Message : "已开始提交，结果尚无证据。请重新核查。");
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
