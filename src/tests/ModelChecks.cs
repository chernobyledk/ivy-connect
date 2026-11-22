using System;
using System.Collections.Generic;
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
    private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
    private readonly Action<string> log;
    internal readonly ManualResetEvent Delayed = new ManualResetEvent(false);
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
            if (!lines[0].StartsWith("POST /chat/completions ") ||
                !lines.Any(line => line.Equals("Authorization: Bearer " + Key, StringComparison.OrdinalIgnoreCase)))
            {
                Write(stream, 401, "{}");
                return;
            }
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
            if ((bool)request["stream"] || (string)((Dictionary<string, object>)request["thinking"])["type"] != "disabled")
                throw new InvalidDataException("wrong contract");
            log("mock request model=" + model + " roles=" + roles);
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
                if (ui) { CheckForm(file); return; }
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
            }
        }
        finally
        {
            if (Path.GetDirectoryName(root).TrimEnd(Path.DirectorySeparatorChar) ==
                temporary.TrimEnd(Path.DirectorySeparatorChar) && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void CheckForm(string file)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var form = new ModelConfigForm(file, Console.WriteLine))
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            var button = form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>()
                .Single(control => control.Text == "模型连接测试");
            var password = form.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<TextBox>()
                .Single(control => control.UseSystemPasswordChar);
            var result = form.Controls.OfType<Label>().Single();
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            string outcome = null;
            timer.Tick += (s, e) => {
                if (!result.Text.StartsWith("连接成功", StringComparison.Ordinal) && elapsed.ElapsedMilliseconds < 10000) return;
                outcome = result.Text;
                form.Close();
            };
            form.Shown += (s, e) => { button.PerformClick(); timer.Start(); };
            Application.Run(form);
            Assert(password.UseSystemPasswordChar && outcome != null && outcome.Contains("连接成功") &&
                outcome.Contains("最终回答") && !outcome.Contains("hidden test trace"), "actual settings dialog displays mock connection result");
        }
    }
}
