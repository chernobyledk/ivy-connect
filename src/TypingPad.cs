using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

internal sealed class TypingPad : IDisposable
{
    private readonly Process process;
    private long hwnd;
    private int edits;
    private int witnessLength, witnessMatch;
    private int disposed;
    internal IntPtr Hwnd { get { return new IntPtr(Interlocked.Read(ref hwnd)); } }
    internal int Edits { get { return Volatile.Read(ref edits); } }
    internal int Pid { get { return process.Id; } }
    internal string WitnessSummary { get { return "edits=" + Edits + " length=" + Volatile.Read(ref witnessLength) +
        " exactRepeatedPrefix=" + (Edits == 0 ? "not-tested" : (Volatile.Read(ref witnessMatch) != 0).ToString()); } }
    internal TypingPad(Rectangle rect, string witness = null, Action<string> witnessLog = null, string testCode = null)
    {
        var args = new[] { rect.X, rect.Y, rect.Width, rect.Height }.Select(n => n.ToString(CultureInfo.InvariantCulture));
        process = new Process {
            StartInfo = new ProcessStartInfo(Application.ExecutablePath, "--typing-pad " + String.Join(" ", args) +
                (witness == null ? "" : " " + witness) + (testCode == null ? "" : " " + testCode)) {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            }
        };
        process.OutputDataReceived += (s, e) => {
            if (e.Data == "edit") Interlocked.Increment(ref edits);
            else if (e.Data != null && e.Data.StartsWith("witness ", StringComparison.Ordinal))
            {
                var result = e.Data.Split(' ');
                int length;
                if (result.Length != 4 || !Int32.TryParse(result[2], out length)) return;
                Volatile.Write(ref witnessLength, length);
                Volatile.Write(ref witnessMatch, result[3] == "yes" ? 1 : 0);
                Interlocked.Increment(ref edits);
                if (witnessLog != null) witnessLog(e.Data);
            }
            else
            {
                long value;
                if (Int64.TryParse(e.Data, out value)) Interlocked.Exchange(ref hwnd, value);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
    }
    internal static void Show(string[] args)
    {
        var size = args.Skip(1).Take(4).Select(s => Int32.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var witness = args.Length >= 6 ? args[5] : null;
        var instruction = args.Length == 7 ? "手机发：联测 " + args[6] + " 01 中文" : null;
        using (var form = new Form {
            Text = instruction ?? "QQ 验证临时便笺", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(size[0], size[1], size[2], size[3]), ShowInTaskbar = true
        })
        {
            var input = new TextBox { Multiline = true, Dock = DockStyle.Fill };
            input.TextChanged += (s, e) => {
                if (String.IsNullOrEmpty(witness)) { Console.WriteLine("edit"); return; }
                var typed = input.Text.Replace("\r", "").Replace("\n", "");
                bool matches = Enumerable.Range(0, typed.Length).All(i => typed[i] == witness[i % witness.Length]);
                Console.WriteLine("witness {0:O} {1} {2}", DateTime.UtcNow, typed.Length, matches ? "yes" : "no");
            };
            form.Controls.Add(input);
            if (instruction != null)
                form.Controls.Add(new Label { Text = instruction, Dock = DockStyle.Top,
                    Height = 28, TextAlign = ContentAlignment.MiddleLeft });
            form.Shown += (s, e) => { input.Focus(); Console.WriteLine(form.Handle.ToInt64()); };
            Application.Run(form);
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (!process.HasExited) process.CloseMainWindow();
        process.Dispose();
    }
}
