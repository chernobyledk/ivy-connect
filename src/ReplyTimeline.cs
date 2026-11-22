using System;
using System.Collections.Generic;
using System.Linq;

internal sealed class ReplyRow
{
    internal readonly string Key, Digest, Text;
    internal readonly int Segments;
    internal readonly bool? Own;
    internal ReplyRow(string key, string[] parts, bool? own = null)
    {
        Key = key;
        Segments = parts.Length;
        Own = own;
        Text = MessageSnapshot.Normalize(String.Concat(parts));
        Digest = MessageSnapshot.Hash(Text);
    }
    internal string Anchor { get { return Key + "/" + Digest; } }
}

internal sealed class ReplyTimeline
{
    private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
    private string[] prior;
    private static string[] Anchors(ReplyRow[] rows)
    {
        if (rows.Length == 0 || rows.Select(row => row.Key).Distinct().Count() != rows.Length)
            throw new InvalidOperationException("消息快照为空或气泡标识重复");
        return rows.Select(row => row.Anchor).ToArray();
    }
    internal void Seed(ReplyRow[] original, ReplyRow[] current)
    {
        var first = Anchors(original);
        var second = Anchors(current);
        if (!first.SequenceEqual(second)) throw new InvalidOperationException("启动历史尚未稳定");
        prior = second;
        foreach (var row in current) seen.Add(row.Key);
    }
    private int CheckTail(string[] current)
    {
        int tail = Array.IndexOf(current, prior[prior.Length - 1]);
        if (tail < 0) throw new InvalidOperationException("消息尾部锚点失效，暂停核对，避免回复历史");
        int overlap = Math.Min(tail + 1, prior.Length);
        if (overlap < Math.Min(2, prior.Length))
            throw new InvalidOperationException("消息快照重叠不足，暂停核对");
        for (int i = 0; i < overlap; i++)
            if (current[tail - i] != prior[prior.Length - 1 - i])
                throw new InvalidOperationException("消息顺序变化，暂停核对");
        return tail;
    }
    internal void CheckSameConversation(ReplyRow[] rows)
    {
        CheckTail(Anchors(rows));
    }
    internal ReplyRow[] Advance(ReplyRow[] rows)
    {
        var current = Anchors(rows);
        int tail = CheckTail(current);
        var appended = rows.Skip(tail + 1).ToArray();
        if (appended.Any(row => seen.Contains(row.Key)))
            throw new InvalidOperationException("新气泡复用了曾见标识，暂停核对");
        foreach (var row in appended) seen.Add(row.Key);
        prior = current;
        return appended;
    }
}
