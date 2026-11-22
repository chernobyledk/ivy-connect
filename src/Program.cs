using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        try
        {
            bool autoMatrix = arguments.Length == 2 && arguments[0] == "--matrix";
            bool baseline = arguments.Length == 2 && arguments[0] == "--round2-baseline";
            bool round2 = (arguments.Length == 2 || arguments.Length == 3 && arguments[2] == "--watch-f") && arguments[0] == "--round2";
            bool region = arguments.Length == 2 && arguments[0] == "--round2-region";
            bool finishRegion = arguments.Length == 3 && arguments[0] == "--round2-finish-clip";
            bool msaaWitness = (arguments.Length == 2 && (arguments[0] == "--round2-msaa" ||
                arguments[0] == "--round2-msaa-write" || arguments[0] == "--round2-msaa-default")) ||
                (arguments.Length == 3 && arguments[0] == "--round2-msaa-default");
            bool fullOcclusionWitness = arguments.Length == 2 && arguments[0] == "--round2-f-witness";
            bool awaitSelf = (arguments.Length == 2 || arguments.Length == 3 && arguments[2] == "--watch-f") &&
                arguments[0] == "--await-verified-self";
            bool typingPad = arguments.Length >= 5 && arguments.Length <= 7 && arguments[0] == "--typing-pad";
            bool selfPad = arguments.Length == 6 && arguments[0] == "--self-test-pad";
            bool selfTest = (arguments.Length == 1 || arguments.Length == 2 &&
                (arguments[1] == "--diagnostic" || arguments[1] == "--foreground-invoke-control" ||
                 arguments[1] == "--foreground-keyboard-control" || arguments[1] == "--foreground-enter-control" ||
                 arguments[1] == "--verify-window-states" || arguments[1] == "--verify-covered-reply" ||
                 arguments[1] == "--verify-covered-small" || SelfTestForm.IsDesktopSingle(arguments[1])))
                && arguments[0] == "--self-test";
            bool continuous = (arguments.Length == 1 || arguments.Length == 2) && arguments[0] == "--continuous";
            bool external = arguments.Length == 2 && arguments[0] == "--external-private";
            bool privateLive = arguments.Length == 2 && arguments[0] == "--private-live-check";
            bool privateForm = arguments.Length == 0 || arguments.Length == 1 && arguments[0] == "--private-core";
            bool legacyLab = arguments.Length == 1 && arguments[0] == "--r5-lab";
            if (arguments.Length != 0 && !autoMatrix && !awaitSelf && !typingPad && !baseline && !round2 &&
                !region && !finishRegion && !msaaWitness && !fullOcclusionWitness && !continuous && !selfTest && !selfPad && !external &&
                !privateLive && !privateForm && !legacyLab)
                return RunCommand(arguments);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (privateLive) { Application.Run(new PrivateLiveCheck(arguments[1])); return 0; }
            if (privateForm)
            {
                using (var instance = new Mutex(false, @"Local\QQPrivateApp.Phase3.Control"))
                {
                    bool entered;
                    try { entered = instance.WaitOne(0); }
                    catch (AbandonedMutexException) { entered = true; }
                    if (!entered)
                    {
                        MessageBox.Show("自动回复控制窗已打开。请使用现有窗口或系统托盘中的控制窗。", "QQ 独立自动回复");
                        return 2;
                    }
                    try { Application.Run(new PrivateCoreForm(PrivateProfile.Folder(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "phase3"))); }
                    finally { instance.ReleaseMutex(); }
                }
                return 0;
            }
            if (external) { Application.Run(new ExternalPrivateForm(arguments[1])); return 0; }
            if (typingPad) { TypingPad.Show(arguments); return 0; }
            if (selfPad) { TypingPad.ShowAutomatic(arguments); return 0; }
            if (selfTest) { Application.Run(new SelfTestForm(arguments.Length == 2 ? arguments[1] : null)); return 0; }
            if (continuous)
            {
                var panel = new ContinuousForm();
                if (arguments.Length == 2) panel.Shown += (s, e) => panel.EnrollPreviousSelf(Int64.Parse(arguments[1]));
                Application.Run(panel);
                return 0;
            }
            var form = new LabForm();
            form.Shown += (s, e) => {
                if (awaitSelf) form.CaptureAndRun(Int64.Parse(arguments[1]), arguments.Length == 3);
                if (autoMatrix) { form.SelectTarget(Int64.Parse(arguments[1])); form.Matrix(false); }
                if (baseline || round2 || region || finishRegion || msaaWitness || fullOcclusionWitness)
                    form.Round2ExistingSelf(Int64.Parse(arguments[1]), round2, region || finishRegion,
                        round2 && arguments.Length == 3, finishRegion ? arguments[2] : null, msaaWitness,
                        arguments[0] == "--round2-msaa-write", arguments[0] == "--round2-msaa-default",
                        msaaWitness && arguments.Length == 3 ? arguments[2] : null, fullOcclusionWitness);
            };
            Application.Run(form);
            return 0;
        }
        catch (SelfTestHalt ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("{0} (0x{1:X8})", ex.GetType().Name, ex.HResult);
            return 1;
        }
    }

    private static int RunCommand(string[] arguments)
    {
        if (arguments.Length == 3 && arguments[0] == "--route-fixture-copy")
            return RouteDiagnostics.CopyFixture(arguments[1], arguments[2]);
        if (arguments.Length == 2 && arguments[0] == "--route-benchmark")
            return Task.Run(() => RouteDiagnostics.Benchmark(arguments[1])).GetAwaiter().GetResult();
        if (arguments.Length == 1 && arguments[0] == "--external-window-inventory")
            return ExternalWindowSetup.Inventory();
        if (arguments.Length == 2 && arguments[0] == "--external-restore")
            return ExternalWindowSetup.Restore(Int64.Parse(arguments[1]));
        if (arguments.Length == 2 && arguments[0] == "--external-hide-setup-window")
            return ExternalWindowSetup.Hide(Int64.Parse(arguments[1]));
        if (arguments.Length == 2 && arguments[0] == "--external-minimize-setup-window")
            return ExternalWindowSetup.Minimize(Int64.Parse(arguments[1]));
        if (arguments.Length == 1 && arguments[0] == "--inspect-verified-window")
            return Task.Run(() => LocalSendComparison.InspectWindow()).GetAwaiter().GetResult();
        if (arguments.Length >= 2 && arguments[0] == "--audit-verified-markers")
            return Task.Run(() => LocalSendComparison.Audit(arguments.Skip(1).ToArray())).GetAwaiter().GetResult();
        if (arguments.Length == 1 && arguments[0] == "--capture-verified-empty-editor")
            return Task.Run(() => LocalSendComparison.CaptureEmptyEditor()).GetAwaiter().GetResult();
        if (arguments.Length == 1 && arguments[0] == "--refresh-verified-qq-display")
            return Task.Run(() => LocalSendComparison.RefreshDisplay()).GetAwaiter().GetResult();
        if (arguments.Length == 2 && arguments[0] == "--refresh-confirmed-self-binding")
            return Task.Run(() => SelfTestIdentity.RefreshConfirmedForeground(Target.FromKnownHandle(Int64.Parse(arguments[1]))))
                .GetAwaiter().GetResult();
        if ((arguments.Length == 2 || arguments.Length == 3) && arguments[0] == "--bind-operator-verified-self")
            return Task.Run(() => SelfTestIdentity.BindOperatorVerified(Target.FromKnownHandle(Int64.Parse(arguments[1])),
                arguments.Length == 3 ? arguments[2] : null))
                .GetAwaiter().GetResult();
        if (arguments.Length == 3 && arguments[0] == "--local-send-comparison" &&
            (arguments[1] == "observe" || arguments[1] == "fill" || arguments[1] == "submit"))
            return Task.Run(() => LocalSendComparison.Run(arguments[1], arguments[2])).GetAwaiter().GetResult();
        if (arguments.Length == 4 && arguments[0] == "--recover-self-test-draft")
            return Task.Run(() => {
                var id = arguments[1];
                int seq;
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9A-F]{10}$") ||
                    !Int32.TryParse(arguments[2], out seq) || seq < 1 || seq > SelfTestBudget.Cap ||
                    (arguments[3] != "in" && arguments[3] != "out")) return 2;
                var protocol = new SelfTestProtocol(id);
                var body = arguments[3] == "in" ? protocol.Input(seq) : protocol.Output(seq);
                var budget = new SelfTestBudget(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "runs", "R4-budget-20261121.log"));
                if (!budget.WasReserved(body)) return 2;
                var verifiedTarget = SelfTestIdentity.OpenTarget();
                using (var mutex = new System.Threading.Mutex(false, @"Local\QQWindowLab.Continuous." + verifiedTarget.Hwnd.ToInt64()))
                {
                    bool owned;
                    try { owned = mutex.WaitOne(0); }
                    catch (System.Threading.AbandonedMutexException) { owned = true; }
                    if (!owned) return 2;
                    try
                    {
                        var self = new SelfTestIdentity(verifiedTarget);
                        return self.ClearUnsubmittedDraft(body, Console.WriteLine) ? 0 : 2;
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }).GetAwaiter().GetResult();
        if (arguments[0] == "--list")
        {
            Console.WriteLine("session={0} foreground={1}", Target.Session, Native.GetForegroundWindow().ToInt64());
            foreach (var item in Target.List())
            {
                var r = Native.Bounds(item.Hwnd);
                Console.WriteLine("QQ.exe pid={0} hwnd={1} title={2} class={3} rect={4},{5},{6},{7} iconic={8} foreground={9}",
                    item.Pid, item.Hwnd.ToInt64(), item.Title == "QQ" ? "QQ" : "[private]", item.WindowClass,
                    r.X, r.Y, r.Width, r.Height, Native.IsIconic(item.Hwnd), Native.GetForegroundWindow() == item.Hwnd);
            }
            return 0;
        }
        bool audit = arguments.Length >= 3 && arguments[0] == "--verify-local-markers";
        bool draftAudit = arguments.Length == 3 && arguments[0] == "--verify-test-draft";
        bool msaa = arguments.Length == 2 && arguments[0] == "--probe-direct-msaa";
        bool nativeMode = (arguments.Length == 3 || arguments.Length == 7) && arguments[0] == "--qq-layout";
        if (nativeMode)
        {
            var panel = Target.FromKnownHandle(Int64.Parse(arguments[1]));
            var rect = arguments.Length == 7 ? (System.Drawing.Rectangle?)new System.Drawing.Rectangle(
                Int32.Parse(arguments[3]), Int32.Parse(arguments[4]), Int32.Parse(arguments[5]), Int32.Parse(arguments[6])) : null;
            return Task.Run(() => NativeChatLayout.Change(panel, arguments[2], Console.WriteLine, rect)).GetAwaiter().GetResult();
        }
        if (arguments.Length == 3 && arguments[0] == "--qq-layout-probe")
        {
            var panel = Target.FromKnownHandle(Int64.Parse(arguments[1]));
            return Task.Run(() => NativeChatLayout.Inspect(panel, arguments[2], Console.WriteLine)).GetAwaiter().GetResult();
        }
        if (arguments.Length == 3 && (arguments[0] == "--qq-open-known" ||
            arguments[0] == "--qq-doubleclick-known" || arguments[0] == "--qq-context-known"))
        {
            var panel = Target.FromKnownHandle(Int64.Parse(arguments[1]));
            return Task.Run(() => NativeChatLayout.OpenKnown(panel, arguments[2], Console.WriteLine,
                arguments[0] != "--qq-open-known", arguments[0] == "--qq-context-known")).GetAwaiter().GetResult();
        }
        if (!audit && !msaa && !draftAudit && (arguments.Length != 2 || arguments[0] != "--probe")) return 2;
        var target = Target.List().Single(x => x.Hwnd.ToInt64() == Int64.Parse(arguments[1]));
        return Task.Run(() => {
            var scan = Controls.Scan(target);
            if (draftAudit)
            {
                const string seed = "窗口测试 基准-01";
                var verified = new Binding(target, false).RequireMatch();
                if (!verified.Messages(seed).Contains(seed)) throw new InvalidOperationException("基准编号未命中");
                var text = verified.Draft();
                var expected = arguments[2];
                Console.WriteLine("test draft exact={0} known-fragment={1} length={2} empty-verified={3}",
                    text == expected, !String.IsNullOrEmpty(text) && expected.Contains(text),
                    text == null ? -1 : text.Length, verified.EmptyDraft());
                return 0;
            }
            if (msaa)
            {
                var bound = new Binding(target, false);
                var verified = bound.RequireMatch();
                const string seed = "窗口测试 基准-01";
                if (!verified.Messages(seed).Contains(seed)) throw new InvalidOperationException("基准编号未命中");
                MsaaProbe.Describe(target, verified, Console.WriteLine);
                return 0;
            }
            if (audit)
            {
                const string seed = "窗口测试 基准-01";
                var markers = arguments.Skip(2).ToArray();
                var bound = new Binding(target, false);
                var snapshot = bound.RequireMatch().Messages(markers.Concat(new[] { seed }).ToArray());
                if (!snapshot.Contains(seed)) throw new InvalidOperationException("基准编号未命中");
                for (int i = 0; i < markers.Length; i++)
                    Console.WriteLine("marker[{0}]: evidence={1} matchingRows={2}", i + 1,
                        snapshot.Evidence(markers[i]), snapshot.MarkerRows(markers[i]));
                return 0;
            }
            Console.WriteLine(scan.Describe());
            Console.WriteLine("classes=" + scan.Shapes);
            Console.WriteLine("history descendants=" + scan.RawShape(scan.History));
            Console.WriteLine("editor descendants=" + scan.RawShape(scan.Editor));
            return 0;
        }).GetAwaiter().GetResult();
    }
}
