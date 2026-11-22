using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

internal sealed class ModelRecognition
{
    internal readonly string Suggestion, Protocol, Message;
    internal ModelRecognition(string suggestion, string protocol, string message)
    { Suggestion = suggestion; Protocol = protocol; Message = message; }
}

internal sealed class ModelPreset
{
    internal readonly string Name, Label, Base, Protocol, Hint, Budget;
    internal ModelPreset(string name, string label, string address, string protocol, string budget, string hint)
    { Name = name; Label = label; Base = address; Protocol = protocol; Budget = budget; Hint = hint; }
    public override string ToString() { return Label; }
}

internal sealed class ThinkingOptions
{
    internal readonly string Switch, On, Off;
    internal readonly string[] Efforts;
    internal ThinkingOptions(string parameter = null, string on = null, string off = null, params string[] efforts)
    { Switch = parameter; On = on; Off = off; Efforts = efforts; }
}

internal static class ModelCapabilities
{
    internal const string Chat = "chat", Responses = "responses";
    internal const string ChatSuffix = "/chat/completions", ResponseSuffix = "/responses";
    internal const string Qwen = "DashScope", Kimi = "Moonshot", Glm = "Zhipu", Ark = "Volcengine",
        MiniMax = "MiniMax", Gemini = "Gemini", Grok = "xAI";
    internal static readonly ModelPreset[] Presets = {
        new ModelPreset(PrivateModelSettings.DeepSeek, "DeepSeek", "https://api.deepseek.com", Chat, "max_tokens", "填写 DeepSeek 控制台提供的模型 ID。"),
        new ModelPreset(Qwen, "通义千问 / 阿里云百炼", "https://dashscope.aliyuncs.com/compatible-mode/v1", Chat, "max_tokens", "使用与 API 密钥同地域的地址。已开通的模型 ID 请查百炼控制台。"),
        new ModelPreset(Kimi, "Kimi / 月之暗面", "https://api.moonshot.cn/v1", Chat, "max_completion_tokens", "填写 Kimi API 控制台的模型 ID。要求回传完整思考记录的型号仅开放单轮。"),
        new ModelPreset(Glm, "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", Chat, "max_tokens", "填写智谱开放平台的模型 ID。"),
        new ModelPreset(Ark, "豆包 / 火山方舟", "https://ark.cn-beijing.volces.com/api/v3", Chat, "max_completion_tokens", "填写已开通的官方 Model ID，或控制台推理接入点 ID（ep-…）。"),
        new ModelPreset(MiniMax, "MiniMax", "https://api.minimax.cn/v1", Chat, "max_completion_tokens", "中国站使用 minimax.cn，国际站使用 minimax.io。填写所在站点提供的模型 ID。"),
        new ModelPreset(PrivateModelSettings.OpenAi, "OpenAI GPT", "https://api.openai.com/v1", Responses, "max_completion_tokens", "填写 API 控制台可用的模型 ID。网页订阅与 API 权限独立。"),
        new ModelPreset(Gemini, "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", Chat, "max_tokens", "使用 Gemini API 密钥与 Google 的 OpenAI 兼容地址。"),
        new ModelPreset(Grok, "xAI Grok", "https://api.x.ai/v1", Responses, "max_tokens", "优先使用 Responses。使用 xAI API 密钥与账号可用的模型 ID。"),
        new ModelPreset(PrivateModelSettings.Compatible, "自定义兼容服务", "", Chat, "max_tokens", "按网关文档选择协议并填写 API 地址。厂商和思考能力保持未确定。")
    };

    internal static ModelPreset Preset(string name) { return Presets.FirstOrDefault(p => p.Name == name); }

