using System;
using System.Collections.Generic;
using System.Globalization;
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
    private readonly string[] activeProofs;
    private readonly string activeOwnDigest;
    private const string OwnDigestPrefix = "own-sha256:";
    private static string AttestationFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "runs", "local-self-binding.tsv"); } }
    private static string Fingerprint(Target target)
    {
        return String.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}", target.Hwnd.ToInt64(), target.Pid,
            Target.Session, target.Start.ToUniversalTime().Ticks);
    }

    internal static Target OpenTarget()
    {
        if (!File.Exists(AttestationFile)) return Target.FromKnownHandle(397068);
        var fields = File.ReadAllText(AttestationFile).TrimEnd().Split('\t');
        long hwnd;
        if (fields.Length != 6 || !Int64.TryParse(fields[0], out hwnd))
            throw new SelfTestHalt("自聊绑定记录无效，应重新按 QQ 号核对");
        return Target.FromKnownHandle(hwnd);
    }

    internal static SelfTestIdentity Read(Target target)
    {
        for (int i = 0; ; i++)
        {
            try { return new SelfTestIdentity(target); }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                if (i == 5) throw;
                Thread.Sleep(350);
            }
        }
    }

    internal static int BindOperatorVerified(Target target, string manualMarker)
    {
        if (!Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd) || !Native.IsWindowEnabled(target.Hwnd))
            throw new SelfTestHalt("请保持刚按 QQ 号核实的本人会话展开");
        if (manualMarker != null && !IsManualProof(manualMarker)) return 2;
        var scope = new Binding(target, true);
        var ui = scope.RequireMatch();
        var proofs = manualMarker == null ? Proofs : new[] { MessageSnapshot.Normalize(manualMarker) };
        ValidateProofs(ui.ReplyRows(), proofs);
        if (manualMarker != null && ui.Messages(manualMarker).MarkerRows(manualMarker) != 1)
            throw new SelfTestHalt("用户刚发送的 A 编号未能唯一回查，未绑定");
        if (!target.SameWindow())
            throw new SelfTestHalt("自聊核对期间窗口发生变化");
        SaveAttestation(target, scope, manualMarker ?? "prior");
        Console.WriteLine("operator-verified self chat bound; " +
            (manualMarker == null ? "historical proofs matched" : "manual A local message count=1") + "; hwnd=" + target.Hwnd);
        return 0;
    }

    internal static int RefreshConfirmedForeground(Target target)
    {
        // The operator must have just compared both QQ numbers and left that chat in front.
        CheckProcess(target);
        if (Native.GetForegroundWindow() != target.Hwnd || !Native.Visible(target.Hwnd) ||
            Native.IsIconic(target.Hwnd) || !Native.IsWindowEnabled(target.Hwnd))
            throw new SelfTestHalt("请把刚按 QQ 号核实的本人会话留在前台");

        var scope = new Binding(target, true);
        var ui = scope.RequireMatch();
        if (ui.HasVisibleConnectionWarning() || !ui.EmptyDraft())
            throw new SelfTestHalt("QQ 提示断开或草稿未空，暂停刷新本人绑定");
        var recent = ui.ReplyRows();
        var anchor = recent.LastOrDefault(row => row.Own == true && row.Text.Length > 0);
        if (anchor == null) throw new SelfTestHalt("当前列表没有可回读的己方气泡，无法无发信刷新绑定");

        Thread.Sleep(260);
        ui = scope.RequireMatch();
        if (Native.GetForegroundWindow() != target.Hwnd ||
            !ui.EmptyDraft() || !ui.ReplyRows().Any(row => row.Own == true && row.Anchor == anchor.Anchor))
            throw new SelfTestHalt("自聊核对期间窗口或气泡发生变化");
        SaveAttestation(target, scope, OwnDigestPrefix + anchor.Digest);
        Console.WriteLine("operator-confirmed self chat refreshed; prior QQ process and foreground matched; own bubble digest saved; no send");
        return 0;
    }

    private static void SaveAttestation(Target target, Binding scope, string proof)
    {
        var path = AttestationFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(staged, Fingerprint(target) + "\t" + scope.HeaderProof + "\t" + proof);
        try { if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path); }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }

    internal SelfTestIdentity(Target target)
    {
        // This local attestation is tied to the account-number comparison the operator
        // already performed in this same live QQ process and authenticated self chat.
        var attestation = CheckProcess(target);
        Scope = new Binding(target, true);
        activeProofs = Proofs;
        if (attestation != null)
        {
            var fields = attestation.Split('\t');
            if (fields.Length != 6 || fields[4] != Scope.HeaderProof)
                throw new SelfTestHalt("当前会话头与已核实的本人会话不符");
            if (IsManualProof(fields[5])) activeProofs = new[] { MessageSnapshot.Normalize(fields[5]) };
            else if (System.Text.RegularExpressions.Regex.IsMatch(fields[5], @"\Aown-sha256:[A-Za-z0-9+/]{43}=\z"))
                activeOwnDigest = fields[5].Substring(OwnDigestPrefix.Length);
            else if (fields[5] != "prior") throw new SelfTestHalt("当前会话的测试锚点格式无效");
        }
        Validate(Scope.RequireMatch().ReplyRows());
    }

    internal static string CheckProcess(Target target)
    {
        var attestation = File.Exists(AttestationFile) ? File.ReadAllText(AttestationFile).TrimEnd() : null;
        if (attestation == null)
        {
            if (target.Hwnd.ToInt64() != 397068 || target.Pid != 33440 || Target.Session != 1 ||
                target.Start.ToUniversalTime() != BootStamp)
                throw new SelfTestHalt("此前按 QQ 号核实的登录会话已变化，需要重新核对本机登录 QQ 号与自聊对方 QQ 号");
        }
        else if (!attestation.StartsWith(Fingerprint(target) + "\t", StringComparison.Ordinal))
            throw new SelfTestHalt("登录进程或窗口已变化，应重新按 QQ 号核对");
        return attestation;
    }
    private static bool IsManualProof(string text)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"\AR5-A-[0-9A-F]{4,8}\z");
    }

    internal void Validate(ReplyRow[] rows)
    {
        if (activeOwnDigest == null) ValidateProofs(rows, activeProofs);
        else if (!rows.Any(row => row.Own == true && row.Digest == activeOwnDigest))
            throw new SelfTestHalt("已核实本人会话的己方气泡锚点失效，停止发送");
    }

    private static void ValidateProofs(ReplyRow[] rows, string[] proofs)
    {
        if (proofs.Any(proof => !rows.Any(row => row.Text.IndexOf(proof, StringComparison.Ordinal) >= 0)))
            throw new SelfTestHalt("已核实自聊的校验编号无法回查，停止发送");
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
