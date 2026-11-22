using System.Drawing;
using System.Windows.Forms;

internal static class LabTheme
{
    internal static readonly Color Surface = ColorTranslator.FromHtml("#F1F5F3");
    internal static readonly Color Ink = ColorTranslator.FromHtml("#18342D");
    internal static readonly Color Muted = ColorTranslator.FromHtml("#49635A");
    internal static readonly Color Green = ColorTranslator.FromHtml("#1B6654");
    internal static readonly Color Danger = ColorTranslator.FromHtml("#952F3C");
    internal static readonly Color Line = ColorTranslator.FromHtml("#CBD9D3");

    internal static Font Font(float points = 10, FontStyle style = FontStyle.Regular)
    { return new Font(SystemFonts.MessageBoxFont.FontFamily, points, style); }

    internal static void StyleButton(Button button, Color? emphasis = null)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(0, 32);
        if (SystemInformation.HighContrast) return;
        button.FlatStyle = FlatStyle.Flat;
        button.Padding = new Padding(9, 0, 9, 0);
        button.FlatAppearance.BorderColor = emphasis ?? Line;
        button.BackColor = emphasis ?? Color.White;
        button.ForeColor = emphasis.HasValue ? Color.White : Ink;
        button.UseVisualStyleBackColor = false;
        if (emphasis.HasValue)
        {
            EventHandlerStyle(button, emphasis.Value);
            button.EnabledChanged += (sender, args) => EventHandlerStyle(button, emphasis.Value);
        }
    }

    private static void EventHandlerStyle(Button button, Color emphasis)
    {
        button.BackColor = button.Enabled ? emphasis : Surface;
        button.FlatAppearance.BorderColor = button.Enabled ? emphasis : Line;
        button.ForeColor = button.Enabled ? Color.White : Muted;
    }

    internal static Color StatusTone(string state)
    {
        if (SystemInformation.HighContrast) return SystemColors.ControlText;
        if (state.Contains("错误") || state.Contains("待确认") || state.Contains("核验")) return Danger;
        return state.Contains("未连接") ? Muted : Green;
    }

    internal static Label Section(string title)
    {
        var label = new Label { Text = title, AutoSize = true, Font = Font(10, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 5) };
        if (!SystemInformation.HighContrast) label.ForeColor = Green;
        return label;
    }

    internal static FlowLayoutPanel Row(params Control[] elements)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, TabStop = false };
        row.Controls.AddRange(elements);
        return row;
    }
}
