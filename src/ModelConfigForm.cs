using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ModelConfigForm : Form
{
    private readonly string path;
    private readonly Action<string> log;
    private readonly ComboBox provider = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox endpoint = new TextBox(), model = new TextBox(),
        key = new TextBox { UseSystemPasswordChar = true, MaxLength = 512 },
        role = new TextBox { Multiline = true, MaxLength = 1200, ScrollBars = ScrollBars.Vertical };
    private readonly NumericUpDown timeout = new NumericUpDown { Minimum = 1, Maximum = 120 };
    private readonly NumericUpDown turns = new NumericUpDown { Minimum = 0, Maximum = 8 };
    private readonly Button test = new Button { Text = "模型连接测试", AutoSize = true };
    private readonly Button save = new Button { Text = "加密保存", AutoSize = true };
    private readonly Label result = new Label { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private CancellationTokenSource active;

    internal ModelConfigForm(string path, Action<string> log)
    {
        this.path = path;
        this.log = log;
        Text = "模型设置";
        ClientSize = new Size(535, 376);
        MinimumSize = new Size(520, 395);
        StartPosition = FormStartPosition.CenterParent;
        provider.Items.Add(PrivateModelSettings.DeepSeek);
        provider.SelectedIndex = 0;
        var settings = new PrivateModelSettings();
        string loadError = null;
        try { settings = ModelSettingsFile.Load(path); }
        catch (Exception ex) { loadError = "现有设置无法解密 · " + Experiment.Failure(ex); }
        endpoint.Text = settings.Endpoint;
        model.Text = settings.Model;
        key.Text = settings.ApiKey;
        role.Text = settings.RolePrompt;
        timeout.Value = Math.Max(1, Math.Min(120, settings.TimeoutSeconds));
        turns.Value = Math.Max(0, Math.Min(8, settings.ContextTurns));
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 217, Padding = new Padding(8), ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));
        var labels = new[] { "服务商和格式", "完整 API 地址", "模型名称", "API 密钥", "超时（秒）", "保留上下文（轮）", "角色设定（可选）" };
        var inputs = new Control[] { provider, endpoint, model, key, timeout, turns, role };
        for (int i = 0; i < labels.Length; i++)
        {
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, i == 6 ? 48 : 25));
            grid.Controls.Add(new Label { Text = labels[i], AutoSize = true, Anchor = AnchorStyles.Left }, 0, i);
            inputs[i].Dock = DockStyle.Fill;
            grid.Controls.Add(inputs[i], 1, i);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 43, Padding = new Padding(6) };
        buttons.Controls.AddRange(new Control[] { test, save });
        Controls.Add(result);
        Controls.Add(buttons);
        Controls.Add(grid);
        result.Text = loadError ?? "连接测试只把固定探针发给 API，结果显示在这里。\r\n0 轮停用上下文。角色设定独立于机器人 QQ 号。";
        test.Click += async (s, e) => await Test();
        save.Click += (s, e) => {
            try { ModelSettingsFile.Save(path, ReadFields()); result.Text = "已加密保存。"; }
            catch (Exception ex) { result.Text = "保存失败 · " + DisplayError(ex); }
        };
        FormClosing += (s, e) => { if (active != null) active.Cancel(); };
    }

    private PrivateModelSettings ReadFields()
    {
        return new PrivateModelSettings { Provider = (string)provider.SelectedItem, Endpoint = endpoint.Text.Trim(),
            Model = model.Text.Trim(), ApiKey = key.Text.Trim(), RolePrompt = role.Text.Trim(),
            TimeoutSeconds = (int)timeout.Value, ContextTurns = (int)turns.Value };
    }

    internal static string DisplayError(Exception error)
    {
        var modelError = error as ModelReplyException;
        return modelError == null ? Experiment.Failure(error) : modelError.Message;
    }

    private async Task Test()
    {
        using (var cancellation = new CancellationTokenSource())
        {
            active = cancellation;
            test.Enabled = save.Enabled = false;
            result.Text = "正在请求模型…";
            try
            {
                using (var reply = new DeepSeekPrivateReply(ReadFields(), () => new PrivateModelTurn[0], log))
                {
                    var answer = await reply.TestAsync(cancellation.Token);
                    if (!IsDisposed) result.Text = "连接成功。模型最终回答\r\n" +
                        (answer.Length > 140 ? answer.Substring(0, 140) + "…" : answer);
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) result.Text = "连接测试已取消。"; }
            catch (Exception ex) { if (!IsDisposed) result.Text = "连接测试未通过 · " + DisplayError(ex); }
            finally { active = null; if (!IsDisposed) test.Enabled = save.Enabled = true; }
        }
    }
}
