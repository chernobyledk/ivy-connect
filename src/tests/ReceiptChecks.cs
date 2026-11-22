using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal static class ReceiptChecks
{
    private const string Epoch = "123/epoch/10";
    private static ReplyRow Row(string key, string text, bool own) { return new ReplyRow(key, new[] { text }, own); }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    private static PrivateBindingProof Proof()
    {
        return new PrivateBindingProof { BotQq = "10001", PeerQq = "10002", BotDisplay = "Robot", PeerDisplay = "Peer",
            Window = 10, Epoch = Epoch, LoginProof = "login", HeaderProof = "header", ConversationProof = "selected-route",
            VerifiedUtc = PrivateStore.Stamp(), ChallengeAnchor = "past-challenge", Version = "version" };
    }

    private static PrivateEvent Pending(PrivateBindingProof proof, ReplyRow[] rows)
    {
        return new PrivateEvent { LocalId = Guid.NewGuid().ToString("N"), BotQq = proof.BotQq, PeerQq = proof.PeerQq,
            ObservedUtc = DateTimeOffset.UtcNow.AddSeconds(-5).ToString("O"), Body = rows[0].RawText, Reply = "reply",
            RuntimeKey = rows[0].Key, Digest = rows[0].Digest, BindingVersion = proof.RouteStamp(), ContextLimit = 2,
            Phase = PrivatePhase.SubmitIntent, Submission = ReceiptReview.Capture(proof.Epoch, rows) };
    }

    private sealed class NeverGenerate : IPrivateReplyProvider
    {
        public Task<ReplyDraft> GenerateAsync(PrivateEvent item, CancellationToken token) { throw new InvalidOperationException("unexpected model invocation"); }
    }

    private sealed class ReadWire : IPrivateTransport, ICheckedPrivateTransport
    {
        internal ReplyRow[] Rows;
        internal int FailFirstRead;
        public string Epoch { get { return ReceiptChecks.Epoch; } }
        public void RequireReady() { }
        public ReplyRow[] Read() { return Rows; }
        public ReplyRow[] ReadAfterSubmit(out bool draftEmpty)
        {
            if (FailFirstRead-- > 0) throw new PrivateRouteLostException("simulated temporary UI failure");
            draftEmpty = false;
            return Rows;
        }
        public void Submit(string text, Action verify, Action<Action> commit) { throw new InvalidOperationException("unexpected submit"); }
        public void SubmitChecked(string text, Action<ReplyRow[]> verify, Action<Action, Action> commit, Action<string> checkpoint)
        { throw new InvalidOperationException("unexpected submit"); }
    }

    private static int Main()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ivy-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var proof = Proof();
            var original = new[] { Row("original", "incoming", false), Row("older", "reply", true),
                Row("a", "a", false), Row("b", "b", true), Row("c", "c", false), Row("d", "d", true) };
            var item = Pending(proof, original);
            var outgoing = Row("new", "reply", true);
            var appended = original.Skip(2).Concat(new[] { outgoing }).ToArray();
            var rebuilt = appended.Select((row, i) => Row("recreated" + i, row.RawText, row.Own == true)).ToArray();
            string reason;
            Check(ReceiptReview.Match(item, appended, DateTimeOffset.UtcNow, true, out reason), "original incoming and challenge can leave visible history; new outgoing relative to saved tail confirms");
            Check(!ReceiptReview.Match(item, original, DateTimeOffset.UtcNow, true, out reason), "identical old reply before tail is not treated as this task");
            Check(ReceiptReview.Match(item, rebuilt, DateTimeOffset.UtcNow, true, out reason), "four unique ordered anchors plus direction handle rebuilt UI identifiers in a short window");
            Check(!ReceiptReview.Match(item, rebuilt, DateTimeOffset.UtcNow.AddMinutes(3), true, out reason), "rebuilt identifier fallback is time bounded");
            Check(!ReceiptReview.Match(item, appended.Concat(new[] { Row("copy", "reply", true) }).ToArray(), DateTimeOffset.UtcNow, true, out reason), "two new same-text outgoing hits remain uncertain");
            Check(!ReceiptReview.Match(item, rebuilt, DateTimeOffset.UtcNow, false, out reason), "content-order fallback requires verified selected conversation proof");

            var candidate = new QqCandidate { Window = 10, Epoch = Epoch, Visible = true, Enabled = true, EditorReady = true,
                Login = "Robot", Chat = "Peer", HeaderProof = "header", LoginProof = "login", ConversationProof = "selected-route", AnchorMatches = false };
            Check(QqConnection.Resolve(proof, new[] { candidate }).State == QqConnectState.Ready, "bound selected recent conversation survives historical challenge leaving the window");
            candidate.ConversationProof = "impostor-route";
            candidate.AnchorMatches = true;
            Check(QqConnection.Resolve(proof, new[] { candidate }).Binding == null, "same nickname and reused header cannot override a changed selected conversation");

            using (var store = new PrivateStore(folder))
            {
                store.State.Binding = proof;
                store.State.Events = new[] { item };
                store.Save();
            }
            using (var store = new PrivateStore(folder))
            using (var control = new PrivateRunControl())
            {
                var wire = new ReadWire { Rows = original };
                var core = new PrivateReplyCore(store, wire, new NeverGenerate(), control, _ => true, _ => { });
                core.Start(proof.BotQq, proof.PeerQq);
                Check(core.RecoveryBlocked && PrivateStore.IsPending(store.State.Events[0]), "crash around submission with no new outgoing never resends");
                wire.Rows = appended;
                wire.FailFirstRead = 1;
                Check(ReceiptReview.Find(store, wire, store.State.Events[0], control.Token, 1000, _ => { }, out reason), "explicit read-only review recovers after a temporary UI read failure");
                store.ConfirmLocal(store.State.Events[0], reason);
                Check(store.State.Events[0].Phase == PrivatePhase.LocalConfirmed && store.State.Events[0].PeerConfirmedUtc == null, "local bubble leaves peer acknowledgement separate");
            }
            using (var store = new PrivateStore(folder))
            using (var control = new PrivateRunControl())
            {
                var wire = new ReadWire { Rows = appended };
                var core = new PrivateReplyCore(store, wire, new NeverGenerate(), control, _ => true, _ => { });
                core.Start(proof.BotQq, proof.PeerQq);
                core.Tick();
                Check(!core.RecoveryBlocked && store.State.Events.Length == 1 && store.State.ModelTurns.Length == 1, "confirmed state and context survive restart without replay");
                var manual = Pending(proof, original);
                var alreadySent = store.State.Events[0];
                store.State.Events = new[] { alreadySent, manual };
                store.ConfirmPeer(manual);
                Check(manual.Phase != PrivatePhase.LocalConfirmed && !PrivateStore.IsPending(manual) &&
                    manual.PeerReceiptSource == "operator-reported-peer-client", "human peer receipt is persisted independently of automated local confirmation");
            }
            using (var store = new PrivateStore(folder))
            using (var control = new PrivateRunControl())
            {
                var core = new PrivateReplyCore(store, new ReadWire { Rows = appended }, new NeverGenerate(), control, _ => true, _ => { });
                core.Start(proof.BotQq, proof.PeerQq);
                core.Tick();
                Check(!core.RecoveryBlocked && store.State.Events.Length == 2, "operator-confirmed task does not block fresh verified start and is never replayed");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex.Message); return 1; }
        finally
        {
            File.Delete(Path.Combine(folder, "private-state.dpapi"));
            File.Delete(Path.Combine(folder, "state.lock"));
            if (Directory.Exists(folder)) Directory.Delete(folder);
        }
    }
}
