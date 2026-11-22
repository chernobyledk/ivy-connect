using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

public enum PrivatePhase
{
    Observed, Queued, Generated, Preparing, SubmitIntent, LocalConfirmed, Uncertain, Failed, Cancelled
}

public sealed class PrivateBindingProof
{
    public string BotQq, PeerQq, BotDisplay, PeerDisplay, HeaderProof, LoginProof, Epoch, ChallengeAnchor, VerifiedUtc;
    public long Window;
}

public sealed class PrivateEvent
{
    public string LocalId, BotQq, PeerQq, ObservedUtc, RuntimeKey, Digest, Body, Reply, RecoveryTag, Detail;
    public PrivatePhase Phase;
}

public sealed class PrivateModelTurn
{
    public string User, Assistant;
}

public sealed class PrivateState
{
    public int Schema = 1;
    public PrivateBindingProof Binding;
    public PrivateEvent[] Events = new PrivateEvent[0];
    public string[] Snapshot = new string[0];
    public string SnapshotEpoch;
    public PrivateModelTurn[] ModelTurns = new PrivateModelTurn[0];
}

internal sealed class PrivateStore : IDisposable
{
    private static readonly XmlSerializer Serializer = new XmlSerializer(typeof(PrivateState));
    private readonly string path;
    private readonly FileStream lease;
    internal readonly PrivateState State;

    internal PrivateStore(string folder)
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "private-state.dpapi");
        lease = File.Open(Path.Combine(folder, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            State = File.Exists(path) ? Load(path) : new PrivateState();
            if (State.Schema != 1 || State.Events == null || State.Snapshot == null)
                throw new InvalidDataException("消息状态版本或内容无效");
        }
        catch { lease.Dispose(); throw; }
    }

    private static PrivateState Load(string file)
    {
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
        try { using (var stream = new MemoryStream(clear)) return (PrivateState)Serializer.Deserialize(stream); }
        finally { Array.Clear(clear, 0, clear.Length); }
    }

    internal void Save()
    {
        byte[] clear;
        using (var buffer = new MemoryStream())
        {
            Serializer.Serialize(buffer, State);
            clear = buffer.ToArray();
            Array.Clear(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        var temporary = path + ".next";
        try
        {
            var bytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { Array.Clear(clear, 0, clear.Length); }
    }

    internal void Set(PrivateEvent item, PrivatePhase phase, string detail)
    {
        item.Phase = phase;
        item.Detail = detail;
        Save();
    }

    internal void ClearModelContext()
    {
        State.ModelTurns = new PrivateModelTurn[0];
        Save();
    }

    internal static string Stamp() { return DateTimeOffset.UtcNow.ToString("O"); }
    internal static string[] Observe(ReplyRow[] rows)
    {
        return rows.Select(row => MessageSnapshot.Hash(row.Key) + "/" + row.Digest +
            "/" + (row.Own.HasValue ? (row.Own.Value ? "OUT" : "IN") : "UNKNOWN")).ToArray();
    }
    public void Dispose() { lease.Dispose(); }
}
