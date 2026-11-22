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
    private readonly Label result = new Label { Dock = DockStyle.Fill, Padding = new Padding(8),
        UseMnemonic = false, UseCompatibleTextRendering = true };
    private readonly Label keyState = new Label { AutoSize = true };
    private readonly Label thinkingNote = new Label { AutoSize = true, Padding = new Padding(0, 3, 0, 0) };
    private readonly ListBox profiles = new ListBox { Dock = DockStyle.Fill };
    private readonly ComboBox provider = DropDown(), protocol = DropDown(), thinking = DropDown(), effort = DropDown(),
        network = DropDown();
    private readonly TextBox name = new TextBox { MaxLength = 60 }, endpoint = new TextBox(), model = new TextBox(),
        key = new TextBox { UseSystemPasswordChar = true, MaxLength = 512, Width = 270 },
        proxy = new TextBox { MaxLength = 256 }, proxyUser = new TextBox { UseSystemPasswordChar = true, MaxLength = 128, Width = 142 },
        proxyPassword = new TextBox { UseSystemPasswordChar = true, MaxLength = 512, Width = 142 },
        roleName = new TextBox { MaxLength = 60 }, role = new TextBox { Multiline = true, MaxLength = 1200, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox removeKey = new CheckBox { Text = "清除密钥", AutoSize = true };
    private readonly CheckBox removeProxyAuth = new CheckBox { Text = "清除认证", AutoSize = true };
    private readonly NumericUpDown timeout = new NumericUpDown { Minimum = 1, Maximum = 120 };
    private readonly NumericUpDown turns = new NumericUpDown { Minimum = 0, Maximum = 8 };
    private readonly NumericUpDown output = new NumericUpDown { Minimum = 0, Maximum = 32768, Width = 85 };
    private readonly FlowLayoutPanel actions = new FlowLayoutPanel(), profileActions = new FlowLayoutPanel();
    private readonly TabControl pages = new TabControl { Dock = DockStyle.Fill };
    private TableLayoutPanel editor;
    private readonly Button identify = new Button { Text = "识别服务商", AutoSize = true };
    private readonly Label resolved = new Label { AutoEllipsis = true, Padding = new Padding(0, 3, 0, 0) };
    private readonly Label modelHint = new Label { AutoEllipsis = true, Padding = new Padding(0, 3, 0, 0) };
    private readonly Label proxyState = new Label { AutoEllipsis = true, Padding = new Padding(0, 3, 0, 0) };
    private readonly ToolTip tips = new ToolTip();
    private readonly Icon dialogIcon;
    private PrivateModelCatalog catalog;
    private PrivateModelProfile editing;
    private CancellationTokenSource active;
    private bool populating;
    private string Provider { get { return ((ModelPreset)provider.SelectedItem ?? ModelCapabilities.Presets.Last()).Name; } }
    private string Protocol { get { return protocol.SelectedIndex == 1 ? ModelCapabilities.Responses : ModelCapabilities.Chat; } }
    private string Network { get { return network.SelectedIndex == 1 ? ModelNetwork.Direct : network.SelectedIndex == 2 ? ModelNetwork.Proxy : ModelNetwork.System; } }

    private static ComboBox DropDown() { return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList }; }
    private static FlowLayoutPanel Line(params Control[] children)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
        panel.Controls.AddRange(children);
        return panel;
    }

    private static Button ButtonFor(string label, Action click)
    {
        var button = new Button { Text = label, AutoSize = true };
        LabTheme.StyleButton(button);
        button.Click += (s, e) => click();
        return button;
    }

    internal ModelConfigForm(string folder, Action<string> log, Action clearContext, string runningName = null)
    {
        this.folder = folder;
        this.log = log;
        this.clearContext = clearContext;
        Text = "Ivy connect · 模型与密钥";
        dialogIcon = PresentationAssets.ReadIcon();
        Icon = dialogIcon;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi; Font = LabTheme.Font(); ClientSize = new Size(910, 550);
        MinimumSize = new Size(650, 435); if (!SystemInformation.HighContrast) { BackColor = LabTheme.Surface; ForeColor = LabTheme.Ink; }
        StartPosition = FormStartPosition.CenterParent;
        var hint = new Label { Dock = DockStyle.Top, Height = 62, Padding = new Padding(8, 6, 8, 0), Text =
            (runningName == null ? "手动选择模型配置与密钥。" : "运行中的会话继续使用「" + runningName + "」的启动快照。") +
            "\r\n切换、编辑和删除在下一次停止并启动后生效。模型切换与当前 QQ 会话相互独立。" };
        provider.Items.AddRange(ModelCapabilities.Presets);
        network.Items.AddRange(new[] { "跟随系统（默认）", "直接连接", "自定义 HTTP 代理" });
        network.Width = 190;
        tips.SetToolTip(proxy, "填已有代理的 http://地址:端口。HTTPS 模型仍验证服务器证书。");
        tips.SetToolTip(proxyUser, "可选代理用户名。留空保留同一代理的已保存用户名。");
        tips.SetToolTip(proxyPassword, "可选代理密码。留空保留同一代理的已保存密码。");
        LabTheme.StyleButton(identify);
        endpoint.AccessibleName = "基础或完整 API 地址";
        key.AccessibleName = "替换 API 密钥";
        proxyUser.AccessibleName = "代理用户名";
        proxyPassword.AccessibleName = "代理密码";
        AddPage("服务与模型");
        AddRow("配置名称", name);
        AddRow("服务商", provider);
        AddRow("接口类型", protocol);
        AddRow("模型 ID", model);
        AddRow("填写提示", modelHint, 45);
        var showApi = new CheckBox { Text = "显示 API 地址与识别选项", AutoSize = true };
        AddRow("高级连接", showApi);
        var apiRows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Visible = false };
        apiRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        apiRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        apiRows.Controls.Add(new Label { Text = "基础或完整 API 地址", AutoSize = true }, 0, 0);
        endpoint.Dock = DockStyle.Fill;
        apiRows.Controls.Add(endpoint, 0, 1);
        apiRows.Controls.Add(identify, 1, 1);
        resolved.Dock = DockStyle.Fill;
        apiRows.Controls.Add(resolved, 0, 2);
        apiRows.SetColumnSpan(resolved, 2);
        var apiRowIndex = editor.RowCount;
        AddRow("", apiRows, 0);
        editor.SetColumn(apiRows, 0);
        editor.SetColumnSpan(apiRows, 2);
        var serviceEditor = editor;
        showApi.CheckedChanged += (s, e) => {
            apiRows.Visible = showApi.Checked;
            serviceEditor.RowStyles[apiRowIndex].Height = showApi.Checked ? showApi.Height * 5.8f : 0;
        };
        provider.SelectedIndexChanged += (s, e) => { if (Provider == PrivateModelSettings.Compatible) showApi.Checked = true; };

        AddPage("密钥");
        key.Width = 195;
        AddRow("API 密钥", Line(key, removeKey), 63);
        AddRow("密钥状态", keyState, 50);
        AddPage("网络");
        AddRow("网络方式", network);
        AddRow("HTTP 代理地址", proxy);
        AddRow("代理用户名", proxyUser);
        AddRow("代理密码", proxyPassword);
        AddRow("认证管理", removeProxyAuth);
        AddRow("代理认证状态", proxyState, 55);

        AddPage("生成设置");
        thinking.Width = 122;
        AddRow("思考模式", Line(thinking, thinkingNote), 65);
        AddRow("思考强度", effort);
        AddRow("请求超时", Line(timeout, new Label { Text = "秒", AutoSize = true }));
        AddRow("输出上限", Line(output, new Label { Text = "Token（0 使用默认上限）", AutoSize = true }), 57);
        AddPage("角色与上下文");
        AddRow("上下文", Line(turns, new Label { Text = "轮（0 为停用）", AutoSize = true }));
        AddRow("角色名称", roleName);
        AddRow("角色设定", role, 96);
        AddRow("角色操作", Line(ButtonFor("填入示例", FillExample),
            ButtonFor("清空角色", () => { roleName.Clear(); role.Clear(); }),
            ButtonFor("清空聊天上下文…", clearContext)), 90);

        actions.Dock = DockStyle.Bottom;
        actions.AutoSize = true;
        var save = ButtonFor("加密保存", Save);
        LabTheme.StyleButton(save, LabTheme.Green);
        actions.Controls.AddRange(new[] { save, ButtonFor("设为当前", SelectCurrent),
            ButtonFor("轻量检查", async () => await Check("check")), ButtonFor("查询模型列表", async () => await Check("list")),
            ButtonFor("测试真实回答…", async () => await Check("answer")) });
        profileActions.Dock = DockStyle.Bottom;
        profileActions.AutoSize = true;
        profileActions.Controls.AddRange(new[] { ButtonFor("新增", StartNew), ButtonFor("复制…", Duplicate), ButtonFor("删除…", Remove) });
        var left = new Panel { Dock = DockStyle.Left, Width = 170, Padding = new Padding(8, 0, 8, 4), TabIndex = 0 };
        left.Controls.Add(profiles);
        left.Controls.Add(profileActions);
        var listLabel = LabTheme.Section("已保存配置");
        listLabel.Dock = DockStyle.Top;
        left.Controls.Add(listLabel);
        var right = new Panel { Dock = DockStyle.Fill, TabIndex = 1 };
        result.Dock = DockStyle.Bottom;
        result.Height = 61;
        right.Controls.Add(pages);
        right.Controls.Add(result);
        right.Controls.Add(actions);
        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(hint);
        Shown += (s, e) => {
            var area = Screen.FromControl(this).WorkingArea;
            if (Width > area.Width || Height > area.Height)
                Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        };

        identify.Click += (s, e) => Recognize();
        provider.SelectedIndexChanged += (s, e) => { if (!populating) ApplyPreset(); };
        protocol.SelectedIndexChanged += (s, e) => { if (!populating) UpdateAddress(); };
        model.TextChanged += (s, e) => UpdateThinking();
        endpoint.TextChanged += (s, e) => { if (!populating) UpdateAddress(); };
        thinking.SelectedIndexChanged += (s, e) => { if (!populating) UpdateThinking(); };
        network.SelectedIndexChanged += (s, e) => UpdateProxyState();
        proxy.TextChanged += (s, e) => UpdateProxyState();
        removeProxyAuth.CheckedChanged += (s, e) => UpdateProxyState();
        removeKey.CheckedChanged += (s, e) => UpdateKeyState();
        profiles.SelectedIndexChanged += (s, e) => {
            if (!populating && profiles.SelectedItem is PrivateModelProfile) ShowProfile((PrivateModelProfile)profiles.SelectedItem);
        };
        FormClosing += (s, e) => { if (active != null) active.Cancel(); };
        FormClosed += (s, e) => { tips.Dispose(); dialogIcon.Dispose(); };
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

    private void AddPage(string title)
    {
        var content = new TabPage(title) { AutoScroll = true };
        if (!SystemInformation.HighContrast) content.BackColor = Color.White;
        editor = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12), ColumnCount = 2 };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(editor);
        pages.TabPages.Add(content);
    }

    private void AddRow(string title, Control field, int height = 37)
    {
        var row = editor.RowCount++;
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        var label = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left };
        editor.Controls.Add(label, 0, row);
        field.Dock = DockStyle.Fill;
        if (String.IsNullOrEmpty(field.AccessibleName)) field.AccessibleName = title;
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
        provider.SelectedItem = ModelCapabilities.Preset(values.Provider) ?? ModelCapabilities.Presets.Last();
        SetProtocols(values.Protocol);
        endpoint.Text = values.Endpoint;
        model.Text = values.Model;
        key.Clear();
        removeKey.Checked = false;
        roleName.Text = values.RoleName;
        role.Text = values.RolePrompt;
        timeout.Value = Math.Max(1, Math.Min(120, values.TimeoutSeconds));
        turns.Value = Math.Max(0, Math.Min(8, values.ContextTurns));
        output.Value = Math.Max(0, Math.Min(32768, values.OutputTokens));
        network.SelectedIndex = values.Network == ModelNetwork.Proxy ? 2 : values.Network == ModelNetwork.Direct ? 1 : 0;
        proxy.Text = values.ProxyAddress;
        proxyUser.Clear();
        proxyPassword.Clear();
        removeProxyAuth.Checked = false;
        populating = false;
        UpdateAddress(values.ThinkingMode, values.ThinkingEffort);
        UpdateProxyState();
        if (profile != null)
            result.Text = (catalog.ActiveId == profile.Id ? "当前配置 · " : "已保存 · ") + profile.Availability();
    }

    private sealed class Option
    {
        internal readonly string Id, Label;
        internal Option(string id, string label) { Id = id; Label = label; }
        public override string ToString() { return Label; }
    }

    private static string Selected(ComboBox control)
    {
        var option = control.SelectedItem as Option;
        return option == null ? "default" : option.Id;
    }

    private static void Options(ComboBox control, Option[] choices, string value)
    {
        control.Items.Clear();
        control.Items.AddRange(choices);
        control.SelectedItem = choices.FirstOrDefault(option => option.Id == value) ?? choices[0];
    }

    private void SetProtocols(string selected)
    {
        var protocols = ModelCapabilities.Protocols(Provider);
        protocol.Items.Clear();
        protocol.Items.AddRange(protocols.Select(ModelCapabilities.ProtocolLabel).ToArray());
        protocol.SelectedIndex = selected == ModelCapabilities.Responses && protocols.Length == 2 ? 1 : 0;
    }

    private void ApplyPreset()
    {
        var preset = ModelCapabilities.Preset(Provider);
        populating = true;
        SetProtocols(preset.Protocol);
        if (preset.Base.Length != 0 && (endpoint.Text.Trim().Length == 0 ||
            ModelCapabilities.Recognize(endpoint.Text.Trim()).Suggestion != preset.Name))
        {
            endpoint.Text = preset.Base;
            model.Text = preset.Name == PrivateModelSettings.DeepSeek ? "deepseek-flash" : "";
            key.Clear();
        }
        populating = false;
        UpdateAddress("default", "default");
    }

    private void UpdateAddress(string mode = null, string strength = null)
    {
        var draft = ReadFields();
        try { resolved.Text = ModelCapabilities.Resolve(draft).AbsoluteUri; }
        catch (ModelReplyException error) { resolved.Text = error.Message; }
        tips.SetToolTip(resolved, resolved.Text);
        modelHint.Text = ModelCapabilities.Preset(Provider).Hint;
        tips.SetToolTip(modelHint, modelHint.Text);
        UpdateThinking(mode, strength);
        UpdateKeyState();
    }

    private void UpdateThinking(string mode = null, string strength = null)
    {
        if (populating) return;
        var options = ModelCapabilities.Thinking(ReadFields());
        mode = mode ?? Selected(thinking);
        strength = strength ?? Selected(effort);
        var modes = new System.Collections.Generic.List<Option> { new Option("default", "模型默认") };
        if (options.On != null) modes.Add(new Option("enabled", "开启"));
        if (options.Off != null) modes.Add(new Option("disabled", "关闭"));
        populating = true;
        Options(thinking, modes.ToArray(), mode);
        thinking.Enabled = modes.Count > 1;
        var ability = options.Switch != null || options.Efforts.Length > 0;
        thinkingNote.Text = !ability ? "当前接口或模型的思考参数未确认" :
            !thinking.Enabled ? "此模型仅支持调整强度" : "按当前模型和协议的选项发送";
        effort.Enabled = options.Efforts.Length > 0 && Selected(thinking) != "disabled" &&
            (options.On == null || Selected(thinking) == "enabled");
        var efforts = new[] { new Option("default", "模型默认强度") }.Concat(
            effort.Enabled ? options.Efforts.Select(value => new Option(value, value)) : Enumerable.Empty<Option>());
        Options(effort, efforts.ToArray(), strength);
        populating = false;
    }

    private void UpdateKeyState()
    {
        var hasKey = editing != null && !String.IsNullOrEmpty(editing.Settings.ApiKey);
        var same = hasKey && ModelCapabilities.SameOrigin(editing.Settings.Endpoint, endpoint.Text.Trim());
        keyState.Text = removeKey.Checked ? "保存时清除" : same ? "已保存 ••••••••　留空保留" :
            hasKey ? "服务地址来源变化，请为新来源填写密钥" : "尚未保存密钥";
        key.Enabled = !removeKey.Checked;
    }

    private void UpdateProxyState()
    {
        if (populating) return;
        var enabled = Network == ModelNetwork.Proxy;
        proxy.Enabled = removeProxyAuth.Enabled = enabled;
        proxyUser.Enabled = proxyPassword.Enabled = enabled && !removeProxyAuth.Checked;
        var saved = editing != null && !String.IsNullOrEmpty(editing.Settings.ProxyUser) &&
            ModelNetwork.SameProxy(editing.Settings.ProxyAddress, proxy.Text.Trim());
        proxyState.Text = !enabled ? "仅影响当前模型配置。系统和 QQ 网络设置保持原样。" :
            removeProxyAuth.Checked ? "保存时清除代理用户名和密码。" :
            saved ? "已保存 ••••••••　留空保留同一代理的认证" : "可选代理认证。用户名和密码都会加密保存。";
    }

    private PrivateModelSettings ReadFields()
    {
        var address = endpoint.Text.Trim();
        var apiKey = key.Text.Trim();
        if (apiKey.Length == 0 && editing != null && ModelCapabilities.SameOrigin(editing.Settings.Endpoint, address))
            apiKey = editing.Settings.ApiKey;
        var draft = new PrivateModelSettings { Provider = Provider, Endpoint = address, Protocol = Protocol, Network = Network,
            Model = model.Text.Trim(), ApiKey = removeKey.Checked ? "" : apiKey, RoleName = roleName.Text.Trim(), RolePrompt = role.Text.Trim(),
            TimeoutSeconds = (int)timeout.Value, ContextTurns = (int)turns.Value, OutputTokens = (int)output.Value,
            ThinkingMode = Selected(thinking), ThinkingEffort = Selected(effort), ProxyAddress = proxy.Text.Trim() };
        if (removeProxyAuth.Checked) return draft;
        var prior = editing != null && ModelNetwork.SameProxy(editing.Settings.ProxyAddress, draft.ProxyAddress) ? editing.Settings : null;
        draft.ProxyUser = String.IsNullOrEmpty(proxyUser.Text) && prior != null ? prior.ProxyUser : proxyUser.Text;
        var preservePassword = prior != null && draft.ProxyUser == prior.ProxyUser && String.IsNullOrEmpty(proxyPassword.Text);
        draft.ProxyPassword = preservePassword ? prior.ProxyPassword : proxyPassword.Text;
        return draft;
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
        var answer = MessageBox.Show(this, "复制选中的已保存配置。是否一并复制已加密保存的 API 密钥和代理认证？",
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
            result.Text = answer == DialogResult.Yes ? "已复制并加密保存凭据。" : "已复制配置，副本的密钥和代理认证为空。";
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
        if (found.Suggestion != null && (found.Suggestion != Provider || found.Protocol != null && found.Protocol != Protocol) &&
            MessageBox.Show(this, found.Message + "\r\n在编辑区采用建议？API 地址保持原样。",
                "识别服务商", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
        {
            var previousProtocol = Protocol;
            populating = true;
            provider.SelectedItem = ModelCapabilities.Preset(found.Suggestion);
            SetProtocols(found.Protocol ?? previousProtocol);
            populating = false;
            UpdateAddress();
        }
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

    private async Task Check(string operation)
    {
        bool completion = operation == "answer";
        PrivateModelSettings draft;
        Uri destination;
        try
        {
            draft = ReadFields();
            destination = draft.Validate(true, completion);
            if (operation == "list" || !completion && draft.Provider != PrivateModelSettings.DeepSeek)
            {
                destination = ModelCapabilities.ModelsAddress(destination, draft.Provider);
                if (destination == null) throw new ModelReplyException("models-unsupported",
                    "当前地址的官方列表暂不可用，请手工填模型 ID。已配置型号可单独测试真实回答。");
            }
        }
        catch (Exception error) { result.Text = DisplayError(error); return; }
        var purpose = completion ? "将发送固定测试句并请求生成一个回答，可能计费。" :
            "将向同一服务地址查询鉴权或模型列表，无生成请求。";
        if (MessageBox.Show(this, purpose + "\r\n目标服务「" + destination.Authority + "」，网络方式「" +
            ModelNetwork.Label(draft.Network) + "」。模型密钥仅用于该服务地址。",
            "模型检查", MessageBoxButtons.OKCancel, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        using (var cancellation = new CancellationTokenSource())
        {
            active = cancellation;
            SetBusy(true);
            result.Text = "正在检查已配置地址…";
            try
            {
                using (var reply = new HttpPrivateReply(draft, () => new PrivateModelTurn[0], log, listOnly: !completion))
                {
                    string kind;
                    if (completion) { await reply.TestAsync(cancellation.Token); kind = "completion"; }
                    else if (operation == "list")
                    {
                        var ids = await reply.ListAsync(cancellation.Token);
                        if (!IsDisposed) ChooseModel(ids);
                        return;
                    }
                    else kind = await reply.ProbeAsync(cancellation.Token);
                    if (!IsDisposed) RecordProbe(draft, kind, ProbeDescription(kind));
                }
            }
            catch (OperationCanceledException) { if (!IsDisposed) result.Text = "模型检查已取消。"; }
            catch (Exception error) { if (!IsDisposed) RecordProbe(draft, "failed", "模型检查未通过 · " + DisplayError(error)); }
            finally { active = null; if (!IsDisposed) SetBusy(false); }
        }
    }

    private void ChooseModel(string[] ids)
    {
        result.Text = ids.Length == 0 ? "列表为空，仍可手工填入模型 ID。" :
            "读取到 " + ids.Length + " 个模型。列表不代表账号可生成，可手工填写或单独测试。";
        if (ids.Length == 0) return;
        using (var dialog = new Form { Text = "选择返回的模型 ID", StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(495, 335), MinimizeBox = false, MaximizeBox = false })
        using (var choices = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true })
        {
            var use = new Button { Text = "填入所选模型", Dock = DockStyle.Bottom, DialogResult = DialogResult.OK, Enabled = false };
            choices.Items.AddRange(ids);
            choices.SelectedIndexChanged += (s, e) => use.Enabled = choices.SelectedItem != null;
            dialog.Controls.Add(choices);
            dialog.Controls.Add(use);
            dialog.AcceptButton = use;
            if (dialog.ShowDialog(this) == DialogResult.OK && choices.SelectedItem != null)
                model.Text = (string)choices.SelectedItem;
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
            a.RolePrompt == b.RolePrompt && a.TimeoutSeconds == b.TimeoutSeconds && a.ContextTurns == b.ContextTurns &&
            a.Protocol == b.Protocol && a.OutputTokens == b.OutputTokens && a.Network == b.Network &&
            a.ProxyAddress == b.ProxyAddress && a.ProxyUser == b.ProxyUser && a.ProxyPassword == b.ProxyPassword;
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
        pages.Enabled = actions.Enabled = profiles.Enabled = profileActions.Enabled = !busy;
    }

    internal static string DisplayError(Exception error)
    {
        if (error is PrivateRouteLostException || error is PrivateProfileBusyException) return error.Message;
        var modelError = error as ModelReplyException;
        return modelError == null ? Experiment.Failure(error) : modelError.Message;
    }
}
