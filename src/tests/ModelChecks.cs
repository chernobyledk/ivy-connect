using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class LoopbackDeepSeek : IDisposable
{
    internal const string Key = "test-only-placeholder-never-real";
    internal const string AlternateKey = "test-only-second-key-never-real";
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly Action<string> log;
    internal readonly ManualResetEvent Delayed = new ManualResetEvent(false);
    internal readonly ConcurrentQueue<string> ObservedRequests = new ConcurrentQueue<string>();
    internal Uri Endpoint { get; private set; }

    internal LoopbackDeepSeek(Action<string> log)
    {
        this.log = log;
        listener.Start();
        Endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/chat/completions");
        Task.Run(() => Accept());
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                var peer = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                ThreadPool.QueueUserWorkItem(state => Handle((TcpClient)state), peer);
            }
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private static void Write(NetworkStream stream, int status, string body)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: application/json\r\nContent-Length: " +
            payload.Length + "\r\nConnection: close\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(payload, 0, payload.Length);
    }

    private void Handle(TcpClient peer)
    {
        using (peer)
        try
        {
            var stream = peer.GetStream();
            stream.ReadTimeout = 6000;
            var headers = new StringBuilder();
            while (headers.Length < 16000)
            {
                int next = stream.ReadByte();
                if (next == -1) return;
                headers.Append((char)next);
                int n = headers.Length;
                if (n >= 4 && headers[n - 4] == '\r' && headers[n - 3] == '\n' && headers[n - 2] == '\r' && headers[n - 1] == '\n') break;
            }
            var lines = headers.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool first = lines.Any(line => line.Equals("Authorization: Bearer " + Key, StringComparison.OrdinalIgnoreCase));
            bool second = lines.Any(line => line.Equals("Authorization: Bearer " + AlternateKey, StringComparison.OrdinalIgnoreCase));
            if (!first && !second)
            {
                Write(stream, 401, "{}");
                return;
            }
            if (lines[0].StartsWith("GET /models "))
            {
                ObservedRequests.Enqueue("GET/models");
                Write(stream, 200, "{\"object\":\"list\",\"data\":[{\"id\":\"test-normal\"}]}");
                return;
            }
            if (!lines[0].StartsWith("POST /chat/completions ")) { Write(stream, 404, "{}"); return; }
            int length = Int32.Parse(lines.Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Substring("Content-Length:".Length));
            if (length < 1 || length > 65536) { Write(stream, 400, "{}"); return; }
            var body = new byte[length];
            for (int pos = 0; pos < length;)
            {
                int read = stream.Read(body, pos, length - pos);
                if (read == 0) return;
                pos += read;
            }
            var json = new JavaScriptSerializer();
            var request = (Dictionary<string, object>)json.DeserializeObject(Encoding.UTF8.GetString(body));
            var model = (string)request["model"];
            var messages = ((object[])request["messages"]).Cast<Dictionary<string, object>>().ToArray();
            var roles = String.Join(",", messages.Select(message => (string)message["role"]));
            if ((bool)request["stream"])
                throw new InvalidDataException("wrong contract");
            var thinkingMode = request.ContainsKey("thinking") ? (string)((Dictionary<string, object>)request["thinking"])["type"] : "omitted";
            var effort = request.ContainsKey("reasoning_effort") ? (string)request["reasoning_effort"] : "omitted";
            ObservedRequests.Enqueue(String.Join("/", model, first ? "primary" : "secondary", thinkingMode, effort,
                request.ContainsKey("max_completion_tokens") ? "new-budget" : "chat-budget"));
            log("mock request model=" + model + " roles=" + roles);
            if (model == "deepseek-v4-pro" && effort == "max") { Write(stream, 422, "{}"); return; }
            switch (model)
            {
                case "test-auth": Write(stream, 401, "{\"error\":\"hidden test error\"}"); return;
                case "test-limit": Write(stream, 429, "{}"); return;
                case "test-server": Write(stream, 503, "{}"); return;
                case "test-invalid": Write(stream, 200, "{ malformed"); return;
                case "test-delay":
                    Delayed.Set();
                    Thread.Sleep(2300);
                    break;
            }
            var answer = model == "test-empty" ? " " :
                model == "test-reason" ? "<think>private trace</think>answer" : "最终回答";
            if (model == "mock-qq")
            {
                var marker = ((string)messages.Last()["content"]).Split(' ')[1];
                if (roles == "system,user") answer = "模拟首轮确认 " + marker;
                else if (roles == "system,user,assistant,user" &&
                    (string)messages[2]["content"] == "模拟首轮确认 " + marker) answer = "模拟第二轮，已带入上一轮 " + marker;
                else { Write(stream, 400, "{}"); return; }
            }
            Write(stream, 200, json.Serialize(new { choices = new[] { new { finish_reason = "stop",
                message = new { role = "assistant", content = answer, reasoning_content = "hidden test trace" } } } }));
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { log("mock rejected unexpected request " + ex.GetType().Name); }
    }

    public void Dispose() { listener.Stop(); Delayed.Dispose(); }
}