    internal static bool TryEndpoint(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private static string WireProtocol(string path)
    {
        if (path.EndsWith(ChatSuffix, StringComparison.Ordinal)) return Chat;
        if (path.EndsWith(ResponseSuffix, StringComparison.Ordinal)) return Responses;
        return null;
    }

    internal static bool IsOfficialDeepSeek(Uri uri) { return Official(uri) == PrivateModelSettings.DeepSeek; }

    private static string Official(Uri uri)
    {
        if (uri.Scheme != "https" || !uri.IsDefaultPort) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        var suffix = WireProtocol(path);
        if (suffix != null) path = path.Substring(0, path.Length - (suffix == Chat ? ChatSuffix.Length : ResponseSuffix.Length));
        var host = uri.IdnHost;
        if (host == "api.deepseek.com" && (path == "" || path == "/v1")) return PrivateModelSettings.DeepSeek;
        if (host == "api.openai.com" && path == "/v1") return PrivateModelSettings.OpenAi;
        if ((host == "api.moonshot.cn" || host == "api.moonshot.ai") && path == "/v1") return Kimi;
        if (host == "open.bigmodel.cn" && path == "/api/paas/v4") return Glm;
        if (host == "ark.cn-beijing.volces.com" && path == "/api/v3") return Ark;
        if ((host == "api.minimax.cn" || host == "api.minimax.io" || host == "api.minimax.chat") && path == "/v1") return MiniMax;
        if (host == "generativelanguage.googleapis.com" && path == "/v1beta/openai") return Gemini;
        if ((host == "api.x.ai" || host == "us.api.x.ai") && path == "/v1") return Grok;
        if (path == "/compatible-mode/v1" && (host == "dashscope.aliyuncs.com" ||
            host == "dashscope-intl.aliyuncs.com" || host == "dashscope-us.aliyuncs.com" || IsDashScopeWorkspace(host)))
            return Qwen;
        if (host == "coding.dashscope.aliyuncs.com" && path == "/v1") return Qwen;
        return null;
    }

    private static bool IsDashScopeWorkspace(string host)
    {
        return Regex.IsMatch(host,
            @"^[a-z0-9-]+\.(cn-beijing|cn-hongkong|ap-southeast-1|ap-northeast-1|eu-central-1|us-east-1)\.maas\.aliyuncs\.com$");
    }

    internal static string[] Protocols(string name)
    {
        return name == PrivateModelSettings.OpenAi || name == Grok || name == Qwen ||
            name == PrivateModelSettings.DeepSeek || name == PrivateModelSettings.Compatible
            ? new[] { Chat, Responses } : new[] { Chat };
    }

    internal static string ProtocolLabel(string protocol) { return protocol == Responses ? "Responses" : "Chat Completions"; }

    internal static Uri Resolve(PrivateModelSettings settings)
    {
        Uri uri;
        if (!TryEndpoint(settings.Endpoint, out uri))
            throw new ModelReplyException("endpoint", "请输入 HTTPS 基础地址或完整请求地址，不带凭据、查询参数或片段。本机模拟允许 HTTP。");
        var preset = Preset(settings.Provider);
        if (preset == null || !Protocols(settings.Provider).Contains(settings.Protocol))
            throw new ModelReplyException("provider", "当前服务商和接口组合未实现");
        string path = uri.AbsolutePath.TrimEnd('/');
        var existing = WireProtocol(path);
        if (existing != null && existing != settings.Protocol)
            throw new ModelReplyException("endpoint", "完整请求地址与接口类型不一致。请更新地址或选择匹配的协议。");
        if (existing == null && (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || path.Contains(":generateContent")))
            throw new ModelReplyException("endpoint", "此原生接口路径不受支持，请使用兼容地址。");
        if (existing == null)
        {
            Uri canonical;
            if (path == "" && Uri.TryCreate(preset.Base, UriKind.Absolute, out canonical) &&
                canonical.IdnHost == uri.IdnHost) path = canonical.AbsolutePath.TrimEnd('/');
            path += settings.Protocol == Responses ? ResponseSuffix : ChatSuffix;
        }
        // A repeated wire route is almost always a pasted URL combined with a second route.
        var suffix = settings.Protocol == Responses ? ResponseSuffix : ChatSuffix;
        var prefix = path.Substring(0, path.Length - suffix.Length);
        if (WireProtocol(prefix) != null)
            throw new ModelReplyException("endpoint", "请求路径重复，请只填写一段完整地址或基础地址。");
        uri = new UriBuilder(uri) { Path = path }.Uri;
        if (!uri.IsLoopback && settings.Provider != PrivateModelSettings.Compatible && Official(uri) != settings.Provider)
            throw new ModelReplyException("provider", "地址与当前官方服务商不匹配。自定义域名或网关请选择「自定义兼容服务」。");
        return uri;
    }

    internal static ModelRecognition Recognize(string address)
    {
        Uri uri;
        if (!TryEndpoint(address, out uri))
            return new ModelRecognition(null, null, "服务商未确定。请填写有效的 HTTPS API 地址。未联网。");
        var path = uri.AbsolutePath.TrimEnd('/');
        var protocol = WireProtocol(path);
        var official = Official(uri);
        if (official == null && path == "" && uri.Scheme == "https" && uri.IsDefaultPort)
        {
            var preset = Presets.FirstOrDefault(p => p.Base.Length != 0 && new Uri(p.Base).IdnHost == uri.IdnHost);
            if (preset != null) official = preset.Name;
        }
        if (official != null && (protocol == null || Protocols(official).Contains(protocol)))
            return new ModelRecognition(official, protocol, "地址匹配「" + Preset(official).Label + "」的官方入口。" +
                (protocol == null ? "基础地址，协议请确认。" : "路径指示 " + ProtocolLabel(protocol) + "。") + " 密钥尚未验证。");
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || path.Contains(":generateContent"))
            return new ModelRecognition(null, null, "该原生协议路径未实现。请填写兼容接口地址。");
        return protocol == null
            ? new ModelRecognition(null, null, "地址无法确定厂商或接口，请按服务文档手动选择。未联网。")
            : new ModelRecognition(PrivateModelSettings.Compatible, protocol, "路径指示 " + ProtocolLabel(protocol) +
                "。真实上游、兼容性与密钥均未验证。");
    }

