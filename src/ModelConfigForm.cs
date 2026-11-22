using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal sealed class ModelConfigForm : Form
{
    internal const string ExampleName = "冰上Ivy";
    internal const string ExampleRole = "你扮演虚构角色冰上Ivy，一名十六岁的女高中生。\r\n性格自然、友善，有自己的想法。\r\n使用轻松、简洁的中文进行日常聊天，根据对方的话题自然回应。";

    private readonly string folder;
    private readonly Action<string> log;
    private readonly Action clearContext;
    private readonly Label result = new Label { Dock = DockStyle.Fill, Padding = new Padding(8), AutoEllipsis = true };
    private readonly Label keyState = new Label { AutoSize = true };
    private readonly Label thinkingNote = new Label { AutoSize = true, Padding = new Padding(0, 3, 0, 0) };
    private readonly ListBox profiles = new ListBox { Dock = DockStyle.Fill };
    private readonly ComboBox provider = DropDown(), thinking = DropDown(), effort = DropDown();
    private readonly TextBox name = new TextBox { MaxLength = 60 }, endpoint = new TextBox(), model = new TextBox(),
        key = new TextBox { UseSystemPasswordChar = true, MaxLength = 512, Width = 270 },
        roleName = new TextBox { MaxLength = 60 }, role = new TextBox { Multiline = true, MaxLength = 1200, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox removeKey = new CheckBox { Text = "清除密钥", AutoSize = true };
    private readonly NumericUpDown timeout = new NumericUpDown { Minimum = 1, Maximum = 120 };
    private readonly NumericUpDown turns = new NumericUpDown { Minimum = 0, Maximum = 8 };
    private readonly FlowLayoutPanel actions = new FlowLayoutPanel(), profileActions = new FlowLayoutPanel();
    private readonly TableLayoutPanel editor = new TableLayoutPanel { Dock = DockStyle.Top, Height = 400, Padding = new Padding(8), ColumnCount = 2 };
    private readonly Button identify = new Button { Text = "识别服务商", AutoSize = true };
    private PrivateModelCatalog catalog;
    private PrivateModelProfile editing;
    private CancellationTokenSource active;
    private bool populating;

    private static ComboBox DropDown() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; }
    private static FlowLayoutPanel Line(params Control[] children)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        panel.Controls.AddRange(children);
        return panel;
    }

    private static Button ButtonFor(string label, Action click)
    {
        var button = new Button { Text = label, AutoSize = true };
        button.Click += (s, e) => click();
        return button;
    }

    internal ModelConfigForm(string folder, Action<string> log, Action clearContext, string runningName = null)
    {
        this.folder = folder;
        this.log = log;
        this.clearContext = clearContext;
        Text = "Ivy connect · 模型与密钥";
        ClientSize = new Size(870, 627);
        MinimumSize = new Size(790, 635);
        StartPosition = FormStartPosition.CenterParent;
        var hint = new Label { Dock = DockStyle.Top, Height = 46, Padding = new Padding(8, 6, 8, 0), Text =
            (runningName == null ? "手动选择模型配置与密钥。" : "运行中的会话继续使用「" + runningName + "」的启动快照。") +
            "\r\n切换、编辑和删除在下一次停止并启动后生效。模型切换与当前 QQ 会话相互独立。" };
        provider.Items.AddRange(ModelCapabilities.Providers);
        thinking.Items.AddRange(new[] { "模型默认", "开启", "关闭" });
        effort.Items.AddRange(new[] { "模型默认强度", "低 (low)", "高 (high)", "最高 (max)" });
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var endpointRow = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        endpointRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        endpoint.Dock = DockStyle.Fill;
        endpointRow.Controls.Add(endpoint, 0, 0);
        endpointRow.Controls.Add(identify, 1, 0);
        var sample = ButtonFor("填入示例", FillExample);
        var clearRole = ButtonFor("清空角色", () => { roleName.Clear(); role.Clear(); });
        var clearHistory = ButtonFor("清空聊天上下文…", clearContext);
        AddRow("配置名称", name);
        AddRow("服务商 / 接口", provider);
        AddRow("完整 API 地址", endpointRow, 32);
        AddRow("模型名称", model);
        AddRow("替换 API 密钥", Line(key, removeKey), 30);
        AddRow("密钥状态", keyState);
        thinking.Width = 122;
        AddRow("思考模式", Line(thinking, thinkingNote));
        AddRow("思考强度", effort);
        AddRow("超时 / 上下文", Line(timeout, new Label { Text = "秒　保留", AutoSize = true }, turns,
            new Label { Text = "轮（0 为停用）", AutoSize = true }), 29);
        AddRow("角色名称", roleName);
        AddRow("角色设定", role, 58);
        AddRow("角色操作", Line(sample, clearRole, clearHistory), 35);
        actions.Dock = DockStyle.Bottom;
        actions.Height = 54;
        actions.Controls.AddRange(new[] { ButtonFor("加密保存", Save), ButtonFor("设为当前配置", SelectCurrent),
            ButtonFor("轻量检查", async () => await Check(false)), ButtonFor("测试真实回答…", async () => await Check(true)) });
        profileActions.Dock = DockStyle.Bottom;
        profileActions.Height = 74;
        profileActions.Controls.AddRange(new[] { ButtonFor("新增", StartNew), ButtonFor("复制…", Duplicate), ButtonFor("删除…", Remove) });
        var left = new Panel { Dock = DockStyle.Left, Width = 215, Padding = new Padding(6) };
        left.Controls.Add(profiles);
        left.Controls.Add(profileActions);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(result);
        right.Controls.Add(actions);
        right.Controls.Add(editor);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(hint);

        identify.Click += (s, e) => Recognize();
        provider.SelectedIndexChanged += (s, e) => UpdateThinking();
        model.TextChanged += (s, e) => UpdateThinking();
        endpoint.TextChanged += (s, e) => { UpdateThinking(); UpdateKeyState(); };
        thinking.SelectedIndexChanged += (s, e) => UpdateThinking();
        removeKey.CheckedChanged += (s, e) => UpdateKeyState();
        profiles.SelectedIndexChanged += (s, e) => {
            if (!populating && profiles.SelectedItem is PrivateModelProfile) ShowProfile((PrivateModelProfile)profiles.SelectedItem);
        };
        FormClosing += (s, e) => { if (active != null) active.Cancel(); };
        try
        {
            catalog = ModelCatalogFile.Load(folder);
            RefreshList(catalog.Active ?? catalog.Profiles.FirstOrDefault());
            result.Text = "保存后点「设为当前配置」。角色设定变更会用于以后启动的请求，已有上下文会保留。";
        }
        catch (Exception error)
        {
            result.Text = "现有模型设置无法读取，原文件已保留 · " + DisplayError(error);
            SetBusy(true);
        }
    }

    private void AddRow(string title, Control field, int height = 27)
    {
        var row = editor.RowCount++;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left };
        editor.Controls.Add(label, 0, row);
        field.Dock = DockStyle.Fill;
        editor.Controls.Add(field, 1, row);
    }

    private void RefreshList(PrivateModelProfile chosen)
    {
        populating = true;
        profiles.Items.Clear();
        profiles.Items.AddRange(catalog.Profiles);
        profiles.SelectedItem = chosen;
        populating = false;
        ShowProfile(chosen);
    }

    private void ShowProfile(PrivateModelProfile profile)
    {
        populating = true;
        editing = profile;
        var values = profile == null ? new PrivateModelSettings() : profile.Settings;
        name.Text = profile == null ? "" : profile.Name;
        provider.SelectedItem = values.Provider;
        if (provider.SelectedIndex < 0) provider.SelectedIndex = 2;
        endpoint.Text = values.Endpoint;
        model.Text = values.Model;
        key.Clear();
        removeKey.Checked = false;
        roleName.Text = values.RoleName;
        role.Text = values.RolePrompt;
        thinking.SelectedIndex = values.ThinkingMode == "enabled" ? 1 : values.ThinkingMode == "disabled" ? 2 : 0;
        effort.SelectedIndex = Array.IndexOf(new[] { "default", "low", "high", "max" }, values.ThinkingEffort);
        if (effort.SelectedIndex < 0) effort.SelectedIndex = 0;
        timeout.Value = Math.Max(1, Math.Min(120, values.TimeoutSeconds));
        turns.Value = Math.Max(0, Math.Min(8, values.ContextTurns));
        populating = false;
        UpdateThinking();
        UpdateKeyState();
        if (profile != null)
            result.Text = (catalog.ActiveId == profile.Id ? "当前配置 · " : "已保存 · ") + profile.Availability();
    }

    private void UpdateThinking()
    {
        if (populating) return;
        if (thinking.SelectedIndex < 0) thinking.SelectedIndex = 0;
        bool supported = ModelCapabilities.SupportsThinking(new PrivateModelSettings {
            Endpoint = endpoint.Text.Trim(), Provider = (string)provider.SelectedItem, Model = model.Text.Trim()
        });
        thinking.Enabled = supported;
        thinkingNote.Text = supported ? "按 DeepSeek 文档映射" : "当前接口或模型的思考参数未确认";
        if (!supported) thinking.SelectedIndex = 0;
        effort.Enabled = supported && thinking.SelectedIndex == 1;
        if (!effort.Enabled) effort.SelectedIndex = 0;
    }

    private void UpdateKeyState()
    {
        var hasKey = editing != null && !String.IsNullOrEmpty(editing.Settings.ApiKey);
        var same = hasKey && ModelCapabilities.SameOrigin(editing.Settings.Endpoint, endpoint.Text.Trim());
        keyState.Text = removeKey.Checked ? "保存时清除" : same ? "已保存 ••••••••　留空保留" :
            hasKey ? "服务地址来源变化，请为新来源填写密钥" : "尚未保存密钥";
        key.Enabled = !removeKey.Checked;
    }

    private PrivateModelSettings ReadFields()
    {
        var address = endpoint.Text.Trim();
        var apiKey = key.Text.Trim();
        if (apiKey.Length == 0 && editing != null && ModelCapabilities.SameOrigin(editing.Settings.Endpoint, address))
            apiKey = editing.Settings.ApiKey;
        return new PrivateModelSettings { Provider = (string)provider.SelectedItem, Endpoint = address,
            Model = model.Text.Trim(), ApiKey = removeKey.Checked ? "" : apiKey, RoleName = roleName.Text.Trim(), RolePrompt = role.Text.Trim(),
            TimeoutSeconds = (int)timeout.Value, ContextTurns = (int)turns.Value,
            ThinkingMode = new[] { "default", "enabled", "disabled" }[thinking.SelectedIndex],
            ThinkingEffort = new[] { "default", "low", "high", "max" }[effort.SelectedIndex] };
    }

    private void StartNew()
    {
        RefreshList(null);
        name.Focus();
        result.Text = "请填写新配置。点「加密保存」后可设为当前。";
    }

    private void Save()
    {
        try
        {
            var value = PrivateModelProfile.New(name.Text.Trim(), ReadFields());
            if (editing != null) value.Id = editing.Id;
            if (editing != null && SameSettings(value.Settings, editing.Settings))
            { value.VerifiedKind = editing.VerifiedKind; value.VerifiedUtc = editing.VerifiedUtc; }
            catalog.Put(value);
            ModelCatalogFile.Save(folder, catalog);
            RefreshList(value);
            result.Text = "配置已加密保存。已有聊天上下文已保留。";
        }
        catch (Exception error) { ReloadCatalogAfterError(error); }
    }

    private void SelectCurrent()
    {
        if (editing == null) { result.Text = "请先加密保存配置。"; return; }
        if (name.Text.Trim() != editing.Name || !SameSettings(ReadFields(), editing.Settings))
        { result.Text = "请先保存修改，再设为当前配置。"; return; }
        try
        {
            catalog.Select(editing.Id);
            ModelCatalogFile.Save(folder, catalog);
            result.Text = "已设为当前配置。下次开始回复时使用这套设置。";
        }
        catch (Exception error) { ReloadCatalogAfterError(error); }
    }

    private void Duplicate()
    {
        if (editing == null) { result.Text = "请选择要复制的已保存配置。"; return; }
        var answer = MessageBox.Show(this, "复制选中的已保存配置。是否一并复制已经加密保存的密钥？",
            "复制配置", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Cancel) return;
        var baseName = editing.Name.Substring(0, Math.Min(editing.Name.Length, 48)) + " 副本";
        var copyName = baseName;
        int serial = 2;
        while (catalog.Profiles.Any(item => String.Equals(item.Name, copyName, StringComparison.OrdinalIgnoreCase))) copyName = baseName + " " + serial++;
        try
        {
            var copy = editing.CopyAs(copyName, answer == DialogResult.Yes);
            catalog.Put(copy);
            ModelCatalogFile.Save(folder, catalog);
            RefreshList(copy);
            result.Text = answer == DialogResult.Yes ? "已复制并加密保存密钥。" : "已复制配置，副本密钥为空。";
        }
        catch (Exception error) { ReloadCatalogAfterError(error); }
    }

    private void Remove()
    {
        if (editing == null) return;
        if (MessageBox.Show(this, "删除所选配置及其中的密钥？运行中的会话保留启动快照，删除当前配置后需手动选一套再启动。",
            "删除配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        try
        {
            catalog.Remove(editing.Id);
            ModelCatalogFile.Save(folder, catalog);
            RefreshList(catalog.Active ?? catalog.Profiles.FirstOrDefault());
            result.Text = "已删除所选配置。";
        }
        catch (Exception error) { ReloadCatalogAfterError(error); }
    }

    private void ReloadCatalogAfterError(Exception error)
    {
        try { catalog = ModelCatalogFile.Load(folder); }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        result.Text = "操作未完成 · " + DisplayError(error);
    }

    private void Recognize()
    {
        var found = ModelCapabilities.Recognize(endpoint.Text.Trim());
        result.Text = found.Message;
        if (found.Suggestion != null && found.Suggestion != (string)provider.SelectedItem &&
            MessageBox.Show(this, found.Message + "\r\n采用此建议作为当前编辑中的接口类型？",
                "识别服务商", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            provider.SelectedItem = found.Suggestion;
    }

    private void FillExample()
    {
        if ((!String.IsNullOrWhiteSpace(roleName.Text) || !String.IsNullOrWhiteSpace(role.Text)) &&
            MessageBox.Show(this, "用「冰上Ivy」示例替换编辑区里的角色设定？已有聊天上下文会保留。",
                "填入示例", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        roleName.Text = ExampleName;
        role.Text = ExampleRole;
        result.Text = "已填入可编辑示例。保存后对下次启动生效。可主动清空聊天上下文。";
    }

    private async Task Check(bool completion)
    {
        PrivateModelSettings draft;
        Uri destination;
        try { draft = ReadFields(); destination = draft.Validate(); }
        catch (Exception error) { result.Text = DisplayError(error); return; }
        var purpose = completion ? "将发送固定测试句并请求生成一个回答，可能计费。" :
            "将向同一服务地址查询鉴权或模型列表，无生成请求。";
        if (MessageBox.Show(this, purpose + "\r\n目标服务「" + destination.Authority + "」，仅向此服务发送当前选用的密钥。",
            "模型检查", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        using (var cancellation = new CancellationTokenSource())
        {
            active = cancellation;
            SetBusy(true);
            result.Text = "正在检查已配置地址…";
            try
            {
                using (var reply = new DeepSeekPrivateReply(draft, () => new PrivateModelTurn[0], log))
                {
                    string kind;
                    if (completion) { await reply.TestAsync(cancellation.Token); kind = "completion"; }
                    else kind = await reply.ProbeAsync(cancellation.Token);
                    if (!IsDisposed) RecordProbe(draft, kind, ProbeDescription(kind));
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) result.Text = "模型检查已取消。"; }
            catch (Exception error) { if (!IsDisposed) RecordProbe(draft, "failed", "模型检查未通过 · " + DisplayError(error)); }
            finally { active = null; if (!IsDisposed) SetBusy(false); }
        }
    }

    private void RecordProbe(PrivateModelSettings draft, string kind, string message)
    {
        result.Text = message;
        if (editing == null || !SameSettings(draft, editing.Settings)) return;
        editing.VerifiedKind = kind;
        editing.VerifiedUtc = PrivateStore.Stamp();
        try { ModelCatalogFile.Save(folder, catalog); }
        catch (Exception error) { ReloadCatalogAfterError(error); }
    }

    private static bool SameSettings(PrivateModelSettings a, PrivateModelSettings b)
    {
        return a.Provider == b.Provider && a.Endpoint == b.Endpoint && a.Model == b.Model && a.ApiKey == b.ApiKey &&
            a.ThinkingMode == b.ThinkingMode && a.ThinkingEffort == b.ThinkingEffort && a.RoleName == b.RoleName &&
            a.RolePrompt == b.RolePrompt && a.TimeoutSeconds == b.TimeoutSeconds && a.ContextTurns == b.ContextTurns;
    }

    private static string ProbeDescription(string kind)
    {
        if (kind == "completion") return "真实模型已完成固定测试。此检查不包含 QQ 正文或上下文。";
        if (kind == "auth-ready") return "DeepSeek 官方鉴权通过，接口提示额度可用。尚未请求模型生成。";
        if (kind == "auth-no-balance") return "DeepSeek 官方鉴权通过，接口提示额度不可用。未请求模型生成。";
        return "收到了标准模型列表。接口格式有响应；密钥是否被检查以及指定模型能否生成仍待验证。";
    }

    private void SetBusy(bool busy)
    {
        editor.Enabled = actions.Enabled = profiles.Enabled = profileActions.Enabled = !busy;
    }

    internal static string DisplayError(Exception error)
    {
        if (error is PrivateRouteLostException || error is PrivateProfileBusyException) return error.Message;
        var modelError = error as ModelReplyException;
        return modelError == null ? Experiment.Failure(error) : modelError.Message;
    }
}