internal static class ModelChecks
{
    private sealed class Wire : IPrivateTransport
    {
        internal readonly List<ReplyRow> Rows = new List<ReplyRow> {
            new ReplyRow("baseline-1", new[] { "first history" }, false),
            new ReplyRow("baseline-2", new[] { "second history" }, true)
        };
        internal int Sends;
        internal bool Invalid, Drop;
        public string Epoch { get { return "fake-qq"; } }
        public ReplyRow[] Read()
        {
            if (Invalid) throw new InvalidOperationException("fixture binding changed");
            return Rows.ToArray();
        }
        public void RequireReady() { Read(); }
        public void Submit(string text, Action verify, Action<Action> commit)
        {
            verify();
            commit(() => {
                Sends++;
                if (!Drop) Rows.Add(new ReplyRow("reply-" + Sends, new[] { text }, true));
            });
        }
        internal void Add(string key) { Rows.Add(new ReplyRow(key, new[] { "来自已绑定私聊" }, false)); }
    }

    private static void Assert(bool ok, string label)
    {
        if (!ok) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static PrivateModelSettings Settings(LoopbackDeepSeek server, string model, int seconds = 8)
    {
        return new PrivateModelSettings { Endpoint = server.Endpoint.ToString(), ApiKey = LoopbackDeepSeek.Key,
            Model = model, TimeoutSeconds = seconds, ContextTurns = 2, RolePrompt = "Use short answers." };
    }

    private static void Error(LoopbackDeepSeek server, string model, string expected, int seconds = 8)
    {
        using (var reply = new DeepSeekPrivateReply(Settings(server, model, seconds), () => new PrivateModelTurn[0], Ignore))
        {
            try { reply.TestAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (ModelReplyException ex) { Assert(ex.Code == expected, model + " -> " + expected); return; }
            throw new InvalidOperationException("expected error: " + model);
        }
    }

    private static void Ignore(string line) { }
    private static PrivateReplyCore Core(PrivateStore store, Wire wire, IPrivateReplyProvider reply, PrivateRunControl control)
    {
        store.State.Binding = new PrivateBindingProof { BotQq = "100000001", PeerQq = "100000002", Epoch = wire.Epoch };
        var core = new PrivateReplyCore(store, wire, reply, control, row => true, Ignore, 380);
        core.Start(store.State.Binding.BotQq, store.State.Binding.PeerQq);
        return core;
    }

    private static void Context(LoopbackDeepSeek server, string folder)
    {
        var wire = new Wire();
        var progress = new List<string>();
        using (var store = new PrivateStore(folder))
        using (var control = new PrivateRunControl())
        using (var model = new DeepSeekPrivateReply(Settings(server, "test-repeat"), () => store.State.ModelTurns, progress.Add))
        {
            var core = Core(store, wire, model, control);
            for (int i = 0; i < 3; i++)
            {
                wire.Add("in-" + i);
                core.Tick();
            }
            Assert(wire.Sends == 3 && wire.Rows.Count(row => row.Own == true && row.RawText == "最终回答") == 3,
                "equal assistant text is confirmed as three fresh outgoing occurrences; no tags or reasoning");
            Assert(store.State.ModelTurns.Length == 2 && progress.Any(s => s.Contains("messages=2")) &&
                progress.Any(s => s.Contains("messages=4")) && progress.Any(s => s.Contains("messages=6")),
                "bounded history sent in serial calls after confirmed QQ submissions");
            wire.Drop = true;
            wire.Add("dropped");
            try { core.Tick(); }
            catch (InvalidOperationException) { }
            Assert(store.State.Events.Last().Phase == PrivatePhase.Uncertain && store.State.ModelTurns.Length == 2,
                "unconfirmed output stays out of the context");
        }
        using (var reopened = new PrivateStore(folder))
        {
            Assert(reopened.State.ModelTurns.Length == 2, "context persisted through encrypted store reopen");
            reopened.ClearModelContext();
        }
        using (var reopened = new PrivateStore(folder))
            Assert(reopened.State.ModelTurns.Length == 0 && reopened.State.Events.Length == 4,
                "clear context persists without removing send ledger");
    }

    private static void Late(LoopbackDeepSeek server, string folder, string mode)
    {
        server.Delayed.Reset();
        var wire = new Wire();
        using (var store = new PrivateStore(folder))
        using (var control = new PrivateRunControl())
        using (var model = new DeepSeekPrivateReply(Settings(server, "test-delay"), () => store.State.ModelTurns, Ignore))
        {
            var core = Core(store, wire, model, control);
            wire.Add("late");
            var task = Task.Run(() => {
                try { core.Tick(); }
                catch (OperationCanceledException) { }
                catch (InvalidOperationException) { }
            });
            if (!server.Delayed.WaitOne(4500)) throw new InvalidOperationException("HTTP fixture did not get the delayed request");
            if (mode == "stop") control.Stop();
            else if (mode == "pause") { control.Pause(); control.Resume(); }
            else wire.Invalid = true;
            task.GetAwaiter().GetResult();
            Assert(wire.Sends == 0 && !store.State.ModelTurns.Any() &&
                store.State.Events.Single().Phase == (mode == "binding" ? PrivatePhase.Failed : PrivatePhase.Cancelled),
                mode + " invalidates pending HTTP result and prevents QQ write");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error)
        {
            for (var e = error; e != null; e = e.InnerException)
                Console.Error.WriteLine("FAIL " + e.GetType().Name + " " + e.Message);
            return 1;
        }
    }

    private static void Run(string[] args)
    {
        if (args.Length == 1 && args[0] == "--serve")
        {
            using (var fixture = new LoopbackDeepSeek(Console.WriteLine))
            {
                Console.WriteLine("READY " + fixture.Endpoint);
                Console.Out.Flush();
                Thread.Sleep(TimeSpan.FromMinutes(4));
            }
            return;
        }
        var ui = args.Length == 1 && args[0] == "--ui";
        if (args.Length != 0 && !ui) throw new ArgumentException("Expected no arguments, --serve or --ui");
        var temporary = Path.GetFullPath(Path.GetTempPath());
        var root = Path.GetFullPath(Path.Combine(temporary, "qq-model-check-" + Guid.NewGuid().ToString("N")));
        try
        {
            using (var server = new LoopbackDeepSeek(Console.WriteLine))
            {
                var settings = Settings(server, "test-normal");
                var file = Path.Combine(root, "model-settings.dpapi");
                ModelSettingsFile.Save(file, settings);
                if (ui) { CheckForm(root); return; }
                Assert(ModelSettingsFile.Load(file).ApiKey == LoopbackDeepSeek.Key &&
                    !Encoding.UTF8.GetString(File.ReadAllBytes(file)).Contains(LoopbackDeepSeek.Key), "model key DPAPI roundtrip");
                using (var model = new DeepSeekPrivateReply(settings, () => { throw new Exception("unexpected private context"); }, Ignore))
                    Assert(model.TestAsync(CancellationToken.None).GetAwaiter().GetResult() == "最终回答",
                        "connection probe uses no private context and only consumes final answer");
                Error(server, "test-auth", "auth");
                Error(server, "test-limit", "rate-limit");
                Error(server, "test-server", "http-503");
                Error(server, "test-invalid", "format");
                Error(server, "test-empty", "empty");
                Error(server, "test-reason", "thinking");
                Error(server, "test-delay", "timeout", 1);
                Context(server, Path.Combine(root, "context"));
                foreach (var mode in new[] { "stop", "pause", "binding" }) Late(server, Path.Combine(root, mode), mode);
                Catalog(server, root, file);
            }
        }
        finally
        {
            if (Path.GetDirectoryName(root).TrimEnd(Path.DirectorySeparatorChar) ==
                temporary.TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Catalog(LoopbackDeepSeek server, string folder, string legacyPath)
    {
        var original = File.ReadAllBytes(legacyPath);
        var catalog = ModelCatalogFile.Load(folder);
        Assert(catalog.Profiles.Length == 1 && catalog.Active.Name == "默认配置" && catalog.Active.Settings.ApiKey == LoopbackDeepSeek.Key &&
            original.SequenceEqual(File.ReadAllBytes(legacyPath)), "legacy settings migrated once without modifying original file");
        var first = catalog.Active;
        first.Settings = Settings(server, "test-delay");
        first.Settings.TimeoutSeconds = 8;
        var second = first.CopyAs("备用", false);
        Assert(second.Settings.ApiKey.Length == 0 && first.CopyAs("保留密钥", true).Settings.ApiKey == LoopbackDeepSeek.Key,
            "copy can deliberately retain or exclude a key");
        second.Settings.ApiKey = LoopbackDeepSeek.AlternateKey;
        second.Settings.Model = "test-secondary";
        catalog.Put(second);
        ModelCatalogFile.Save(folder, catalog);
        var both = ModelCatalogFile.Load(folder);
        Assert(both.Profiles.Length == 2 && both.Active.Id == first.Id &&
            both.Profiles.Single(item => item.Id == first.Id).Settings.ApiKey == LoopbackDeepSeek.Key &&
            both.Profiles.Single(item => item.Id == second.Id).Settings.ApiKey == LoopbackDeepSeek.AlternateKey,
            "two independently encrypted credentials reload with the manual selection intact");
        var delayedSettings = first.Settings;
        using (var running = new DeepSeekPrivateReply(delayedSettings, () => new PrivateModelTurn[0], Ignore))
        {
            server.Delayed.Reset();
            var pending = running.TestAsync(CancellationToken.None);
            Assert(server.Delayed.WaitOne(3500), "mock provider is in flight before switch and delete");
            catalog.Select(second.Id);
            catalog.Remove(first.Id);
            ModelCatalogFile.Save(folder, catalog);
            delayedSettings.ApiKey = LoopbackDeepSeek.AlternateKey;
            delayedSettings.Model = "mutation-must-not-leak";
            Assert(pending.GetAwaiter().GetResult() == "最终回答" && running.TestAsync(CancellationToken.None).GetAwaiter().GetResult() == "最终回答" &&
                server.ObservedRequests.Count(item => item.StartsWith("test-delay/primary/")) >= 2,
                "running snapshot remains pinned while current profile changes or is deleted");
        }
        catalog = ModelCatalogFile.Load(folder);
        Assert(catalog.Active.Id == second.Id && catalog.Active.Settings.ApiKey == LoopbackDeepSeek.AlternateKey &&
            catalog.Profiles.Length == 1 && original.SequenceEqual(File.ReadAllBytes(legacyPath)), "current selection and independent key survive reopening encrypted catalog");
        using (var reply = new DeepSeekPrivateReply(catalog.Active.Settings, () => new PrivateModelTurn[0], Ignore))
        {
            reply.TestAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert(server.ObservedRequests.Any(item => item.StartsWith("test-secondary/secondary/")), "new client uses selected configuration and its own key");
            Assert(reply.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult() == "models" && server.ObservedRequests.Contains("GET/models"),
                "lightweight check sends exactly the same-origin models GET");
        }

        var deep = Settings(server, "deepseek-flash");
        ThinkingRequest(deep, "default", "default");
        ThinkingRequest(deep, "disabled", "default");
        ThinkingRequest(deep, "enabled", "low");
        ThinkingRequest(deep, "enabled", "high");
        var attempted = server.ObservedRequests.ToArray();
        Assert(attempted.Contains("deepseek-flash/primary/omitted/omitted/chat-budget") &&
            attempted.Contains("deepseek-flash/primary/disabled/omitted/chat-budget") &&
            attempted.Contains("deepseek-flash/primary/enabled/low/chat-budget") &&
            attempted.Contains("deepseek-flash/primary/enabled/high/chat-budget"), "default omission and documented DeepSeek modes mapped to HTTP JSON");
        deep.Model = "deepseek-v4-pro";
        try { ThinkingRequest(deep, "enabled", "max"); throw new Exception("expected rejection"); }
        catch (ModelReplyException error) { Assert(error.Code == "request-parameters", "rejected thinking parameters report failure without fallback"); }
        Assert(server.ObservedRequests.Count(item => item.StartsWith("deepseek-v4-pro/")) == 1, "no thinking retry after HTTP rejection");
        Assert(ModelCapabilities.Recognize("https://api.deepseek.com/v1/chat/completions").Suggestion == PrivateModelSettings.DeepSeek &&
            ModelCapabilities.Recognize("https://api.openai.com/v1/chat/completions").Suggestion == PrivateModelSettings.OpenAi &&
            ModelCapabilities.Recognize("https://api.deepseek.com.gateway.example/v1/chat/completions").Suggestion == PrivateModelSettings.Compatible &&
            ModelCapabilities.Recognize("https://api.anthropic.com/v1/messages").Suggestion == null,
            "official host and format classified separately from custom endpoint or unsupported native path");
        var custom = Settings(server, "deepseek-flash"); custom.Provider = PrivateModelSettings.Compatible;
        try { ThinkingRequest(custom, "enabled", "low"); throw new Exception("expected local rejection"); }
        catch (ModelReplyException ex) { Assert(ex.Code == "thinking-unsupported", "custom relay receives no guessed thinking parameters"); }
        Assert(!ModelCapabilities.SameOrigin("https://api.deepseek.com/chat/completions", "https://gateway.example/v1/chat/completions"),
            "credential reuse is denied across endpoint origins");
        var openAi = Settings(server, "compatible-test"); openAi.Provider = PrivateModelSettings.OpenAi;
        ThinkingRequest(openAi, "default", "default");
        Assert(server.ObservedRequests.Contains("compatible-test/primary/omitted/omitted/new-budget"), "OpenAI Chat format uses documented completion limit and no unverified thinking fields");
    }

    private static void ThinkingRequest(PrivateModelSettings settings, string mode, string effort)
    {
        var copy = settings.Copy();
        copy.ThinkingMode = mode;
        copy.ThinkingEffort = effort;
        using (var client = new DeepSeekPrivateReply(copy, () => new PrivateModelTurn[0], Ignore))
            client.TestAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    private static void CheckForm(string folder)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var original = ModelSettingsFile.Load(Path.Combine(folder, "model-settings.dpapi"));
        original.RolePrompt = "existing private fixture persona";
        ModelSettingsFile.Save(Path.Combine(folder, "model-settings.dpapi"), original);
        using (var dialog = new ModelConfigForm(folder, Console.WriteLine, () => { throw new Exception("unexpected context clear"); }))
        {
            var role = PrivateField<TextBox>(dialog, "role");
            var password = PrivateField<TextBox>(dialog, "key");
            Assert(role.Text == original.RolePrompt && password.UseSystemPasswordChar && password.Text == "",
                "opening actual settings neither fills a saved key into text input nor replaces an existing role");
            var type = dialog.GetType();
            type.GetMethod("StartNew", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(dialog, null);
            type.GetMethod("FillExample", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(dialog, null);
            Assert(PrivateField<TextBox>(dialog, "roleName").Text == "冰上Ivy" && role.Text.Contains("十六岁") &&
                ModelCatalogFile.Load(folder).Active.Settings.RolePrompt == original.RolePrompt, "example fills only the editable draft after explicit action");
        }
    }

    private static T PrivateField<T>(object instance, string name)
    {
        return (T)instance.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(instance);
    }
}
