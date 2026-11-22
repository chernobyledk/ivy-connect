using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;

internal static class LabEntry
{
    internal const string InstanceName = @"Local\QQPrivateApp.Phase3.Control";

    [STAThread]
    private static int Main()
    {
#if NET10_0_OR_GREATER
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#else
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.SystemDefault;
#endif
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var lease = new Mutex(false, InstanceName))
        {
            bool entered;
            try { entered = lease.WaitOne(0); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered)
            {
                MessageBox.Show("Ivy connect 的另一实例已经打开。请从主窗或托盘退出已有实例，再打开此版本。",
                    "Ivy connect · 已在使用", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 2;
            }
            try
            {
                var folder = LabData.Profile;
                using (var store = new PrivateStore(folder)) LabData.BackupBeforeFirstRun(folder);
                Application.Run(new PrivateCoreForm(folder));
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法安全打开或备份本机数据。请确认没有旧版任务运行，并检查数据目录的剩余空间和权限。\r\n" +
                    "故障类型 " + ex.GetType().Name, "Ivy connect · 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            finally { lease.ReleaseMutex(); }
        }
    }
}

internal static class LabData
{
    internal static string Profile
    {
        get { return PrivateProfile.Folder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "phase3"); }
    }

    private static readonly string[] ProtectedFiles = {
        "model-settings.dpapi", "model-profiles.dpapi", "private-state.dpapi", "qq-cover.user.png"
    };

    internal static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        using (Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })) { }
    }

    internal static void BackupBeforeFirstRun(string folder)
    {
        var parent = Directory.GetParent(folder).FullName;
        var completed = Path.Combine(parent, "备份", "首次使用-v1.0.0");
        if (File.Exists(Path.Combine(completed, "备份完成.txt"))) return;
        if (!Array.Exists(ProtectedFiles, file => File.Exists(Path.Combine(folder, file)))) return;
        if (Directory.Exists(completed)) throw new IOException("Incomplete backup requires review");
        WriteBackup(folder, completed);
    }

    internal static string BackupNow(string folder)
    {
        var parent = Directory.GetParent(folder).FullName;
        var target = Path.Combine(parent, "备份", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        using (var store = new PrivateStore(folder)) WriteBackup(folder, target);
        return target;
    }

    private static void WriteBackup(string folder, string destination)
    {
        var scratch = destination + ".partial-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(scratch);
        foreach (var file in ProtectedFiles)
        {
            var input = Path.Combine(folder, file);
            if (!File.Exists(input)) continue;
            var output = Path.Combine(scratch, file);
            File.Copy(input, output, false);
            if (Fingerprint(input) != Fingerprint(output)) throw new IOException("Backup verification mismatch");
        }
        File.WriteAllText(Path.Combine(scratch, "备份完成.txt"), DateTimeOffset.Now.ToString("O") +
            "\r\nIvy connect 本机加密数据副本。恢复前请退出两个版本，优先保留最新的 QQ 消息账本。");
        Directory.Move(scratch, destination);
    }

    private static string Fingerprint(string file)
    {
        using (var digest = SHA256.Create())
        using (var stream = File.OpenRead(file))
            return Convert.ToBase64String(digest.ComputeHash(stream));
    }
}
