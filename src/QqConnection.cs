using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class PrivateProfile
{
    internal static string Folder(string root, string phase) { return Path.Combine(root, "QQPrivateApp", phase); }

    internal static void Migrate(string destination)
    {
        if (!String.Equals(Path.GetFileName(destination), "phase3", StringComparison.OrdinalIgnoreCase)) return;
        var previous = Path.Combine(Path.GetDirectoryName(destination), "phase2");
        if (!Directory.Exists(previous)) return;
        var files = new[] { "private-state.dpapi", "model-settings.dpapi" };
        if (files.All(file => !File.Exists(Path.Combine(previous, file)) || File.Exists(Path.Combine(destination, file)))) return;
        using (var mutex = new Mutex(false, @"Local\QQPrivateApp.Phase3Profile"))
        {
            bool owned;
            try { owned = mutex.WaitOne(2500); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new InvalidOperationException("本机资料正在被另一个窗口复制");
            try
            {
                using (var source = new PrivateStore(previous))
                {
                    Directory.CreateDirectory(destination);
                    foreach (var file in files)
                    {
                        var from = Path.Combine(previous, file);
                        var to = Path.Combine(destination, file);
                        if (!File.Exists(from) || File.Exists(to)) continue;
                        var pending = to + ".migration";
                        File.Copy(from, pending, true);
                        File.Move(pending, to);
                    }
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}

internal enum QqConnectState
{
    NotConnected, NeedsLogin, NeedsChat, NeedsVerification, ChooseWindow, Hidden, Minimized,
    Dialog, Reconnecting, Draft, Unreadable, Ready
}

internal sealed class QqCandidate
{
    internal Target Target;
    internal long Window;
    internal string Login, Chat, HeaderProof, LoginProof, ConversationProof, Epoch;
    internal bool Visible, Minimized, Enabled, LoginPrompt, ReadFailed, EditorReady, HasDraft, NetworkWarning, AnchorMatches;

    internal bool CanVerify(string botDisplay, string chatDisplay)
    {
        return Visible && !Minimized && Enabled && EditorReady && !NetworkWarning && !HasDraft &&
            !String.IsNullOrWhiteSpace(Login) && Login == botDisplay && Chat == chatDisplay;
    }

    public override string ToString()
    {
        var state = Minimized ? "已最小化" : !Visible ? "托盘隐藏" : !Enabled ? "有待处理的对话框" :
            LoginPrompt ? "等待登录" : ReadFailed ? "暂时无法读取" : "已展开";
        var kind = Target == null ? "QQ" : Target.Title.Length > 36 ? Target.Title.Substring(0, 36) + "…" : Target.Title;
        return kind + " ｜ 登录显示名 " + (Login ?? "未识别") + " ｜ 当前会话 " + (Chat ?? "未识别") + " ｜ " + state;
    }
}

internal sealed class QqConnectionResult
{
    internal readonly QqConnectState State;
    internal readonly string Detail;
    internal readonly QqCandidate[] Candidates;
    internal readonly PrivateBindingProof Binding;

    internal QqConnectionResult(QqConnectState state, string detail, QqCandidate[] candidates, PrivateBindingProof binding = null)
    {
        State = state; Detail = detail; Candidates = candidates; Binding = binding;
    }
}

internal static class QqConnection
{
    internal static bool IsRunning()
    {
        var processes = Process.GetProcessesByName("QQ");
        try { return processes.Any(p => p.SessionId == Target.Session); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static bool LaunchIfNotRunning()
    {
        if (IsRunning()) return false;
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tencent", "QQNT", "QQ.exe");
        if (!File.Exists(exe)) throw new InvalidOperationException("未找到已核对的 QQNT 安装位置，请手动启动官方 QQ");
        var company = FileVersionInfo.GetVersionInfo(exe).CompanyName ?? "";
        if (company.IndexOf("Tencent", StringComparison.OrdinalIgnoreCase) < 0 && !company.Contains("腾讯"))
            throw new InvalidOperationException("安装文件供应商信息无法核验，请手动启动官方 QQ");
        using (var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }))
            return process != null;
    }

    internal static Target Pinned(PrivateBindingProof proof)
    {
        if (proof == null) throw new InvalidOperationException("没有已核验绑定，请先核对 QQ 资料卡");
        if (!Native.IsWindow(new IntPtr(proof.Window))) throw new PrivateRouteLostException("保存的 QQ 窗口已关闭，请重新连接");
        var target = Target.FromKnownHandle(proof.Window);
        if (QqPrivateTransport.EpochOf(target) != proof.Epoch)
            throw new PrivateRouteLostException("已保存 QQ 窗口已换代，请打开原私聊并重新核验");
        return target;
    }

    internal static long VerifyUnique(PrivateBindingProof proof, Action guard, Action<string> log)
    {
        var target = Pinned(proof);
        var matches = Target.List().Where(candidate => QqPrivateTransport.EpochOf(candidate) == proof.Epoch).ToArray();
        if (matches.Length != 1 || matches[0].Hwnd != target.Hwnd)
            throw new PrivateRouteLostException("没有唯一的已保存展开窗口，请打开机器人 QQ 和已核验私聊");
        using (var wire = new QqPrivateTransport(proof, guard, log)) wire.RequireReady();
        return target.Hwnd.ToInt64();
    }

    internal static QqCandidate[] Collect(PrivateBindingProof prior, Action guard, Action<string> log)
    {
        var targets = Target.List(true).Where(t => Native.Visible(t.Hwnd) ||
            (prior != null && t.Hwnd.ToInt64() == prior.Window)).ToArray();
        if (prior != null)
        {
            var old = new IntPtr(prior.Window);
            log("saved hwnd=" + prior.Window + " exists=" + Native.IsWindow(old) + " pid=" + Native.Pid(old) +
                " visible=" + Native.Visible(old) + " minimized=" + Native.IsIconic(old) +
                " enumerated=" + targets.Any(t => t.Hwnd == old));
        }
        var candidates = targets.Select(t => Observe(t, prior, guard)).ToArray();
        foreach (var row in candidates)
            log("candidate hwnd=" + row.Target.Hwnd.ToInt64() + " pid=" + row.Target.Pid + " visible=" + row.Visible +
                " minimized=" + row.Minimized + " enabled=" + row.Enabled + " login-known=" + (row.Login != null) +
                " bot-ui-match=" + (prior != null && row.Login == prior.BotDisplay) +
                " peer-header-match=" + (prior != null && row.Chat == prior.PeerDisplay) +
                " session-proof=" + (prior != null && ExactProof(prior, row)) + " readable=" + !row.ReadFailed);
        return candidates;
    }

    private static QqCandidate Observe(Target target, PrivateBindingProof prior, Action guard)
    {
        var row = new QqCandidate { Target = target, Window = target.Hwnd.ToInt64(), Epoch = QqPrivateTransport.EpochOf(target),
            Visible = Native.Visible(target.Hwnd), Minimized = Native.IsIconic(target.Hwnd), Enabled = Native.IsWindowEnabled(target.Hwnd) };
        if (!row.Visible || row.Minimized || !row.Enabled) return row;
        for (int retry = 0; ; retry++)
        {
            guard();
            try
            {
                var ui = Controls.Scan(target, true);
                row.AnchorMatches = false;
                row.HeaderProof = row.LoginProof = row.ConversationProof = null;
                row.Login = ui.ConnectionLoginDisplay();
                row.Chat = ui.HeaderIdentity();
                row.LoginPrompt = row.Login == null && row.Chat == null && ui.ShowsLoginPrompt();
                row.EditorReady = ui.HasMessageArea && ui.Editor != null && ui.Editor.Current.IsEnabled && ui.Button != null;
                row.NetworkWarning = ui.HasVisibleConnectionWarning();
                if (prior != null && SameProcess(prior.Epoch, row.Epoch) && row.Chat == prior.PeerDisplay)
                {
                    row.HeaderProof = ui.ConnectionHeaderProof();
                    row.LoginProof = ui.LoginEvidence(prior.BotDisplay);
                    if (row.Login == null && row.LoginProof != null && row.LoginProof == prior.LoginProof)
                        row.Login = prior.BotDisplay; // prior run-time avatar evidence is stronger than a display label
                    if (row.HeaderProof == prior.HeaderProof && row.LoginProof == prior.LoginProof && !String.IsNullOrEmpty(prior.ChallengeAnchor))
                    {
                        row.ConversationProof = ui.SelectedConversationProof();
                        if (String.IsNullOrEmpty(prior.ConversationProof))
                            row.AnchorMatches = ui.ReplyRows().Count(r => r.Own == false && MessageSnapshot.Hash(r.Anchor) == prior.ChallengeAnchor) == 1;
                    }
                }
                row.HasDraft = row.EditorReady && prior != null && row.Login == prior.BotDisplay &&
                    row.Chat == prior.PeerDisplay && !ui.EmptyDraft();
                return row;
            }
            catch (ElementNotAvailableException) { }
            catch (COMException) { }
            catch (InvalidOperationException) { }
            if (retry == 1) { row.ReadFailed = true; return row; }
            Thread.Sleep(160);
        }
    }

    private static bool SameProcess(string oldEpoch, string currentEpoch)
    {
        if (String.IsNullOrEmpty(oldEpoch) || String.IsNullOrEmpty(currentEpoch)) return false;
        int a = oldEpoch.LastIndexOf('/'), b = currentEpoch.LastIndexOf('/');
        return a > 0 && b > 0 && String.Equals(oldEpoch.Substring(0, a), currentEpoch.Substring(0, b), StringComparison.Ordinal);
    }

    private static bool ExactProof(PrivateBindingProof prior, QqCandidate row)
    {
        return !row.ReadFailed && row.Visible && !row.Minimized && row.Enabled &&
            SameProcess(prior.Epoch, row.Epoch) && row.Login == prior.BotDisplay && row.Chat == prior.PeerDisplay &&
            !String.IsNullOrEmpty(prior.LoginProof) && row.LoginProof == prior.LoginProof &&
            !String.IsNullOrEmpty(prior.HeaderProof) && row.HeaderProof == prior.HeaderProof &&
            (String.IsNullOrEmpty(prior.ConversationProof) ? row.AnchorMatches : row.ConversationProof == prior.ConversationProof);
    }

    internal static QqConnectionResult Resolve(PrivateBindingProof prior, QqCandidate[] rows, bool receiptOnly = false)
    {
        if (rows.Length == 0) return new QqConnectionResult(QqConnectState.NotConnected, "未发现 QQ 窗口。请打开 QQ 并登录。", rows);
        if (prior == null)
        {
            if (rows.Any(row => row.LoginPrompt) && !rows.Any(row => row.Login != null))
                return new QqConnectionResult(QqConnectState.NeedsLogin, "请先登录机器人 QQ。", rows);
            return new QqConnectionResult(QqConnectState.NeedsVerification,
                "尚无核验记录。填写双方 QQ 号及显示名，在 QQ 打开原私聊，选择窗口并核验。", rows);
        }
        var proven = rows.Where(row => ExactProof(prior, row)).ToArray();
        if (proven.Length > 1) return new QqConnectionResult(QqConnectState.ChooseWindow, "有多个窗口呈现相同核验证据，请选择并重新核验。", rows);
        if (proven.Length == 1)
        {
            var match = proven[0];
            if (match.NetworkWarning) return new QqConnectionResult(QqConnectState.Reconnecting, "QQ 显示断线或正在重连。网络恢复后再重试。", rows);
            if (!receiptOnly && !match.EditorReady) return new QqConnectionResult(QqConnectState.Unreadable, "已识别原私聊，输入框或发送按钮暂不可用。", rows);
            if (!receiptOnly && match.HasDraft) return new QqConnectionResult(QqConnectState.Draft, "原私聊内有草稿。请先由本人处理草稿，再开始回复。", rows);
            var hwnd = match.Window;
            var sameWindow = prior.Window == hwnd && prior.Epoch == match.Epoch;
            if (sameWindow && (receiptOnly || prior.ConversationProof == match.ConversationProof))
                return new QqConnectionResult(QqConnectState.Ready, "已找到并核验原私聊。", rows, prior);
            return new QqConnectionResult(QqConnectState.Ready, "已重新定位原私聊。", rows, new PrivateBindingProof {
                BotQq = prior.BotQq, PeerQq = prior.PeerQq, BotDisplay = prior.BotDisplay, PeerDisplay = prior.PeerDisplay,
                Window = hwnd, Epoch = match.Epoch, LoginProof = prior.LoginProof, HeaderProof = prior.HeaderProof,
                ChallengeAnchor = prior.ChallengeAnchor, ConversationProof = match.ConversationProof,
                VerifiedUtc = prior.VerifiedUtc, Version = sameWindow ? prior.Version : Guid.NewGuid().ToString("N") });
        }

        var sameChat = rows.Where(row => row.Visible && !row.Minimized && row.Enabled && !row.ReadFailed &&
            row.Login == prior.BotDisplay && row.Chat == prior.PeerDisplay).ToArray();
        if (sameChat.Length > 1) return new QqConnectionResult(QqConnectState.ChooseWindow,
            "有多个可能的私聊，显示名不足以核对身份。请选择正确窗口并重新核验。", rows);
        if (sameChat.Length == 1)
        {
            if (sameChat[0].NetworkWarning) return new QqConnectionResult(QqConnectState.Reconnecting, "QQ 显示断线或正在重连。", rows);
            if (sameChat[0].HasDraft) return new QqConnectionResult(QqConnectState.Draft, "请处理私聊中的草稿后再核验。", rows);
            return new QqConnectionResult(QqConnectState.NeedsVerification,
                "找到了显示名相符的私聊，但当前选中会话或旧验证证据不匹配。请核对双方资料卡并重新核验。", rows);
        }
        var saved = rows.FirstOrDefault(row => row.Epoch == prior.Epoch);
        if (saved != null && saved.Minimized) return new QqConnectionResult(QqConnectState.Minimized,
            "机器人 QQ 已最小化。请从任务栏恢复，或点击「显示 QQ」，再连接。", rows);
        if (saved != null && !saved.Visible) return new QqConnectionResult(QqConnectState.Hidden,
            "机器人 QQ 在系统托盘隐藏。请打开托盘中的机器人 QQ，或点击「显示 QQ」，再连接。", rows);
        if (saved != null && !saved.Enabled) return new QqConnectionResult(QqConnectState.Dialog,
            "机器人 QQ 有待处理的窗口。请完成 QQ 中的提示再连接。", rows);
        if (rows.Any(row => row.LoginPrompt) && !rows.Any(row => row.Login == prior.BotDisplay))
            return new QqConnectionResult(QqConnectState.NeedsLogin, "请登录保存的机器人 QQ，再打开目标私聊。", rows);
        if (saved != null && saved.ReadFailed) return new QqConnectionResult(QqConnectState.Unreadable,
            "原 QQ 窗口仍在，但无障碍控件暂不可读。请在 QQ 中打开目标私聊后重试。", rows);
        if (rows.Any(row => row.Login == prior.PeerDisplay && row.Chat == prior.BotDisplay) && !rows.Any(row => row.Login == prior.BotDisplay))
            return new QqConnectionResult(QqConnectState.NeedsChat, "目前看到的是对端登录的 QQ 界面。请切到机器人账号，在那里打开目标私聊。", rows);
        return new QqConnectionResult(QqConnectState.NeedsChat, "请在机器人账号的 QQ 主窗打开目标私聊。窗口重开后如提示核验，请核对双方账号。", rows);
    }

    internal static QqConnectionResult Connect(PrivateBindingProof prior, Action guard, Action<string> log)
    {
        var result = Resolve(prior, Collect(prior, guard, log));
        if (result.Binding != null)
        {
            using (var wire = new QqPrivateTransport(result.Binding, guard, log)) wire.RequireReady();
        }
        log("connect state=" + result.State + " candidates=" + result.Candidates.Length);
        return result;
    }
}

internal sealed class QqPlacement
{
    private Target originalTarget;
    private Rectangle original;
    private Native.Placement placement;

    internal static Rectangle RightSide(Rectangle outer, Rectangle work)
    {
        if (outer.Width <= 0 || outer.Height <= 0 || outer.Width > work.Width || outer.Height > work.Height)
            throw new InvalidOperationException("QQ 当前尺寸放不进此显示器工作区，请手动恢复常规大小");
        var y = Math.Min(Math.Max(outer.Top, work.Top), work.Bottom - outer.Height);
        return new Rectangle(work.Right - outer.Width, y, outer.Width, outer.Height);
    }

    internal void Side(Target target)
    {
        if (!target.SameWindow() || !Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd) || Native.IsZoomed(target.Hwnd))
            throw new InvalidOperationException("请先把 QQ 展开至常规窗口，然后再侧边摆放");
        if (originalTarget != null && originalTarget.Hwnd != target.Hwnd)
            throw new InvalidOperationException("请先恢复上一次摆放的 QQ 窗口");
        var bounds = Native.Bounds(target.Hwnd);
        var side = RightSide(bounds, Screen.FromHandle(target.Hwnd).WorkingArea);
        var saved = Native.GetPlacement(target.Hwnd);
        var flags = Native.NoActivate | Native.NoOwnerZOrder | 0x0001u; // SWP_NOSIZE
        if (!Native.SetWindowPos(target.Hwnd, new IntPtr(1), side.X, side.Y, 0, 0, flags))
            throw new InvalidOperationException("QQ 侧边摆放失败");
        if (originalTarget == null) { originalTarget = target; original = bounds; placement = saved; }
    }

    internal static void Show(Target target)
    {
        if (!target.SameWindow()) throw new PrivateRouteLostException("QQ 窗口已失效");
        if (!Native.Visible(target.Hwnd) || Native.IsIconic(target.Hwnd))
        {
            if (!Native.ShowWindowAsync(target.Hwnd, 4)) throw new InvalidOperationException("QQ 恢复展开失败");
        }
        if (!Native.SetWindowPos(target.Hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.NoActivate | Native.NoOwnerZOrder | 3u))
            throw new InvalidOperationException("QQ 无法显示到普通窗口前面");
    }

    internal bool Restore()
    {
        if (originalTarget == null) return false;
        var target = originalTarget;
        if (!target.SameWindow())
        {
            originalTarget = null;
            throw new PrivateRouteLostException("原 QQ 窗已换代，无法恢复其位置");
        }
        if (Native.IsIconic(target.Hwnd) || Native.IsZoomed(target.Hwnd) || !Native.Visible(target.Hwnd))
        {
            if (!Native.SetWindowPlacement(target.Hwnd, ref placement)) throw new InvalidOperationException("原窗口状态未能恢复");
        }
        if (!Native.SetWindowPos(target.Hwnd, IntPtr.Zero, original.X, original.Y, original.Width, original.Height,
            Native.NoActivate | Native.NoZOrder | Native.NoOwnerZOrder))
            throw new InvalidOperationException("原 QQ 窗口位置未能恢复");
        originalTarget = null;
        return true;
    }
}
