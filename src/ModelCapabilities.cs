using System;
using System.Linq;

internal sealed class ModelRecognition
{
    internal readonly string Suggestion, Message;
    internal ModelRecognition(string suggestion, string message) { Suggestion = suggestion; Message = message; }
}

internal static class ModelCapabilities
{
    internal static readonly string[] Providers = { PrivateModelSettings.DeepSeek, PrivateModelSettings.OpenAi, PrivateModelSettings.Compatible };
    internal const string ChatSuffix = "/chat/completions";

    internal static bool TryEndpoint(string value, out Uri uri)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    internal static bool IsChat(Uri uri) { return uri.AbsolutePath.EndsWith(ChatSuffix, StringComparison.Ordinal); }
    internal static bool IsOfficialDeepSeek(Uri uri)
    {
        return uri.Scheme == "https" && uri.IsDefaultPort && uri.IdnHost == "api.deepseek.com" &&
            (uri.AbsolutePath == ChatSuffix || uri.AbsolutePath == "/v1" + ChatSuffix);
    }

    internal static bool IsOfficialOpenAi(Uri uri)
    {
        return uri.Scheme == "https" && uri.IsDefaultPort && uri.IdnHost == "api.openai.com" && uri.AbsolutePath == "/v1" + ChatSuffix;
    }

    internal static ModelRecognition Recognize(string address)
    {
        Uri uri;
        if (!TryEndpoint(address, out uri))
            return new ModelRecognition(null, "服务商未确定。请填写完整 HTTPS 地址。地址、配置和密钥未发送。");
        if (IsOfficialDeepSeek(uri))
            return new ModelRecognition(PrivateModelSettings.DeepSeek, "地址为 DeepSeek 官方主机，路径为 Chat Completions。密钥尚未验证。");
        if (IsOfficialOpenAi(uri))
            return new ModelRecognition(PrivateModelSettings.OpenAi, "地址为 OpenAI 官方主机，路径为 Chat Completions。密钥尚未验证。");
        if (uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal) ||
            uri.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal) || uri.AbsolutePath.Contains(":generateContent"))
            return new ModelRecognition(null, "该路径看起来使用其他原生协议。本版尚未实现 Messages、Responses 或 generateContent，请手动填写受支持的 Chat Completions 地址。");
        return IsChat(uri) ? new ModelRecognition(PrivateModelSettings.Compatible,
            "路径看起来兼容 Chat Completions。服务商及真实上游未确定，接口和密钥仍需分别验证。") :
            new ModelRecognition(null, "地址未提供可确认的接口路径，保留手动设置。密钥尚未验证。");
    }

    internal static bool SupportsThinking(PrivateModelSettings settings, Uri uri)
    {
        return settings.Provider == PrivateModelSettings.DeepSeek && (IsOfficialDeepSeek(uri) || uri.IsLoopback) &&
            (settings.Model == "deepseek-flash" || settings.Model == "deepseek-v4-pro" || settings.Model == "deepseek-v4-flash");
    }

    internal static bool SupportsThinking(PrivateModelSettings settings)
    {
        Uri uri;
        return TryEndpoint(settings.Endpoint, out uri) && IsChat(uri) && SupportsThinking(settings, uri);
    }

    internal static void ValidateThinking(PrivateModelSettings settings, Uri uri)
    {
        var mode = settings.ThinkingMode;
        var effort = settings.ThinkingEffort;
        if ((mode != "default" && mode != "enabled" && mode != "disabled") ||
            (effort != "default" && effort != "low" && effort != "high" && effort != "max") ||
            (effort != "default" && mode != "enabled"))
            throw new ModelReplyException("thinking-options", "请先选择开启思考，再选择该模型支持的强度");
        if (mode != "default" && !SupportsThinking(settings, uri))
            throw new ModelReplyException("thinking-unsupported", "当前地址或模型的思考参数尚未验证，请使用模型默认设置");
    }

    internal static string DescribeThinking(PrivateModelSettings settings)
    {
        if (settings.ThinkingMode == "disabled") return "关闭";
        if (settings.ThinkingMode == "enabled") return "开启 / " + (settings.ThinkingEffort == "default" ? "模型默认强度" : settings.ThinkingEffort);
        return SupportsThinking(settings) ? "模型默认" : "模型默认 / 参数支持未确认";
    }

    internal static bool SameOrigin(string first, string second)
    {
        Uri a, b;
        return TryEndpoint(first, out a) && TryEndpoint(second, out b) &&
            a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
    }

    internal static Uri ProbeAddress(Uri endpoint)
    {
        var route = IsOfficialDeepSeek(endpoint) ? "/user/balance" :
            endpoint.AbsolutePath.Substring(0, endpoint.AbsolutePath.Length - ChatSuffix.Length) + "/models";
        return new UriBuilder(endpoint) { Path = route }.Uri;
    }
}
