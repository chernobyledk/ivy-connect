using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class RouteChecks
{
    private sealed class Wire : IPrivateTransport, ICheckedPrivateTransport
    {
        internal readonly List<ReplyRow> Rows = new List<ReplyRow> { Row("a","old"), Row("b","baseline") };
        internal string Situation, Draft;
        internal int Writes, Sends;
        internal bool LoseBeforeWrite, LoseBeforeCommit, MissingBubble;
        private static ReplyRow Row(string id, string message) { return new ReplyRow(id, new[] { message }, false); }
        internal void Add() { Rows.Add(Row("c","test")); Rows.Add(Row("d","queued")); }
        public string Epoch { get { return "verified"; } }
        public void RequireReady() { if (Draft != null) throw new InvalidOperationException("draft present"); }
        public ReplyRow[] Read()
        {
            if (Situation != null) throw new PrivateRouteLostException(Situation);
            return Rows.ToArray();
        }
        public ReplyRow[] ReadAfterSubmit(out bool draftEmpty) { draftEmpty = Draft == null; return Read(); }
        public void Submit(string text, Action verify, Action<Action> commit) { throw new Exception("unguarded path"); }
        public void SubmitChecked(string text, Action<ReplyRow[]> verify, Action<Action, Action> commit, Action<string> checkpoint)
        {
            if (LoseBeforeWrite) Situation = "changed just before write";
            verify(Read());
            RequireReady();
            Writes++;
            Draft = text;
            commit(() => {
                if (LoseBeforeCommit) Situation = "changed after intent and before commit";
                verify(Read());
                if (Draft != text) throw new InvalidOperationException("draft changed");
            }, () => {
                Sends++;
                Draft = null;
                if (!MissingBubble) Rows.Add(new ReplyRow("out", new[] { text }, true));
            });
        }
    }

    private sealed class LateModel : IPrivateReplyProvider, IDisposable
    {
        internal readonly ManualResetEvent Called = new ManualResetEvent(false);
        internal readonly TaskCompletionSource<ReplyDraft> Result = new TaskCompletionSource<ReplyDraft>();
        public Task<ReplyDraft> GenerateAsync(PrivateEvent item, CancellationToken token) { Called.Set(); return Result.Task; }
        public void Dispose() { Called.Dispose(); }
    }

    private static void Assert(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    private static PrivateReplyCore Start(PrivateStore store, Wire wire, PrivateRunControl run, IPrivateReplyProvider reply)
    {
        store.State.Binding = new PrivateBindingProof { BotQq = "10001", PeerQq = "10002", Window = 1, Version = "one", Epoch = wire.Epoch };
        var core = new PrivateReplyCore(store, wire, reply, run, row => true, line => { }, 100);
        core.Start("10001", "10002");
        wire.Add();
        return core;
    }

    private static void Tick(PrivateReplyCore core)
    {
        try { core.Tick(); }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
    }

    private static void Check(string root, string name)
    {
        using (var store = new PrivateStore(Path.Combine(root, name)))
        using (var run = new PrivateRunControl())
        {
            var wire = new Wire();
            var core = Start(store, wire, run, new FixedPrivateReply(item => "test reply"));
            if (name == "before-write") wire.LoseBeforeWrite = true;
            if (name == "before-commit") wire.LoseBeforeCommit = true;
            if (name == "human-draft") wire.Draft = "human";
            if (name == "uncertain") wire.MissingBubble = true;
            Tick(core);
            if (name == "uncertain")
            {
                Tick(core);
                Assert(wire.Sends == 1 && core.RecoveryBlocked && store.State.Events[0].Phase == PrivatePhase.Uncertain,
                    "uncertain action stays pending and is never retried");
                return;
            }
            Assert(wire.Sends == 0 &&
                (name == "human-draft" ? wire.Writes == 0 && wire.Draft == "human" : core.BindingInvalidated &&
                store.State.Events[1].Phase == PrivatePhase.Cancelled && store.State.Events[0].Phase == PrivatePhase.Failed),
                name + " refuses send; safe drafts and old queued items handled");
        }
    }

    private static void DuringModel(string root, string condition)
    {
        using (var store = new PrivateStore(Path.Combine(root, condition)))
        using (var run = new PrivateRunControl())
        using (var model = new LateModel())
        {
            var wire = new Wire();
            var core = Start(store, wire, run, model);
            var worker = Task.Run(() => Tick(core));
            if (!model.Called.WaitOne(3000)) throw new InvalidOperationException("model fixture did not start");
            if (condition == "new-binding-version") store.State.Binding.Version = "two";
            else if (condition == "stop") run.Stop();
            else if (condition == "pause-resume") { run.Pause(); run.Resume(); }
            else wire.Situation = condition;
            model.Result.SetResult(new ReplyDraft("late output", null));
            worker.GetAwaiter().GetResult();
            Assert(wire.Writes == 0 && wire.Sends == 0 &&
                (condition == "stop" || condition == "pause-resume" || core.BindingInvalidated &&
                store.State.Events[1].Phase == PrivatePhase.Cancelled), condition + " during model never writes or submits");
        }
    }

    private static void Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "qq-route-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var test in new[] { "before-write", "before-commit", "human-draft", "uncertain" }) Check(root, test);
            foreach (var condition in new[] { "private-switch", "group-switch", "recreated-window", "wrong-login",
                "namesake-without-anchor", "new-binding-version", "stop", "pause-resume" }) DuringModel(root, condition);
            using (var store = new PrivateStore(Path.Combine(root, "replace")))
            {
                var prior = new PrivateBindingProof { BotQq = "10001", PeerQq = "10002", Version = "one" };
                var queued = new PrivateEvent { Phase = PrivatePhase.Queued, BindingVersion = prior.RouteStamp() };
                var uncertain = new PrivateEvent { Phase = PrivatePhase.Uncertain, BindingVersion = prior.RouteStamp() };
                store.State.Binding = prior;
                store.State.Events = new[] { queued, uncertain };
                bool rejected = false;
                try { store.ReplaceBinding(new PrivateBindingProof { BotQq = "10001", PeerQq = "10003", Version = "two" }); }
                catch (InvalidOperationException) { rejected = true; }
                store.ReplaceBinding(new PrivateBindingProof { BotQq = "10001", PeerQq = "10002", Version = "two" });
                Assert(rejected && queued.Phase == PrivatePhase.Cancelled && uncertain.Phase == PrivatePhase.Uncertain,
                    "durable rebind refuses other accounts, cancels queued and retains uncertain");
            }
            Assert(QqPlacement.RightSide(new Rectangle(60, 390, 800, 620), new Rectangle(-1700, 20, 1700, 900))
                == new Rectangle(-800, 300, 800, 620), "layout side uses work rectangle and preserves size");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)).TrimEnd(Path.DirectorySeparatorChar) ==
                Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
