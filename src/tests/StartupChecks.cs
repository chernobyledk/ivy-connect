using System;

internal static class StartupChecks
{
    private static readonly PrivateBindingProof Prior = new PrivateBindingProof {
        BotQq = "10001", PeerQq = "10002", BotDisplay = "Robot", PeerDisplay = "Peer", Version = "old",
        Window = 10, Epoch = "123/start/10", LoginProof = "login", HeaderProof = "header", ChallengeAnchor = "challenge"
    };

    private static QqCandidate Candidate(long window = 10, string epoch = "123/start/10")
    {
        return new QqCandidate { Window = window, Epoch = epoch, Visible = true, Enabled = true,
            Login = "Robot", Chat = "Peer", HeaderProof = "header", LoginProof = "login", AnchorMatches = true, EditorReady = true };
    }

    private static void Assert(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    private static int Main()
    {
        try
        {
            var ready = QqConnection.Resolve(Prior, new[] { Candidate() });
            Assert(ready.State == QqConnectState.Ready && ReferenceEquals(Prior, ready.Binding), "current proof connects without changing version");
            var replacement = QqConnection.Resolve(Prior, new[] { Candidate(42, "123/start/42") });
            Assert(replacement.State == QqConnectState.Ready && replacement.Binding.Window == 42 &&
                replacement.Binding.Version != Prior.Version && replacement.Binding.BotQq == Prior.BotQq &&
                replacement.Binding.PeerQq == Prior.PeerQq, "same exact evidence relocates to a current handle and creates a new binding version");
            var otherProcess = QqConnection.Resolve(Prior, new[] { Candidate(42, "123/restarted/42") });
            Assert(otherProcess.State == QqConnectState.NeedsVerification && otherProcess.Binding == null, "recreated QQ process requires fresh identity proof");
            var namesake = Candidate(43, "123/start/43");
            namesake.AnchorMatches = false;
            Assert(QqConnection.Resolve(Prior, new[] { namesake }).State == QqConnectState.NeedsVerification, "same display names alone cannot connect");
            Assert(QqConnection.Resolve(Prior, new[] { namesake, Candidate() }).Binding == Prior, "a unique verified route can win over a namesake");
            Assert(QqConnection.Resolve(Prior, new[] { Candidate(), Candidate(42, "123/start/42") }).State == QqConnectState.ChooseWindow,
                "two proven candidates cannot be selected silently");
            var reversed = Candidate();
            reversed.Login = "Peer";
            reversed.Chat = "Robot";
            Assert(QqConnection.Resolve(Prior, new[] { reversed }).State == QqConnectState.NeedsChat, "reverse account orientation cannot connect");
            var window = Candidate();
            window.Visible = false;
            Assert(QqConnection.Resolve(Prior, new[] { window }).State == QqConnectState.Hidden, "hidden separated from minimization");
            window.Minimized = true;
            Assert(QqConnection.Resolve(Prior, new[] { window }).State == QqConnectState.Minimized, "taskbar minimized rejected");
            window = Candidate();
            window.HasDraft = true;
            Assert(QqConnection.Resolve(Prior, new[] { window }).State == QqConnectState.Draft, "manual draft blocks start");
            Assert(QqConnection.Resolve(null, new[] { new QqCandidate { LoginPrompt = true } }).State == QqConnectState.NeedsLogin,
                "recognized QQ login prompt leads to login guidance");
            Assert(QqConnection.Resolve(null, new[] { Candidate() }).State == QqConnectState.NeedsVerification, "first connection requires operator verification");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
