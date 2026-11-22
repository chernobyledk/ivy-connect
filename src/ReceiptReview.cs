using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

public sealed class PrivateSubmissionEvidence
{
    public string Epoch, IntentUtc, ReturnedUtc;
    public string[] Before = new string[0];
}

internal static class ReceiptReview
{
    internal static string Fingerprint(ReplyRow row)
    {
        return MessageSnapshot.Hash(row.Key) + ":" + row.Digest + ":" + (row.Own == true ? "O" : row.Own == false ? "I" : "?");
    }

    internal static PrivateSubmissionEvidence Capture(string epoch, ReplyRow[] before)
    {
        if (before == null || before.Length == 0) throw new InvalidOperationException("提交前没有可靠的消息基线");
        return new PrivateSubmissionEvidence { Epoch = epoch, IntentUtc = PrivateStore.Stamp(),
            Before = before.Select(Fingerprint).ToArray() };
    }

    private static string WithoutKey(string value) { return value.Substring(value.IndexOf(':') + 1); }

    private static int AnchoredTail(string[] before, string[] current)
    {
        var matches = Enumerable.Range(0, current.Length).Where(i => current[i] == before.Last()).ToArray();
        if (matches.Length != 1) return -1;
        int index = matches[0], overlap = Math.Min(index + 1, before.Length);
        if (overlap < Math.Min(2, before.Length)) return -1;
        return current.Skip(index + 1 - overlap).Take(overlap).SequenceEqual(before.Skip(before.Length - overlap)) ? index : -1;
    }

    private static int RefreshedTail(string[] before, string[] current)
    {
        var original = before.Select(WithoutKey).ToArray();
        var refreshed = current.Select(WithoutKey).ToArray();
        for (int n = Math.Min(original.Length, 8); n >= 4; n--)
        {
            var suffix = original.Skip(original.Length - n).ToArray();
            if (suffix.Any(value => value.EndsWith(":?")) || suffix.Distinct().Count() < 4) continue;
            if (CountSequence(original, suffix).Length != 1) continue;
            var positions = CountSequence(refreshed, suffix);
            if (positions.Length == 1) return positions[0] + n - 1;
        }
        return -1;
    }

    private static int[] CountSequence(string[] rows, string[] sequence)
    {
        return Enumerable.Range(0, Math.Max(0, rows.Length - sequence.Length + 1))
            .Where(i => rows.Skip(i).Take(sequence.Length).SequenceEqual(sequence)).ToArray();
    }

    internal static bool Match(PrivateEvent item, ReplyRow[] current, DateTimeOffset now, bool allowRefresh, out string reason)
    {
        reason = "没有可比对的提交证据";
        var before = item.Submission == null ? null : item.Submission.Before;
        DateTimeOffset intent, observed;
        if (before == null || before.Length == 0 || String.IsNullOrEmpty(item.Reply) ||
            !DateTimeOffset.TryParse(item.Submission.IntentUtc, out intent) || intent > now ||
            !DateTimeOffset.TryParse(item.ObservedUtc, out observed) || observed > intent ||
            current.Length == 0 || current.Select(row => row.Key).Distinct().Count() != current.Length) return false;
        var after = current.Select(Fingerprint).ToArray();
        var index = AnchoredTail(before, after);
        bool rebuilt = index < 0;
        if (rebuilt && allowRefresh && now - intent <= TimeSpan.FromMinutes(2)) index = RefreshedTail(before, after);
        if (index < 0) { reason = "当前列表没有足够且唯一的提交前尾部证据，可将原私聊滚到最新后重新核查"; return false; }
        var tag = MessageSnapshot.Normalize(item.Reply);
        var hits = current.Skip(index + 1).Where(row => row.Own == true && row.Text == tag).ToArray();
        if (hits.Length != 1 || hits.Any(hit => before.Any(prior => prior.StartsWith(MessageSnapshot.Hash(hit.Key) + ":", StringComparison.Ordinal))))
        {
            reason = hits.Length > 1 ? "发现多条相同的新出站气泡，无法唯一核对" : "尚未观察到唯一的新出站气泡";
            return false;
        }
        reason = rebuilt ? "refreshed-ordered-tail-and-new-outgoing" : "durable-tail-and-new-outgoing";
        return true;
    }

    internal static bool Find(PrivateStore store, IPrivateTransport wire, PrivateEvent item, CancellationToken cancel,
        int timeout, Action<string> progress, out string reason)
    {
        var proof = store.State.Binding;
        reason = "提交时的窗口代际或绑定已变化，请用对端收件证据核对";
        if (proof == null || item.BotQq != proof.BotQq || item.PeerQq != proof.PeerQq || proof.Epoch != wire.Epoch ||
            (item.BindingVersion != null && item.BindingVersion != proof.RouteStamp()) ||
            (item.Submission != null && item.Submission.Epoch != wire.Epoch)) return false;

        var observer = wire as ICheckedPrivateTransport;
        var watch = Stopwatch.StartNew();
        int scans = 0;
        do
        {
            cancel.ThrowIfCancellationRequested();
            progress("正在核查出站气泡，第 " + (++scans) + " 次 · 已用 " + watch.ElapsedMilliseconds / 1000 + " 秒");
            try
            {
                bool empty;
                var current = observer == null ? wire.Read() : observer.ReadAfterSubmit(out empty);
                if (item.Submission != null)
                {
                    if (Match(item, current, DateTimeOffset.UtcNow, !String.IsNullOrEmpty(proof.ConversationProof), out reason)) return true;
                }
                else if (LegacyTagMatches(store, wire, item, current)) { reason = "legacy-unique-tag-and-incoming"; return true; }
                else { reason = "旧版任务没有保存发送前证据，且没有可核对的专用标记。请查看对方收件"; return false; }
            }
            catch (InvalidOperationException) { reason = "QQ 界面暂不可读或当前窗口身份不符，请打开原私聊后再次核查"; }
        } while (watch.ElapsedMilliseconds < timeout && scans < 12 && !cancel.WaitHandle.WaitOne(260));
        cancel.ThrowIfCancellationRequested();
        return false;
    }

    private static bool LegacyTagMatches(PrivateStore store, IPrivateTransport wire, PrivateEvent item, ReplyRow[] rows)
    {
        return !String.IsNullOrEmpty(item.RecoveryTag) && store.State.SnapshotEpoch == wire.Epoch &&
            store.State.Snapshot.Intersect(PrivateStore.Observe(rows)).Any() &&
            rows.Any(row => row.Own == false && row.Key == item.RuntimeKey && row.Digest == item.Digest) &&
            rows.Count(row => row.Own == true && row.Text == MessageSnapshot.Normalize(item.Reply)) == 1;
    }
}
