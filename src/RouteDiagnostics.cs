using System;
using System.Diagnostics;
using System.Linq;

internal static class RouteDiagnostics
{
    internal static int CopyFixture(string source, string destination)
    {
        using (var prior = new PrivateStore(source))
        using (var fixture = new PrivateStore(destination))
        {
            if (fixture.State.Binding != null || fixture.State.Events.Length != 0 ||
                prior.State.Binding == null || prior.State.Events.Any(e => e.Phase == PrivatePhase.SubmitIntent || e.Phase == PrivatePhase.Uncertain))
                throw new InvalidOperationException("原状态有待确认项或测试目录已有数据");
            fixture.State.Binding = prior.State.Binding;
            fixture.Save();
            Console.WriteLine("fixture binding copied without chat, keys or existing event ledger");
            return 0;
        }
    }

    internal static int Benchmark(string folder)
    {
        using (var store = new PrivateStore(folder))
        {
            var proof = store.State.Binding;
            var target = QqConnection.Pinned(proof);
            var ready = QqConnection.VerifyUnique(proof, () => { }, Console.WriteLine);
            Console.WriteLine("auto-readiness unique hwnd=" + ready + " geometry=" + Native.Bounds(target.Hwnd) +
                " front-is-target=" + (Native.GetForegroundWindow() == target.Hwnd));
            var bound = new Binding(target, false, true);
            for (int i = 0; i < 3; i++)
            {
                var first = Observe(target, proof, bound, i % 2 == 0);
                var second = Observe(target, proof, bound, i % 2 != 0);
                if (!first.SequenceEqual(second)) throw new InvalidOperationException("两种扫描的气泡证据不同，不能比较");
            }
            return 0;
        }
    }

    private static string[] Observe(Target target, PrivateBindingProof proof, Binding bound, bool fast)
    {
        var sw = Stopwatch.StartNew();
        var current = Controls.Scan(target, fast);
        if (!bound.Matches(current) || bound.HeaderProof != proof.HeaderProof ||
            current.LoginEvidence(proof.BotDisplay) != proof.LoginProof || current.HasVisibleConnectionWarning() || !current.EmptyDraft())
            throw new PrivateRouteLostException("只读计时无法复核原会话和空草稿");
        var rows = current.ReplyRows();
        if (rows.Count(row => row.Own == false && MessageSnapshot.Hash(row.Anchor) == proof.ChallengeAnchor) != 1)
            throw new PrivateRouteLostException("只读计时未见唯一的原核验入站锚点");
        Console.WriteLine("scan mode=" + (fast ? "batched" : "legacy") + " ms=" + sw.ElapsedMilliseconds + " rows=" + rows.Length);
        return PrivateStore.Observe(rows);
    }
}
