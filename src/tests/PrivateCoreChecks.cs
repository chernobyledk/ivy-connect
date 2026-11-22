using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class PrivateCoreChecks
{
    private const string Bot = "100000001", Peer = "100000002", Epoch = "qq-window-a";

    private sealed class Wire : IPrivateTransport
    {
        internal readonly List<ReplyRow> Rows = new List<ReplyRow> { Incoming("h1", "history"), Incoming("h2", "older") };
        internal Action BeforeCommit;
        internal int Attempts;
        internal bool DropBubble, Broken;
        internal string Draft;
        public string Epoch { get { return PrivateCoreChecks.Epoch; } }
        public void RequireReady()
        {
            Read();
            if (Draft != null) throw new InvalidOperationException("draft remains");
        }
        public ReplyRow[] Read()
        {
            if (Broken) throw new InvalidOperationException("conversation changed");
            return Rows.ToArray();
        }
        public void Submit(string text, Action verifyBefore, Action<Action> commit)
        {
            verifyBefore();
            Draft = text;
            if (BeforeCommit != null) BeforeCommit();
            verifyBefore();
            commit(() => {
                Attempts++;
                Draft = null;
                if (!DropBubble) Rows.Add(new ReplyRow("reply-" + Attempts, new[] { text }, true));
            });
        }
    }
    private sealed class WaitingReply : IPrivateReplyProvider
    {
        internal readonly ManualResetEvent Entered = new ManualResetEvent(false);
        internal readonly TaskCompletionSource<ReplyDraft> Answer = new TaskCompletionSource<ReplyDraft>();
        public Task<ReplyDraft> GenerateAsync(PrivateEvent item, CancellationToken token)
        {
            Entered.Set();
            return Answer.Task;
        }
    }
    private static ReplyRow Incoming(string key, string body) { return new ReplyRow(key, new[] { body }, false); }
    private static void Assert(bool result, string name)
    {
        if (!result) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }
    private static void ExpectHalt(Action action)
    {
        try { action(); }
        catch (OperationCanceledException) { return; }
        catch (InvalidOperationException) { return; }
        throw new Exception("expected halt");
    }
    private static PrivateReplyCore Core(PrivateStore store, Wire wire, PrivateRunControl controller,
        IPrivateReplyProvider provider = null)
    {
        if (store.State.Binding == null)
        {
            store.State.Binding = new PrivateBindingProof { BotQq = Bot, PeerQq = Peer, Epoch = Epoch };
            store.Save();
        }
        return new PrivateReplyCore(store, wire, provider ?? new FixedPrivateReply(item => "received"), controller,
            row => true, text => { }, 250);
    }
    private static void RestartAndDuplicates(string folder)
    {
        var wire = new Wire();
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            ExpectHalt(() => core.Start(Bot, "999999999"));
            Assert(wire.Attempts == 0, "wrong peer rejected before any action");
            core.Start(Bot, Peer);
            core.Tick();
            wire.Rows.Add(Incoming("n1", "same"));
            wire.Rows.Add(Incoming("n2", "same"));
            core.Tick(); core.Tick(); core.Tick(); core.Tick();
            Assert(wire.Attempts == 2 && store.State.Events.Length == 2 && core.OwnIgnored == 2 &&
                store.State.Events.Select(x => x.LocalId).Distinct().Count() == 2 &&
                store.State.Events.All(x => x.Phase == PrivatePhase.LocalConfirmed),
                "history, own replies and repeated scans ignored; same-body occurrences kept separate");
        }
        // Provider/UIA window keys may change during reconstruction. Startup folds current view into its baseline.
        wire.Rows.Add(Incoming("offline", "arrived while down"));
        for (int i = 0; i < wire.Rows.Count; i++)
            wire.Rows[i] = new ReplyRow("new-" + i, new[] { wire.Rows[i].RawText }, wire.Rows[i].Own);
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            core.Start(Bot, Peer); core.Tick();
            Assert(wire.Attempts == 2 && store.State.Events.Length == 2, "restart with rebuilt RuntimeIds never replays or backfills");
            wire.Rows.Add(Incoming("after", "new after restart"));
            core.Tick();
            Assert(wire.Attempts == 3, "fresh arrival after restart still handled");
        }
    }
    private static void Pending(string folder, bool delayedBubble)
    {
        var wire = new Wire { DropBubble = true };
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            core.Start(Bot, Peer);
            wire.Rows.Add(Incoming("pending", "pending"));
            ExpectHalt(core.Tick);
            Assert(wire.Attempts == 1 && store.State.Events.Single().Phase == PrivatePhase.Uncertain,
                "submitted without bubble marked uncertain");
            if (delayedBubble)
            {
                var item = store.State.Events.Single();
                // Disk reflects a crash after durable intent and before saving the UI observation.
                store.Set(item, PrivatePhase.SubmitIntent, "interrupted-write");
                wire.Rows.Add(new ReplyRow("late", new[] { item.Reply }, true));
            }
        }
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            core.Start(Bot, Peer); core.Tick(); core.Tick();
            Assert(wire.Attempts == 1 && core.RecoveryBlocked == !delayedBubble &&
                store.State.Events.Single().Phase == (delayedBubble ? PrivatePhase.LocalConfirmed : PrivatePhase.Uncertain),
                delayedBubble ? "unsaved prior bubble reconciled after restart without resubmission" :
                    "pending across restart stops sending and never resubmits");
        }
    }
    private static void Cutoff(string folder, string kind)
    {
        var wire = new Wire();
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            core.Start(Bot, Peer);
            wire.Rows.Add(Incoming("change", "new"));
            wire.BeforeCommit = () => {
                if (kind == "switch") wire.Broken = true;
                else if (kind == "pause") ctl.Pause();
                else ctl.Stop();
            };
            ExpectHalt(core.Tick);
            Assert(wire.Attempts == 0 && store.State.Events.Single().Phase ==
                (kind == "switch" ? PrivatePhase.Failed : PrivatePhase.Cancelled),
                kind + " after draft preparation prevents submission");
        }
    }
    private static void LateProvider(string folder)
    {
        var wire = new Wire();
        var provider = new WaitingReply();
        using (provider.Entered)
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl, provider);
            core.Start(Bot, Peer);
            wire.Rows.Add(Incoming("late", "new"));
            var worker = Task.Run(() => ExpectHalt(core.Tick));
            if (!provider.Entered.WaitOne(3000)) throw new Exception("provider did not start");
            ctl.Stop();
            provider.Answer.SetResult(new ReplyDraft("late answer", null));
            worker.GetAwaiter().GetResult();
            Assert(wire.Attempts == 0 && store.State.Events.Single().Phase == PrivatePhase.Cancelled,
                "stopping before delayed provider completion forbids later send");
        }
    }
    private static void UnsubmittedRestart(string folder)
    {
        var wire = new Wire();
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            store.State.Events = new[] { PrivatePhase.Observed, PrivatePhase.Queued, PrivatePhase.Generated,
                PrivatePhase.Preparing }.Select(phase => new PrivateEvent { Phase = phase, BotQq = Bot, PeerQq = Peer,
                    LocalId = Guid.NewGuid().ToString("N") }).ToArray();
            store.Save();
        }
        using (var store = new PrivateStore(folder))
        using (var ctl = new PrivateRunControl())
        {
            var core = Core(store, wire, ctl);
            core.Start(Bot, Peer); core.Tick();
            Assert(wire.Attempts == 0 && store.State.Events.All(x => x.Phase == PrivatePhase.Cancelled),
                "restart cancels observed, queued, generated and prepared events with no recorded intent");
        }
    }
    private static void Main()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temp, "qq-phase1-check-" + Guid.NewGuid().ToString("N")));
        try
        {
            RestartAndDuplicates(Path.Combine(root, "duplicates"));
            Pending(Path.Combine(root, "pending"), false);
            Pending(Path.Combine(root, "commit"), true);
            foreach (var kind in new[] { "switch", "stop", "pause" }) Cutoff(Path.Combine(root, kind), kind);
            LateProvider(Path.Combine(root, "late"));
            UnsubmittedRestart(Path.Combine(root, "restart"));
        }
        finally
        {
            if (Path.GetDirectoryName(root).TrimEnd(Path.DirectorySeparatorChar) ==
                temp.TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
