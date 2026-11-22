using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

internal sealed class SelfTestProtocol
{
    private readonly string id;
    private bool enabled = true;
    private readonly HashSet<string> armed = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<int> issued = new HashSet<int>();
    internal SelfTestProtocol(string id) { this.id = id; }
    internal string Input(int n)
    {
        return "TEST_IN " + id + " " + n.ToString("D2") + " 中文自测" + (n % 2 == 0 ? "\n输入第二行" : "");
    }
    internal string Output(int n)
    {
        return "TEST_OUT " + id + " " + n.ToString("D2") + " 已收到\n自动回执";
    }
    internal void Arm(int n)
    {
        if (!enabled || !issued.Add(n))
            throw new InvalidOperationException("自测入站编号重复或模式已关闭");
        armed.Add(MessageSnapshot.Normalize(Input(n)));
    }
    internal bool Consume(ReplyRow row)
    {
        return enabled && row.Own == true && armed.Remove(row.Text);
    }
    internal void Disable() { enabled = false; armed.Clear(); }
}

internal sealed class SelfTestHalt : InvalidOperationException
{
    internal SelfTestHalt(string message) : base(message) { }
}

internal sealed class SelfTestIdentity
{
    private static readonly DateTime BootStamp = new DateTime(2026, 11, 17, 9, 0, 46, DateTimeKind.Utc).AddTicks(4376318);
    private static readonly string[] Proofs = new[] {
        "窗口测试 基准-01", "小窗复测 R2-A-0c365ca9", "小窗复测 R2-ALT-F-20f252c6"
    }.Select(MessageSnapshot.Normalize).ToArray();
    internal readonly Binding Scope;
    internal SelfTestIdentity(Target target)
    {
        // This local attestation is tied to the account-number comparison the operator
        // already performed in this same live QQ process and authenticated self chat.
        if (target.Hwnd.ToInt64() != 397068 || target.Pid != 33440 || Target.Session != 1 ||
            target.Start.ToUniversalTime() != BootStamp)
            throw new InvalidOperationException("此前按 QQ 号核实的登录会话已变化，需要重新核对本机登录 QQ 号与自聊对方 QQ 号");
        Scope = new Binding(target, true);
        Validate(Scope.RequireMatch().ReplyRows());
    }
    internal void Validate(ReplyRow[] rows)
    {
        if (Proofs.Any(proof => !rows.Any(row => row.Text.IndexOf(proof, StringComparison.Ordinal) >= 0)))
            throw new SelfTestHalt("既有已核实自聊的三个独立编号未全部匹配，停止发送");
    }

    internal bool ClearUnsubmittedDraft(string expected, Action<string> log)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                var ui = Scope.RequireMatch();
                var rows = ui.ReplyRows();
                Validate(rows);
                var appeared = rows.Any(row => row.Text.Contains(MessageSnapshot.Normalize(expected)));
                var exact = ui.Draft() == expected;
                log("draft recovery audit: message-present=" + appeared + " draft-exact=" + exact +
                    " empty=" + ui.EmptyDraft());
                if (appeared || !exact) return false;
                var mapped = MsaaProbe.FindMappedEditor(Scope.Target, ui);
                if (mapped == null || ui.Draft() != expected) return false;
                var hr = MsaaProbe.PutValue(mapped, "");
                Thread.Sleep(260);
                var after = Scope.RequireMatch();
                var empty = after.EmptyDraft();
                log("draft recovery tool-only MSAA clear hr=0x" + hr.ToString("X8") + " empty=" + empty);
                return empty;
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                if (i == 3) throw;
                Thread.Sleep(200);
            }
        }
    }
}

internal sealed class SelfTestBudget
{
    internal const int Cap = 20;
    private readonly string path;
    internal SelfTestBudget(string path) { this.path = path; }
    internal int Used { get { return File.Exists(path) ? File.ReadAllLines(path).Length : 0; } }
    internal bool WasReserved(string marker)
    {
        var suffix = " " + marker.Replace("\n", "\\n");
        return File.Exists(path) && File.ReadLines(path).Any(line => line.EndsWith(suffix, StringComparison.Ordinal));
    }
    internal void Reserve(string marker)
    {
        if (Used >= Cap) throw new InvalidOperationException("本轮发送调用预算已用完");
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream))
        {
            writer.WriteLine(DateTimeOffset.UtcNow.ToString("O") + " " + marker.Replace("\n", "\\n"));
            writer.Flush();
            stream.Flush(true);
        }
    }
}
