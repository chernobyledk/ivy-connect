using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static class MultimodelChecks
{
    private sealed class Fixture : IDisposable
    {
        private readonly TcpListener server = new TcpListener(IPAddress.Loopback, 0);
        private readonly bool requireProxyAuth;
        private readonly bool officialTunnel;
        internal string Address { get; private set; }
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests =
            new System.Collections.Concurrent.ConcurrentQueue<string>();

        internal Fixture(bool requireProxyAuth = false, bool officialTunnel = false)
        {
            this.requireProxyAuth = requireProxyAuth;
            this.officialTunnel = officialTunnel;
            server.Start();
            Address = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port;
            Task.Run(() => Accept());
        }

        private async Task Accept()
        {
            try
            {
                while (true)
                {
                    var client = await server.AcceptTcpClientAsync().ConfigureAwait(false);
                    ThreadPool.QueueUserWorkItem(_ => Exchange(client));
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void Exchange(TcpClient client)
        {
            using (client)
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var header = new StringBuilder();
                while (header.Length < 16384)
                {
                    int next = stream.ReadByte();
                    if (next < 0) return;
                    header.Append((char)next);
                    int n = header.Length;
                    if (n > 3 && header[n - 4] == '\r' && header[n - 3] == '\n' && header[n - 2] == '\r' && header[n - 1] == '\n') break;
                }
                var lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                var first = lines[0].Split(' ');
                var target = first[1];
                if (first[0] == "CONNECT") Requests.Enqueue("CONNECT requested");
                var auth = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("bench-user:bench-pass"));
                if (requireProxyAuth && !lines.Any(line => line.Equals(auth, StringComparison.OrdinalIgnoreCase)))
                {
                    Requests.Enqueue("proxy-407");
                    Write(stream, "407 Proxy Authentication Required", "{}", "Proxy-Authenticate: Basic realm=\"fixture\"\r\n");
                    return;
                }
                if (first[0] == "CONNECT")
                {
                    Requests.Enqueue("CONNECT accepted");
                    if (officialTunnel && target == "api.deepseek.com:443")
                    {
                        using (var upstream = new TcpClient("api.deepseek.com", 443))
                        {
                            var tls = upstream.GetStream();
                            var ready = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n");
                            stream.Write(ready, 0, ready.Length);
                            stream.ReadTimeout = Timeout.Infinite;
                            Task.WhenAny(stream.CopyToAsync(tls), tls.CopyToAsync(stream)).GetAwaiter().GetResult();
                        }
                    }
                    else Write(stream, "502 Bad Gateway", "{}");
                    return;
                }
                Requests.Enqueue(first[0] + " " + (target.StartsWith("http://") ? "absolute" : "origin"));
                if (first[0] == "GET")
                {
                    Write(stream, "200 OK", "{\"data\":[{\"id\":\"listed-model\"}]}");
                    return;
                }
                if (target.EndsWith("/responses"))
                {
                    var field = lines.First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var bytes = new byte[Int32.Parse(field.Substring(field.IndexOf(':') + 1))];
                    for (int pos = 0; pos < bytes.Length;)
                    {
                        int n = stream.Read(bytes, pos, bytes.Length - pos);
                        if (n == 0) return;
                        pos += n;
                    }
                    var body = ModelWire.Decode(Encoding.UTF8.GetString(bytes));
                    var messages = ModelWire.List(ModelWire.Field(body, "input")).Select(ModelWire.Map).ToArray();
                    if (messages.Length == 4)
                    {
                        var earlier = ModelWire.Map(ModelWire.List(ModelWire.Field(messages[2], "content"))[0]);
                        Assert(ModelWire.Text(earlier, "type") == "output_text" && ModelWire.Text(earlier, "text") == "past-answer",
                            "Responses actual HTTP request retains assistant context");
                    }
                    Write(stream, "200 OK", Response);
                }
                else Write(stream, "200 OK", Chat);
            }
            catch (IOException) { }
            catch (SocketException) { }
        }

        private static void Write(NetworkStream stream, string status, string text, string additional = "")
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var header = "HTTP/1.1 " + status + "\r\n" + additional + "Content-Type: application/json; charset=utf-8\r\n" +
                "Connection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n";
            var preamble = Encoding.ASCII.GetBytes(header);
            stream.Write(preamble, 0, preamble.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose() { server.Stop(); }
    }

    private const string Chat = "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"reasoning_content\":\"private-thought\",\"content\":\"done\"}}]}";
    private const string Response = "{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"content\":[{\"type\":\"reasoning_text\",\"text\":\"private-thought\"}]},{\"type\":\"function_call\",\"arguments\":\"tool-content\"},{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"commentary\",\"content\":[{\"type\":\"output_text\",\"text\":\"intermediate\"}]},{\"type\":\"message\",\"role\":\"assistant\",\"phase\":\"final_answer\",\"content\":[{\"type\":\"output_text\",\"text\":\"done\"}]}]}";

    private static void Assert(bool ok, string name)
    { if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
    private static void Reject(string code, Action check)
    {
        try { check(); }
        catch (ModelReplyException error) { Assert(error.Code == code, code + " classified"); return; }
        throw new InvalidOperationException("expected " + code);
    }

    private static PrivateModelSettings At(string provider, string model, string protocol)
    {
        return new PrivateModelSettings { Provider = provider, Endpoint = ModelCapabilities.Preset(provider).Base,
            Protocol = protocol, Model = model, ApiKey = "synthetic-no-real-api-key", Network = ModelNetwork.Direct, OutputTokens = 512 };
    }

    private static Dictionary<string, object> Request(PrivateModelSettings settings)
    {
        var body = ModelWire.Request(settings, settings.Validate(), new[] { new ModelMessage("system", "role"),
            new ModelMessage("user", "question"), new ModelMessage("assistant", "past-answer"), new ModelMessage("user", "new") });
        return ModelWire.Decode(ModelWire.Encode(body));
    }

    private static void Protocols()
    {
        var open = At(PrivateModelSettings.OpenAi, "gpt-6.2-sol", ModelCapabilities.Responses);
        open.ThinkingEffort = "medium";
        var body = Request(open);
        Assert(open.Validate().AbsolutePath == "/v1/responses" && body.ContainsKey("input") && !body.ContainsKey("messages") &&
            (int)body["max_output_tokens"] == 512 && Equals(body["store"], false) &&
            ModelWire.Text(ModelWire.Map(body["reasoning"]), "effort") == "medium", "Responses request protocol and reasoning object");
        open.Endpoint += "/responses";
        Assert(open.Validate().AbsolutePath == "/v1/responses", "complete route is not duplicated");
        open.Endpoint = "https://api.openai.com/v1/chat/completions";
        Reject("endpoint", () => open.Validate());
        open.Endpoint = "https://api.openai.com/v1";
        Assert(ModelWire.Answer(ModelWire.Decode(Response), open) == "done", "Responses returns only final assistant output_text");
        Reject("output-budget", () => ModelWire.Answer(ModelWire.Decode("{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"}}"), open));
        Reject("empty", () => ModelWire.Answer(ModelWire.Decode("{\"status\":\"completed\",\"output\":[{\"type\":\"reasoning\",\"text\":\"private\"}]}"), open));
        Reject("format", () => ModelWire.Decode("}{"));

        var qwen = At(ModelCapabilities.Qwen, "qwen3.8-max", ModelCapabilities.Chat);
        qwen.ThinkingMode = "enabled";
        Assert(Equals(Request(qwen)["enable_thinking"], true) && Request(qwen).ContainsKey("max_tokens"), "Qwen Chat switch");
        qwen.Protocol = ModelCapabilities.Responses; qwen.ThinkingMode = "default"; qwen.ThinkingEffort = "xhigh";
        Assert(ModelWire.Text(ModelWire.Map(Request(qwen)["reasoning"]), "effort") == "xhigh", "Qwen Responses effort");
        var glm = At(ModelCapabilities.Glm, "glm-5.3", ModelCapabilities.Chat);
        glm.ThinkingMode = "enabled"; glm.ThinkingEffort = "low";
        Assert(ModelWire.Text(ModelWire.Map(Request(glm)["thinking"]), "type") == "enabled", "GLM thinking object");
        var ark = At(ModelCapabilities.Ark, "ep-synthetic-001", ModelCapabilities.Chat);
        Assert(Request(ark).ContainsKey("max_completion_tokens") && !Request(ark).ContainsKey("thinking"), "Ark endpoint ID has no guessed thinking capability");
        var mini = At(ModelCapabilities.MiniMax, "MiniMax-M3.1-Flash", ModelCapabilities.Chat);
        mini.ThinkingEffort = "high";
        Assert(Equals(Request(mini)["reasoning_split"], true) && Request(mini).ContainsKey("max_completion_tokens"), "MiniMax separation and budget");
        var gemini = At(ModelCapabilities.Gemini, "gemini-2.5-flash", ModelCapabilities.Chat);
        gemini.ThinkingMode = "disabled";
        Assert((string)Request(gemini)["reasoning_effort"] == "none", "Gemini documented disable setting");
        var grok = At(ModelCapabilities.Grok, "grok-4.7", ModelCapabilities.Responses);
        grok.ThinkingEffort = "xhigh";
        Assert(Request(grok).ContainsKey("reasoning"), "Grok Responses effort");
        var kimi = At(ModelCapabilities.Kimi, "kimi-k3.1-pro", ModelCapabilities.Chat);
        Reject("model-context", () => Request(kimi));
        kimi.ContextTurns = 0; kimi.ThinkingEffort = "max";
        Assert((string)Request(kimi)["reasoning_effort"] == "max", "Kimi single-turn effort");
        var deep = At(PrivateModelSettings.DeepSeek, "deepseek-flash", ModelCapabilities.Responses);
        deep.ThinkingMode = "disabled";
        Assert(ModelWire.Text(ModelWire.Map(Request(deep)["reasoning"]), "effort") == "none", "DeepSeek Responses adapter uses protocol-specific thinking controls");
        Assert(ModelWire.Answer(ModelWire.Decode(Chat), glm) == "done", "Chat ignores separate reasoning");
        Reject("output-budget", () => ModelWire.Answer(ModelWire.Decode(Chat.Replace("\"stop\"", "\"length\"")), glm));
        Reject("sensitive", () => ModelWire.Answer(ModelWire.Decode(Chat.Replace("\"done\"", "\"synthetic-no-real-api-key\"")), glm));
        Reject("quota", () => { throw ModelWire.ApiError(429, ModelWire.Decode("{\"error\":{\"code\":\"insufficient_quota\"}}"), glm); });
        Reject("region", () => { throw ModelWire.ApiError(403, ModelWire.Decode("{\"error\":{\"code\":\"unsupported_country_region_territory\"}}"), glm); });
        Reject("permission", () => { throw ModelWire.ApiError(403, null, glm); });
        Reject("model-not-found", () => { throw ModelWire.ApiError(404, ModelWire.Decode("{\"error\":{\"code\":\"model_not_found\"}}"), glm); });
        Assert(ModelCapabilities.Recognize("https://api.openai.com.some-router.example/v1/responses").Suggestion ==
            PrivateModelSettings.Compatible && ModelCapabilities.Recognize("https://api.openai.com/v1").Suggestion ==
            PrivateModelSettings.OpenAi, "recognition separates official domain from gateway");
        Assert(ModelCapabilities.ModelsAddress(gemini.Validate(), gemini.Provider).AbsolutePath == "/v1beta/openai/models" &&
            ModelCapabilities.ModelsAddress(ark.Validate(), ark.Provider) == null &&
            ModelCapabilities.ModelsAddress(deep.Validate(), deep.Provider).AbsolutePath == "/models", "only known list routes used");
    }

    private static void Network()
    {
#if NET10_0_OR_GREATER
        var before = HttpClient.DefaultProxy;
#else
        var before = WebRequest.DefaultWebProxy;
#endif
        using (var origin = new Fixture())
        using (var system = new Fixture(true))
        using (var proxy = new Fixture(true))
        {
            try
            {
#if NET10_0_OR_GREATER
                HttpClient.DefaultProxy = new ModelNetwork.ExplicitHttpProxy(new Uri(system.Address));
#else
                WebRequest.DefaultWebProxy = new ModelNetwork.ExplicitHttpProxy(new Uri(system.Address));
#endif
                var profile = At(PrivateModelSettings.Compatible, "fixture", ModelCapabilities.Responses);
                profile.Endpoint = origin.Address;
                profile.RolePrompt = "role";
                var history = new[] { new PrivateModelTurn { User = "past-question", Assistant = "past-answer" } };
                var collected = new List<string>();
                using (var client = new HttpPrivateReply(profile, () => history, collected.Add))
                    Assert(client.GenerateAsync(new PrivateEvent { LocalId = "route-id", Body = "new question" }, CancellationToken.None)
                        .GetAwaiter().GetResult().Text == "done", "Responses over HTTP uses role and multi-turn context");
                Assert(origin.Requests.Contains("POST origin") && !system.Requests.Any(), "direct bypasses the default proxy");
                profile.Network = ModelNetwork.System;
                profile.Endpoint = "https://model-system-route.invalid/v1";
                using (var client = new HttpPrivateReply(profile, () => history, collected.Add))
                {
                    try { client.TestAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (ModelReplyException) { }
                }
                Assert(system.Requests.Contains("CONNECT requested"), "system route sends HTTPS CONNECT to process default proxy");
                profile.Network = ModelNetwork.Proxy;
                profile.ProxyAddress = proxy.Address;
                using (var client = new HttpPrivateReply(profile, () => history, collected.Add))
                {
                    try { client.TestAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (ModelReplyException error) { Console.WriteLine("proxy rejection classified as " + error.Code); }
                }
                profile.ProxyUser = "bench-user"; profile.ProxyPassword = "bench-pass";
                using (var client = new HttpPrivateReply(profile, () => history, collected.Add))
                {
                    try { client.TestAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (ModelReplyException) { }
                }
                Assert(proxy.Requests.Contains("CONNECT accepted") && proxy.Requests.Contains("proxy-407") && collected.All(x => !x.Contains(profile.ProxyPassword) &&
                    !x.Contains(profile.ProxyUser) && !x.Contains(profile.ApiKey)), "credentials and response details absent from logs");
                profile.ProxyAddress = "socks5://localhost:8888";
                Reject("proxy-setting", () => profile.Validate());
                Reject("dns", () => { throw ModelNetwork.TransportError(new HttpRequestException("redacted",
                    new WebException("redacted", WebExceptionStatus.NameResolutionFailure)), ModelNetwork.Direct); });
                Reject("tls", () => { throw ModelNetwork.TransportError(new HttpRequestException("redacted",
                    new WebException("redacted", WebExceptionStatus.TrustFailure)), ModelNetwork.Direct); });
            }
            finally
            {
#if NET10_0_OR_GREATER
                HttpClient.DefaultProxy = before;
#else
                WebRequest.DefaultWebProxy = before;
#endif
            }
        }
    }

    private static void Migration()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ivy-profile-v060-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ModelCatalogFile.FileName);
            const string legacy = "<PrivateModelCatalog><Schema>1</Schema><ActiveId>001</ActiveId><Profiles>" +
                "<PrivateModelProfile><Id>001</Id><Name>old</Name><Settings><Provider>DeepSeek Chat Completions</Provider>" +
                "<Endpoint>https://api.deepseek.com/chat/completions</Endpoint><Model>deepseek-flash</Model><ApiKey>fixture-only</ApiKey>" +
                "<ThinkingMode>disabled</ThinkingMode><RolePrompt>legacy role</RolePrompt></Settings></PrivateModelProfile></Profiles></PrivateModelCatalog>";
            File.WriteAllBytes(path, System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(legacy), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
            var bytes = File.ReadAllBytes(path);
            var catalog = ModelCatalogFile.Load(dir);
            var settings = catalog.Active.Settings;
            Assert(bytes.SequenceEqual(File.ReadAllBytes(path)) && settings.Network == ModelNetwork.System &&
                settings.Protocol == ModelCapabilities.Chat && settings.ApiKey == "fixture-only" && settings.RolePrompt == "legacy role" &&
                settings.ThinkingMode == "disabled", "v0.6 DPAPI XML loads losslessly with defaults and is untouched");
            settings.Network = ModelNetwork.Proxy; settings.ProxyAddress = "http://127.0.0.1:11111";
            settings.ProxyUser = "migration-user"; settings.ProxyPassword = "migration-pass";
            ModelCatalogFile.Save(dir, catalog);
            var loaded = ModelCatalogFile.Load(dir).Active;
            Assert(loaded.Settings.ProxyPassword == settings.ProxyPassword && !File.ReadAllText(path).Contains(settings.ProxyPassword) &&
                loaded.CopyAs("clean", false).Settings.ProxyPassword.Length == 0, "encrypted proxy credentials persist and redacted copy clears them");
        }
        finally { foreach (var file in Directory.GetFiles(dir)) File.Delete(file); Directory.Delete(dir); }
    }

    private static void QqContinuity()
    {
        var code = new ReplyRow("recent-code", new[] { "11337" }, false);
        var one = new ReplyRow("stable-a", new[] { "one" }, true);
        var two = new ReplyRow("stable-b", new[] { "two" }, false);
        var three = new ReplyRow("stable-c", new[] { "three" }, true);
        var next = new ReplyRow("fresh", new[] { "fresh" }, false);
        string challenge = MessageSnapshot.Hash(code.Anchor);
        var before = new[] { code, one, two, three };
        var after = new[] { one, two, three, next };
        bool rolled;
        var flow = new VerifiedChatContinuity();
        Assert(flow.Check(challenge, "history-a", before, out rolled) && !rolled &&
            flow.Check(challenge, "history-a", after, out rolled) && rolled,
            "same live history accepts losing old challenge with ordered stable rows of both directions");
        Assert(!new VerifiedChatContinuity().Check(challenge, "history-a", after, out rolled),
            "restart cannot use rolling proof to bypass independent identity verification");
        Assert(!flow.Check(challenge, "history-b", after, out rolled), "history reconstruction invalidates rolling proof");
        Assert(flow.Check(challenge, "history-a", before, out rolled) &&
            !flow.Check(challenge, "history-a", new[] { two, three, next }, out rolled), "anchor gap fails closed");
        var changedDirection = new ReplyRow("stable-b", new[] { "two" }, true);
        Assert(flow.Check(challenge, "history-a", before, out rolled) &&
            !flow.Check(challenge, "history-a", new[] { one, changedDirection, three, next }, out rolled), "direction mismatch fails closed");
    }

    private static void Real(string folder, string protocol)
    {
        var catalog = ModelCatalogFile.Load(folder);
        var setting = catalog.Active.Settings.Copy();
        if (setting.Provider != PrivateModelSettings.DeepSeek || !ModelCapabilities.IsOfficialDeepSeek(setting.Validate()))
            throw new InvalidOperationException("active profile is outside the authorized official DeepSeek test");
        if (protocol == ModelCapabilities.Responses)
        {
            setting.Protocol = protocol;
            setting.Endpoint = ModelCapabilities.Preset(setting.Provider).Base;
            setting.ThinkingMode = "disabled";
            setting.ThinkingEffort = "default";
        }
        using (var relay = protocol == "proxy" ? new Fixture(true, true) : null)
        {
            if (relay != null)
            {
                setting.Network = ModelNetwork.Proxy;
                setting.ProxyAddress = relay.Address;
                setting.ProxyUser = "bench-user";
                setting.ProxyPassword = "bench-pass";
            }
            using (var client = new HttpPrivateReply(setting, () => new PrivateModelTurn[0], Console.WriteLine))
            {
                if (protocol == ModelCapabilities.Chat)
                {
                    Console.WriteLine("lightweight=" + client.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult());
                    Console.WriteLine("models-count=" + client.ListAsync(CancellationToken.None).GetAwaiter().GetResult().Length);
                }
                if (relay != null) Assert(client.ProbeAsync(CancellationToken.None).GetAwaiter().GetResult() == "auth-ready",
                    "official check uses authenticated proxy route");
                Assert(!String.IsNullOrWhiteSpace(client.TestAsync(CancellationToken.None).GetAwaiter().GetResult()),
                    "authorized real DeepSeek " + setting.Protocol + " final text");
                if (relay != null) Assert(relay.Requests.Contains("CONNECT accepted"), "HTTPS model request transported through authenticated proxy");
            }
        }
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2) Real(args[0], args[1]);
            else { Protocols(); Migration(); QqContinuity(); Network(); }
            return 0;
        }
        catch (ModelReplyException error) { Console.WriteLine("FAIL " + error.Code + " " + error.Message); return 1; }
        catch (Exception error) { Console.WriteLine("FAIL " + error.GetType().Name + " " + error.Message); return 1; }
    }
}
