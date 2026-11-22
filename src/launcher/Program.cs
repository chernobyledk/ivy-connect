using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class Launcher
{
    [STAThread]
    private static int Main()
    {
        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "当前版本");
        var program = Path.Combine(folder, "Ivy connect.exe");
        if (!File.Exists(program))
        {
            MessageBox.Show("找不到「当前版本\\Ivy connect.exe」。请解压整个 Ivy connect 发布包。", "Ivy connect · 启动器");
            return 2;
        }
        try
        {
            using (Process.Start(new ProcessStartInfo(program) { WorkingDirectory = folder, UseShellExecute = false })) { }
            return 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("Ivy connect 无法启动，请检查发布文件和 .NET Framework 4.8。", "Ivy connect · 启动器");
            return 1;
        }
    }
}
