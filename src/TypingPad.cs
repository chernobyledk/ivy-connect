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
    internal IntPtr Hwnd { get { return new IntPtr(Interlocked.Read(ref hwnd)); } }
    internal int Edits { get { return Volatile.Read(ref edits); } }
    internal int Pid { get { return process.Id; } }
    internal TypingPad(Rectangle rect)
    {
        var args = new[] { rect.X, rect.Y, rect.Width, rect.Height }.Select(n => n.ToString(CultureInfo.InvariantCulture));
        process = new Process {
            StartInfo = new ProcessStartInfo(Application.ExecutablePath, "--typing-pad " + String.Join(" ", args)) {
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            }
        };
        process.OutputDataReceived += (s, e) => {
            if (e.Data == "edit") Interlocked.Increment(ref edits);
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
        var size = args.Skip(1).Select(s => Int32.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        using (var form = new Form {
            Text = "QQ 验证临时便笺", StartPosition = FormStartPosition.Manual,
            Bounds = new Rectangle(size[0], size[1], size[2], size[3]), ShowInTaskbar = true
        })
        {
            var input = new TextBox { Multiline = true, Dock = DockStyle.Fill };
            input.TextChanged += (s, e) => Console.WriteLine("edit");
            form.Controls.Add(input);
            form.Shown += (s, e) => { input.Focus(); Console.WriteLine(form.Handle.ToInt64()); };
            Application.Run(form);
        }
    }
    public void Dispose()
    {
        if (!process.HasExited) process.CloseMainWindow();
        process.Dispose();
    }
}
