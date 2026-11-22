using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
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
