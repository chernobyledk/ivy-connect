using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

public sealed class PrivateModelSettings
{
    internal const string DeepSeek = "DeepSeek Chat Completions";
    internal const string OpenAi = "OpenAI Chat Completions";
    internal const string Compatible = "自定义 Chat Completions";
    internal const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";
    public string Provider = DeepSeek, Endpoint = DefaultEndpoint, Model = "deepseek-flash";
    public string ApiKey = "", RolePrompt = "";
    public string RoleName = "", ThinkingMode = "default", ThinkingEffort = "default";
    public int TimeoutSeconds = 30, ContextTurns = 3;
    public string Protocol = ModelCapabilities.Chat, Network = ModelNetwork.System;
    public string ProxyAddress = "", ProxyUser = "", ProxyPassword = "";
    public int OutputTokens;

    internal PrivateModelSettings Copy() { return (PrivateModelSettings)MemberwiseClone(); }

    internal Uri Validate(bool requireKey = true, bool requireModel = true)
    {
        var uri = ModelCapabilities.Resolve(this);
        if (requireModel && !IsModelId(Model))
            throw new ModelReplyException("model", "模型名称为空或包含不支持的字符");
        if ((requireKey || !String.IsNullOrEmpty(ApiKey)) && !Regex.IsMatch(ApiKey ?? "", @"^[\x21-\x7E]{1,512}$"))
            throw new ModelReplyException("key", "请在本机填写 API 密钥。密钥不得包含空白字符");
        if (TimeoutSeconds < 1 || TimeoutSeconds > 120 || ContextTurns < 0 || ContextTurns > 8 || OutputTokens < 0 || OutputTokens > 32768 ||
            (RolePrompt ?? "").Length > 1200 || (RoleName ?? "").Length > 60)
            throw new ModelReplyException("limits", "请检查超时、输出长度、上下文轮数或角色长度");
        ModelNetwork.Validate(this);
        if (requireModel)
        {
            if (Provider == ModelCapabilities.Kimi && ContextTurns != 0 && (Model == "kimi-k3.1-pro" || Model == "kimi-k3"))
                throw new ModelReplyException("model-context", "此 Kimi 型号要求多轮回传完整思考记录。当前仅支持单轮，请将上下文设为 0 或换用其他型号。");
            ModelCapabilities.ValidateThinking(this, uri);
        }
        return uri;
    }

    internal static bool IsModelId(string value)
    { return Regex.IsMatch(value ?? "", @"\A[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}\z"); }
}

internal static class ModelSettingsFile
{
    private static readonly XmlSerializer Codec = new XmlSerializer(typeof(PrivateModelSettings));

    internal static PrivateModelSettings Load(string path)
    {
        if (!File.Exists(path)) return new PrivateModelSettings();
        return ReadEncrypted<PrivateModelSettings>(path, Codec);
    }

    internal static T ReadEncrypted<T>(string path, XmlSerializer codec)
    {
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { using (var stream = new MemoryStream(bytes)) return (T)codec.Deserialize(stream); }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }

    internal static void Save(string path, PrivateModelSettings settings)
    {
        settings.Validate();
        WriteEncrypted(path, Codec, settings);
    }

    internal static void WriteEncrypted(string path, XmlSerializer codec, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] encrypted;
        using (var clear = new MemoryStream())
        {
            try
            {
                codec.Serialize(clear, value);
                var bytes = clear.ToArray();
                try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
            }
            finally { Array.Clear(clear.GetBuffer(), 0, (int)clear.Length); }
        }
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".next";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(encrypted, 0, encrypted.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(staging, path, null);
            else File.Move(staging, path);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

}