    internal static ThinkingOptions Thinking(PrivateModelSettings settings, Uri uri)
    {
        if (uri == null || (!uri.IsLoopback && Official(uri) != settings.Provider)) return new ThinkingOptions();
        var model = settings.Model ?? "";
        switch (settings.Provider)
        {
            case PrivateModelSettings.DeepSeek:
                if (new[] { "deepseek-flash", "deepseek-v4-pro", "deepseek-v4-flash" }.Contains(model))
                    return settings.Protocol == Responses
                        ? new ThinkingOptions("reasoning_effort", null, "none", "low", "high", "max")
                        : new ThinkingOptions("thinking", "enabled", "disabled", "low", "high", "max");
                break;
            case Qwen:
                if (settings.Protocol == Responses)
                {
                    if (model == "qwen4-max") return new ThinkingOptions("reasoning_effort", null, "none", "low", "high", "xhigh");
                    if (model == "qwen3.8-max" || model == "qwen3.8-flash")
                        return new ThinkingOptions("reasoning_effort", null, "none", "low", "medium", "xhigh");
                    break;
                }
                if (new[] { "qwen3.8-max", "qwen3.8-flash", "qwen3.7-plus", "qwen3.6-plus", "qwen3.5-plus" }.Contains(model))
                    return new ThinkingOptions("enable_thinking", "true", "false");
                break;
            case Kimi:
                if (model == "kimi-k3.1-pro" || model == "kimi-k3")
                    return new ThinkingOptions(null, null, null, "low", "high", "max");
                break;
            case Glm:
                if (model == "glm-5.3" || model == "glm-5.3-flash")
                    return new ThinkingOptions("thinking", "enabled", "disabled", "low", "high", "max");
                if (new[] { "glm-4.5", "glm-4.5-air", "glm-4.6", "glm-4.7", "glm-5", "glm-5.1" }.Contains(model))
                    return new ThinkingOptions("thinking", "enabled", "disabled");
                break;
            case Ark:
                if (model == "doubao-seed-2-1-pro-260915")
                    return new ThinkingOptions("thinking", "enabled", "disabled", "low", "high");
                if (new[] { "doubao-seed-2-1-pro-260628", "doubao-seed-2-0-lite-260428" }.Contains(model))
                    return new ThinkingOptions("thinking", "enabled", "disabled");
                break;
            case MiniMax:
                if (model == "MiniMax-M3.1-Flash")
                    return new ThinkingOptions(null, null, null, "low", "medium", "high", "xhigh", "max");
                if (model == "MiniMax-M3") return new ThinkingOptions("thinking", "adaptive", "disabled");
                break;
            case PrivateModelSettings.OpenAi:
                if (model == "gpt-6.2-astra" || model == "gpt-6.2-sol")
                    return new ThinkingOptions(null, null, null, "low", "medium", "high", "xhigh", "max");
                if (model == "gpt-6.1-terra" || model == "gpt-6-luna")
                    return new ThinkingOptions("reasoning_effort", null, "none", "low", "medium", "high", "xhigh", "max");
                break;
            case Gemini:
                if (model == "gemini-4-hydrogen" || model == "gemini-3-flash-preview")
                    return new ThinkingOptions(null, null, null, "low", "medium", "high");
                if (model == "gemini-2.5-pro") return new ThinkingOptions(null, null, null, "low", "medium", "high");
                if (new[] { "gemini-2.5-flash", "gemini-2.5-flash-lite" }.Contains(model))
                    return new ThinkingOptions("reasoning_effort", null, "none", "low", "medium", "high");
                break;
            case Grok:
                if (model == "grok-4.7" || model == "grok-4.6")
                    return new ThinkingOptions(null, null, null, "low", "medium", "high", "xhigh");
                if (model == "grok-4.5") return new ThinkingOptions(null, null, null, "low", "medium", "high");
                break;
        }
        return new ThinkingOptions();
    }

