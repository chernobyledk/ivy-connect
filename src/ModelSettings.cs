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

    internal PrivateModelSettings Copy() { return (PrivateModelSettings)MemberwiseClone(); }

    internal Uri Validate(bool requireKey = true)
    {
        Uri uri;
        if (!ModelCapabilities.TryEndpoint(Endpoint, out uri) || !ModelCapabilities.IsChat(uri))
            throw new ModelReplyException("endpoint", "请填写完整 HTTPS /chat/completions 地址。本机模拟允许使用 HTTP");
        if (!ModelCapabilities.Providers.Contains(Provider) || (!uri.IsLoopback &&
            ((Provider == DeepSeek && !ModelCapabilities.IsOfficialDeepSeek(uri)) ||
             (Provider == OpenAi && !ModelCapabilities.IsOfficialOpenAi(uri)))))
            throw new ModelReplyException("provider", "地址与所选官方接口不一致。中转或其他兼容地址请选择自定义 Chat Completions");
        if (!Regex.IsMatch(Model ?? "", "^[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}$"))
            throw new ModelReplyException("model", "模型名称为空或包含不支持的字符");
        if ((requireKey || !String.IsNullOrEmpty(ApiKey)) && !Regex.IsMatch(ApiKey ?? "", @"^[\x21-\x7E]{1,512}$"))
            throw new ModelReplyException("key", "请在本机填写 API 密钥。密钥不得包含空白字符");
        if (TimeoutSeconds < 1 || TimeoutSeconds > 120 || ContextTurns < 0 || ContextTurns > 8 ||
            (RolePrompt ?? "").Length > 1200 || (RoleName ?? "").Length > 60)
            throw new ModelReplyException("limits", "超时须为 1–120 秒，上下文 0–8 轮，角色设定最多 1200 字");
        ModelCapabilities.ValidateThinking(this, uri);
        return uri;
    }
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
