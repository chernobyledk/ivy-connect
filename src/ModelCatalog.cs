using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Serialization;

public sealed class PrivateModelProfile
{
    public string Id, Name, VerifiedKind, VerifiedUtc;
    public PrivateModelSettings Settings;

    internal PrivateModelProfile CopyAs(string name, bool includeKey)
    {
        var cloned = Settings.Copy();
        if (!includeKey) cloned.ApiKey = "";
        return New(name, cloned);
    }

    internal static PrivateModelProfile New(string name, PrivateModelSettings settings)
    {
        return new PrivateModelProfile { Id = Guid.NewGuid().ToString("N"), Name = name, Settings = settings };
    }

    internal string Availability()
    {
        try { Settings.Validate(); }
        catch (ModelReplyException e) { return e.Code == "key" ? "缺少可用密钥" : "配置须修正"; }
        if (VerifiedKind == "completion") return "最近真实模型测试通过";
        if (VerifiedKind == "auth-ready") return "最近鉴权通过，模型待测";
        if (VerifiedKind == "auth-no-balance") return "最近鉴权通过，可用额度未通过";
        if (VerifiedKind == "models") return "模型列表已响应，密钥及模型待测";
        if (VerifiedKind == "failed") return "最近网络检查未通过";
        return "本地检查通过，尚未联网验证";
    }

    public override string ToString() { return Name; }
}

public sealed class PrivateModelCatalog
{
    public int Schema = 1;
    public string ActiveId;
    public PrivateModelProfile[] Profiles = new PrivateModelProfile[0];

    internal PrivateModelProfile Active { get { return Profiles.FirstOrDefault(item => item.Id == ActiveId); } }

    internal void Put(PrivateModelProfile profile)
    {
        if (String.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 60)
            throw new ModelReplyException("name", "请输入 1–60 字的配置名称");
        if (Profiles.Any(item => item.Id != profile.Id && String.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            throw new ModelReplyException("name", "此名称已存在，请换一个配置名称");
        profile.Settings.Validate(false);
        int index = Array.FindIndex(Profiles, item => item.Id == profile.Id);
        if (index < 0) Profiles = Profiles.Concat(new[] { profile }).ToArray();
        else Profiles[index] = profile;
    }

    internal void Select(string id)
    {
        var profile = Profiles.SingleOrDefault(item => item.Id == id);
        if (profile == null) throw new ModelReplyException("profile", "此配置已不存在");
        profile.Settings.Validate();
        ActiveId = profile.Id;
    }

    internal void Remove(string id)
    {
        Profiles = Profiles.Where(item => item.Id != id).ToArray();
        if (ActiveId == id) ActiveId = null;
    }
}

internal static class ModelCatalogFile
{
    internal const string FileName = "model-profiles.dpapi";
    internal const string LegacyFileName = "model-settings.dpapi";
    private static readonly XmlSerializer Codec = new XmlSerializer(typeof(PrivateModelCatalog));

    internal static PrivateModelCatalog Load(string folder)
    {
        return WithLease(folder, () => {
            var path = Path.Combine(folder, FileName);
            if (File.Exists(path))
            {
                var catalog = ModelSettingsFile.ReadEncrypted<PrivateModelCatalog>(path, Codec);
                if (catalog == null || catalog.Schema != 1 || catalog.Profiles == null ||
                    catalog.Profiles.Any(profile => profile == null || profile.Settings == null || String.IsNullOrEmpty(profile.Id)) ||
                    catalog.Profiles.Select(profile => profile.Id).Distinct().Count() != catalog.Profiles.Length ||
                    (!String.IsNullOrEmpty(catalog.ActiveId) && catalog.Active == null))
                    throw new InvalidDataException("模型配置库结构有误");
                return catalog;
            }
            var legacyPath = Path.Combine(folder, LegacyFileName);
            if (!File.Exists(legacyPath)) return new PrivateModelCatalog();
            var prior = ModelSettingsFile.Load(legacyPath);
            var recognition = ModelCapabilities.Recognize(prior.Endpoint);
            if (recognition.Suggestion != null) prior.Provider = recognition.Suggestion;
            if (ModelCapabilities.SupportsThinking(prior)) prior.ThinkingMode = "disabled";
            var imported = PrivateModelProfile.New("默认配置", prior);
            var migrated = new PrivateModelCatalog { Profiles = new[] { imported }, ActiveId = imported.Id };
            ModelSettingsFile.WriteEncrypted(path, Codec, migrated);
            return migrated;
        });
    }

    internal static void Save(string folder, PrivateModelCatalog value)
    {
        WithLease(folder, () => {
            ModelSettingsFile.WriteEncrypted(Path.Combine(folder, FileName), Codec, value);
            return true;
        });
    }

    private static T WithLease<T>(string folder, Func<T> action)
    {
        var name = "Local\\IvyConnect.ModelConfig." + MessageSnapshot.Hash(Path.GetFullPath(folder).ToUpperInvariant())
            .Replace('/', '_').Replace('=', '_');
        using (var lease = new Mutex(false, name))
        {
            bool entered;
            try { entered = lease.WaitOne(2500); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new ModelReplyException("profile", "模型配置正在保存，请稍后重试");
            try { return action(); }
            finally { lease.ReleaseMutex(); }
        }
    }
}
