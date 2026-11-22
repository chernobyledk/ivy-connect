using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class ReleaseLayoutChecks
{
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
#if NET10_0_OR_GREATER
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
#else
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
#endif
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var output = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(output);
            var fixture = Path.Combine(output, "visual-fixture-" + Guid.NewGuid().ToString("N"), "profile");
            Seed(fixture);
            bool prior = args.Length == 2 && args[1] == "prior";
            using (var control = new PrivateCoreForm(fixture))
            using (var settings = new ModelConfigForm(fixture, _ => { }, () => { }))
            {
                control.Show();
                Application.DoEvents();
                Capture(control, Path.Combine(output, prior ? "01_稳定版主界面.png" : "03_实验版主界面.png"));
                settings.Show(control);
                Application.DoEvents();
                Capture(settings, Path.Combine(output, prior ? "02_稳定版模型设置.png" : "04_实验版模型设置.png"));
                if (!prior)
                {
                    var tabs = All(settings).OfType<TabControl>().Single();
                    Assert(tabs.TabPages.Count == 5 && tabs.TabPages[0].Text == "服务与模型", "service, key, network, generation and role tabs");
                    tabs.SelectedIndex = 2;
                    Application.DoEvents();
                    Capture(settings, Path.Combine(output, "05_实验版网络设置.png"));
                    settings.Hide();
                    VerifyLayouts(control, settings);
                    var prompt = All(control).OfType<BindingPromptPanel>().Single();
                    prompt.ShowCode(new NumericChallenge("57391"), "演示机器人", "100000001", "演示对象", "100000002");
                    typeof(PrivateCoreForm).GetField("dashboardScroll", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(control).AsPanel().ScrollControlIntoView(prompt);
                    Application.DoEvents();
                    Assert(All(prompt).OfType<Label>().Any(l => l.Text.Contains("演示对象") && l.Text.Contains("演示机器人")),
                        "code panel names both sending and receiving accounts");
                    Capture(control, Path.Combine(output, "06_实验版验证码.png"));
                    prompt.End();
                    var tray = (NotifyIcon)typeof(PrivateCoreForm).GetField("tray", BindingFlags.NonPublic | BindingFlags.Instance)
                        .GetValue(control);
                    control.WindowState = FormWindowState.Minimized;
                    Application.DoEvents();
                    Assert(tray.Visible && !control.Visible, "minimized control hands off to the system tray");
                    typeof(PrivateCoreForm).GetMethod("ShowControl", BindingFlags.NonPublic | BindingFlags.Instance)
                        .Invoke(control, null);
                    Application.DoEvents();
                    Assert(control.Visible && control.WindowState == FormWindowState.Normal && !tray.Visible,
                        "the tray restore action opens the original window");
                }
            }
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("FAIL " + ex.GetType().Name + ": " + ex.Message); return 1; }
    }

    private static Panel AsPanel(this object target) { return (Panel)target; }

    private static void Seed(string folder)
    {
        using (var store = new PrivateStore(folder))
            store.ReplaceBinding(new PrivateBindingProof { BotQq = "100000001", PeerQq = "100000002",
                BotDisplay = "演示机器人", PeerDisplay = "演示对象", Version = "visual-only" });
        var sample = PrivateModelProfile.New("演示配置", new PrivateModelSettings {
            ApiKey = "fixture-only-key-not-an-api-credential", RoleName = "演示角色", ContextTurns = 3
        });
        var catalog = new PrivateModelCatalog { Profiles = new[] { sample }, ActiveId = sample.Id };
        ModelCatalogFile.Save(folder, catalog);
    }

    private static void VerifyLayouts(Form main, Form settings)
    {
        var buttons = All(main).OfType<Button>().ToArray();
        var stop = buttons.Single(b => b.Text == "停止");
        var go = buttons.Single(b => b.Text == "开始回复");
        var model = buttons.Single(b => b.Text == "模型与密钥");
        var dpi = main.DeviceDpi;
        foreach (var scale in new[] { 1f, 1.25f, 1.5f })
        {
            main.ClientSize = new Size((int)(780 * scale), (int)(590 * scale));
            main.PerformLayout();
            Application.DoEvents();
            Assert(Inside(main, stop) && Inside(main, go) && Inside(main, model),
                "key buttons in viewport at candidate " + scale * 100 + "% viewport; live DPI=" + dpi);
        }
        main.ActiveControl = null;
        var visited = new HashSet<Control>();
        Control current = null;
        for (int i = 0; i < 45; i++)
        {
            if (!main.SelectNextControl(current, true, true, true, true)) break;
            current = ActiveLeaf(main);
            if (current != null) visited.Add(current);
        }
        Assert(visited.Contains(model) && visited.Contains(go) &&
            visited.OfType<TextBox>().Count() >= 4, "keyboard Tab reaches settings, account inputs and start");
        Console.WriteLine("INFO physical monitor scale=" + (dpi * 100 / 96) +
            "%; other candidate viewport sizes do not change Windows DPI");
    }

    private static bool Inside(Form main, Control child)
    {
        var box = main.RectangleToClient(child.RectangleToScreen(child.ClientRectangle));
        return main.ClientRectangle.Contains(box) && box.Width > 0 && box.Height > 0;
    }

    private static Control ActiveLeaf(ContainerControl owner)
    {
        var current = owner.ActiveControl;
        while (current is ContainerControl && ((ContainerControl)current).ActiveControl != null)
            current = ((ContainerControl)current).ActiveControl;
        return current;
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in All(child)) yield return nested;
        }
    }

    private static void Capture(Form form, string output)
    {
        using (var picture = new Bitmap(form.Width, form.Height))
        using (var target = Graphics.FromImage(picture))
        {
            var dc = target.GetHdc();
            bool ok;
            try { ok = PrintWindow(form.Handle, dc, 2); }
            finally { target.ReleaseHdc(dc); }
            if (!ok) form.DrawToBitmap(picture, new Rectangle(Point.Empty, picture.Size));
            picture.Save(output, ImageFormat.Png);
        }
    }

    private static void Assert(bool success, string name)
    { if (!success) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
}
