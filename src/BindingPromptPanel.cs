using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class BindingPromptPanel : TableLayoutPanel
{
    private readonly Label instruction = new Label { Dock = DockStyle.Fill, Padding = new Padding(6, 5, 0, 0) };
    private readonly Label digits = new Label { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label progress = new Label { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 0, 0), TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button copy = new Button { Text = "复制验证码", AutoSize = true };
    private readonly Timer timer = new Timer { Interval = 250 };
    private readonly Font codeFont = new Font("Consolas", 24, FontStyle.Bold);
    private NumericChallenge active;
    private int failures;

    internal event EventHandler Regenerate;
    internal event EventHandler Cancel;

    internal BindingPromptPanel()
    {
        Dock = DockStyle.Top;
        Height = 172;
        Padding = new Padding(8, 0, 8, 0);
        ColumnCount = 1;
        RowCount = 4;
        RowStyles.Add(new RowStyle(SizeType.Absolute, 61));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        digits.Font = codeFont;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var next = new Button { Text = "重新生成", AutoSize = true };
        var cancel = new Button { Text = "取消核验", AutoSize = true };
        buttons.Controls.AddRange(new Control[] { copy, next, cancel });
        Controls.Add(instruction, 0, 0);
        Controls.Add(digits, 0, 1);
        Controls.Add(progress, 0, 2);
        Controls.Add(buttons, 0, 3);
        copy.Click += (sender, args) => {
            if (active == null || active.SecondsLeft == 0) return;
            try { Clipboard.SetText(active.Code); }
            catch (System.Runtime.InteropServices.ExternalException) { progress.Text = "剪贴板暂时忙碌，可以手动输入数字。"; }
        };
        next.Click += (sender, args) => { if (Regenerate != null) Regenerate(this, EventArgs.Empty); };
        cancel.Click += (sender, args) => { if (Cancel != null) Cancel(this, EventArgs.Empty); };
        timer.Tick += (sender, args) => RefreshProgress();
        Visible = false;
    }

    internal void ShowCode(NumericChallenge challenge, string bot, string botQq, string peer, string peerQq)
    {
        active = challenge;
        failures = 0;
        instruction.Text = "请用【" + peer + "（" + peerQq + "）】\r\n给【" + bot + "（" + botQq + "）】发送以下 5 位数字。\r\n只发送数字即可，无需添加其他文字。";
        digits.Text = active.Code;
        Visible = true;
        RefreshProgress();
        timer.Start();
    }

    internal void RecordFailure(int total)
    {
        failures = total;
        RefreshProgress();
    }

    private void RefreshProgress()
    {
        if (active == null) return;
        var left = active.SecondsLeft;
        copy.Enabled = left > 0;
        progress.Text = left == 0 ? "验证码已过期，请重新生成。" :
            String.Format("剩余 {0:00}:{1:00}    错误次数 {2}/{3}", left / 60, left % 60, failures, NumericChallenge.MaxAttempts);
    }

    internal void End(string message = null)
    {
        timer.Stop();
        active = null;
        digits.Text = "";
        instruction.Text = "";
        copy.Enabled = false;
        progress.Text = message ?? "";
        Visible = message != null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); codeFont.Dispose(); }
        base.Dispose(disposing);
    }
}
