using System;
using System.IO;
using System.Linq;
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
            bool typingPad = arguments.Length == 5 && arguments[0] == "--typing-pad";
            if (arguments.Length != 0 && !autoMatrix && !awaitSelf && !typingPad && !baseline && !round2 &&
                !region && !finishRegion && !msaaWitness && !fullOcclusionWitness) return RunCommand(arguments);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (typingPad) { TypingPad.Show(arguments); return 0; }
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
        catch (Exception ex)
        {
            Console.Error.WriteLine("{0} (0x{1:X8})", ex.GetType().Name, ex.HResult);
            return 1;
        }
    }

    private static int RunCommand(string[] arguments)
    {
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
