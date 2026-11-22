using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

public sealed class PrivateModelSettings
{
    internal const string DeepSeek = "DeepSeek Chat Completions";
    internal const string DefaultEndpoint = "https://api.deepseek.com/chat/completions";
    public string Provider = DeepSeek, Endpoint = DefaultEndpoint, Model = "deepseek-flash";
    public string ApiKey = "", RolePrompt = "";
    public int TimeoutSeconds = 30, ContextTurns = 3;

    internal Uri Validate()
    {
        Uri uri;
        if (Provider != DeepSeek)
            throw new ModelReplyException("provider", "请选择 DeepSeek Chat Completions");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ModelReplyException("endpoint", "请填写完整 HTTPS /chat/completions 地址。本机模拟允许使用 HTTP");
        if (!Regex.IsMatch(Model ?? "", "^[a-zA-Z0-9][a-zA-Z0-9._:/-]{0,127}$"))
            throw new ModelReplyException("model", "模型名称为空或包含不支持的字符");
        if (!Regex.IsMatch(ApiKey ?? "", @"^[\x21-\x7E]{1,512}$"))
            throw new ModelReplyException("key", "请在本机填写 API 密钥。密钥不得包含空白字符");
        if (TimeoutSeconds < 1 || TimeoutSeconds > 120 || ContextTurns < 0 || ContextTurns > 8 ||
            (RolePrompt ?? "").Length > 1200)
            throw new ModelReplyException("limits", "超时须为 1–120 秒，上下文 0–8 轮，角色设定最多 1200 字");
        return uri;
    }
}

internal static class ModelSettingsFile
{
    private static readonly XmlSerializer Codec = new XmlSerializer(typeof(PrivateModelSettings));

    internal static PrivateModelSettings Load(string path)
    {
        if (!File.Exists(path)) return new PrivateModelSettings();
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try { using (var stream = new MemoryStream(bytes)) return (PrivateModelSettings)Codec.Deserialize(stream); }
        finally { Array.Clear(bytes, 0, bytes.Length); }
    }

    internal static void Save(string path, PrivateModelSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        byte[] encrypted;
        using (var clear = new MemoryStream())
        {
            try
            {
                Codec.Serialize(clear, settings);
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