    internal static ThinkingOptions Thinking(PrivateModelSettings settings)
    {
        try { return Thinking(settings, Resolve(settings)); }
        catch (ModelReplyException) { return new ThinkingOptions(); }
    }

    internal static bool SupportsThinking(PrivateModelSettings settings)
    {
        var options = Thinking(settings);
        return options.Switch != null || options.Efforts.Length != 0;
    }

    internal static void ValidateThinking(PrivateModelSettings settings, Uri uri)
    {
        var mode = settings.ThinkingMode;
        var effort = settings.ThinkingEffort;
        if (mode != "default" && mode != "enabled" && mode != "disabled" || (effort != "default" && mode == "disabled"))
            throw new ModelReplyException("thinking-options", "请检查思考开关与强度组合");
        var options = Thinking(settings, uri);
        if (mode == "enabled" && options.On == null || mode == "disabled" && options.Off == null ||
            effort != "default" && !options.Efforts.Contains(effort) ||
            mode == "default" && options.On != null && effort != "default")
            throw new ModelReplyException("thinking-unsupported", "当前模型和协议不支持这个思考组合，请使用可选档位或模型默认值");
    }

    internal static void ApplyThinking(PrivateModelSettings settings, Uri uri, Dictionary<string, object> body)
    {
        var options = Thinking(settings, uri);
        var selected = settings.ThinkingMode == "enabled" ? options.On : settings.ThinkingMode == "disabled" ? options.Off : null;
        if (selected != null && options.Switch == "thinking") body["thinking"] = new { type = selected };
        if (selected != null && options.Switch == "enable_thinking") body["enable_thinking"] = selected == "true";
        var effort = options.Switch == "reasoning_effort" && selected != null ? selected : settings.ThinkingEffort;
        if (effort == "default") return;
        if (settings.Protocol == Responses) body["reasoning"] = new { effort = effort };
        else body["reasoning_effort"] = effort;
    }

    internal static string DescribeThinking(PrivateModelSettings settings)
    {
        if (settings.ThinkingMode == "disabled") return "关闭";
        if (settings.ThinkingEffort != "default") return settings.ThinkingEffort;
        if (settings.ThinkingMode == "enabled") return "开启 / 模型默认强度";
        return SupportsThinking(settings) ? "模型默认" : "模型默认 / 参数支持未确认";
    }

    internal static bool SameOrigin(string first, string second)
    {
        Uri a, b;
        return TryEndpoint(first, out a) && TryEndpoint(second, out b) &&
            a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    }

    internal static Uri ModelsAddress(Uri endpoint, string provider)
    {
        var official = Official(endpoint);
        if (provider != official) return null;
        string path;
        switch (official)
        {
            case PrivateModelSettings.DeepSeek: path = "/models"; break;
            case PrivateModelSettings.OpenAi: case Grok: case Kimi: path = "/v1/models"; break;
            case Gemini: path = "/v1beta/openai/models"; break;
            case Qwen:
                if (!IsDashScopeWorkspace(endpoint.IdnHost) && endpoint.IdnHost != "dashscope-us.aliyuncs.com") return null;
                path = "/api/v1/models"; break;
            default: return null;
        }
        return new UriBuilder(endpoint) { Path = path }.Uri;
    }
}
