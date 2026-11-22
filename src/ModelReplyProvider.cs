using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

internal sealed class ModelReplyException : InvalidOperationException
{
    internal readonly string Code;
    internal ModelReplyException(string code, string description) : base(description) { Code = code; }
}

internal static class ModelReplyContext
{
    internal static IEnumerable<PrivateModelTurn> Recent(IEnumerable<PrivateModelTurn> turns, int limit)
    {
        int length = 0;
        var recent = new List<PrivateModelTurn>();
        foreach (var turn in turns.Reverse().Take(Math.Min(8, limit)))
        {
            if (turn == null || String.IsNullOrWhiteSpace(turn.User) || String.IsNullOrWhiteSpace(turn.Assistant)) break;
            length += turn.User.Length + turn.Assistant.Length;
            if (length > 6000) break;
            recent.Add(turn);
        }
        recent.Reverse();
        return recent;
    }
}

internal sealed class ModelMessage
{
    internal readonly string Role, Text;
    internal ModelMessage(string role, string text) { Role = role; Text = text; }
}

internal static class ModelWire
{
    internal static Dictionary<string, object> Request(PrivateModelSettings settings, Uri endpoint, IEnumerable<ModelMessage> messages)
    {
        var body = new Dictionary<string, object> { { "model", settings.Model }, { "stream", false } };
        if (settings.Protocol == ModelCapabilities.Responses)
        {
            body["store"] = false;
            body["max_output_tokens"] = settings.OutputTokens == 0 ? 4096 : settings.OutputTokens;
            body["input"] = messages.Select(message => new {
                type = "message", role = message.Role,
                content = new[] { new { type = message.Role == "assistant" ? "output_text" : "input_text", text = message.Text } }
            }).ToArray();
        }
        else
        {
            var preset = ModelCapabilities.Preset(settings.Provider);
            var options = ModelCapabilities.Thinking(settings, endpoint);
            int standard = settings.Provider == PrivateModelSettings.OpenAi ? 1200 :
                (settings.Provider == PrivateModelSettings.DeepSeek && (options.Switch == null || settings.ThinkingMode == "disabled")) ||
                settings.Provider == PrivateModelSettings.Compatible ? 700 : 4096;
            body[preset.Budget] = settings.OutputTokens == 0 ? standard : settings.OutputTokens;
            if (settings.Provider == ModelCapabilities.MiniMax &&
                (settings.Model == "MiniMax-M3" || settings.Model == "MiniMax-M3.1-Flash")) body["reasoning_split"] = true;
            if (settings.Provider == PrivateModelSettings.OpenAi) body["store"] = false;
            body["messages"] = messages.Select(message => new { role = message.Role, content = message.Text }).ToArray();
        }
        ModelCapabilities.ApplyThinking(settings, endpoint, body);
        return body;
    }

    internal static string Answer(Dictionary<string, object> packet, PrivateModelSettings settings)
    {
        if (Field(packet, "error") != null) throw ApiError(200, packet, settings);
        var baseResponse = Map(Field(packet, "base_resp"));
        if (Field(baseResponse, "status_code") != null && Verbatim(baseResponse, "status_code") != "0")
            throw ApiError(200, packet, settings);
        var raw = settings.Protocol == ModelCapabilities.Responses ? ResponsesText(packet, settings) : ChatText(packet);
        var text = (raw ?? "").Trim();
        if (String.IsNullOrWhiteSpace(text)) throw new ModelReplyException("empty", "服务没有返回可发送的最终正文");
        if (Regex.IsMatch(text, @"<\s*/?\s*(?:think|analysis|reasoning)(?:\s|>|/)", RegexOptions.IgnoreCase))
            throw new ModelReplyException("thinking", "最终正文混有思考标签，已跳过");
        if (text.Length > 2000) throw new ModelReplyException("length", "模型最终回答超出 2000 字，已跳过");
        if (new[] { settings.ApiKey, settings.ProxyPassword, settings.ProxyUser }
            .Any(secret => !String.IsNullOrEmpty(secret) && text.IndexOf(secret, StringComparison.Ordinal) >= 0))
            throw new ModelReplyException("sensitive", "最终回答疑似包含凭据，已跳过");
        return text;
    }

    private static string ChatText(Dictionary<string, object> packet)
    {
        var choices = List(Field(packet, "choices"));
        var choice = choices == null ? null : choices.Select(Map).FirstOrDefault();
        if (choice == null) throw Format("Chat Completions 响应缺少 choices");
        var finish = Text(choice, "finish_reason");
        if (finish == "length") throw BudgetExhausted();
        if (finish != "stop") throw new ModelReplyException("finish", "Chat Completions 未完成普通文本回复，已跳过");
        var message = Map(Field(choice, "message"));
        if (Text(message, "role") != "assistant") throw Format("Chat Completions 响应缺少助手消息");
        var tools = List(Field(message, "tool_calls"));
        if (Field(message, "refusal") is string || (tools != null && tools.Length > 0) ||
            Field(message, "function_call") != null)
            throw new ModelReplyException("finish", "服务返回拒绝或工具内容，未发送");
        var content = Field(message, "content");
        var parts = List(content);
        return parts == null ? content as string : String.Concat(parts.Select(Map)
            .Where(x => Text(x, "type") == "text").Select(x => Text(x, "text")));
    }

    private static string ResponsesText(Dictionary<string, object> packet, PrivateModelSettings settings)
    {
        var state = Text(packet, "status");
        if (state == "incomplete" && Text(Map(Field(packet, "incomplete_details")), "reason") == "max_output_tokens")
            throw BudgetExhausted();
        if (state == "failed" && Field(packet, "error") != null) throw ApiError(200, packet, settings);
        if (state != "completed") throw new ModelReplyException("finish", "Responses 未完成普通文本回复，已跳过");
        var output = List(Field(packet, "output"));
        if (output == null) throw Format("Responses 响应缺少 output");
        var messages = output.Select(Map).Where(item => Text(item, "type") == "message" &&
            Text(item, "role") == "assistant" && (Text(item, "status") == null || Text(item, "status") == "completed") &&
            (Text(item, "phase") == null || Text(item, "phase") == "final_answer"));
        return String.Join("\n", messages.Select(message => String.Concat((List(Field(message, "content")) ?? new object[0])
            .Select(Map).Where(part => Text(part, "type") == "output_text").Select(part => Text(part, "text"))))
            .Where(text => !String.IsNullOrWhiteSpace(text)));
    }

    internal static Dictionary<string, object> Decode(string text)
    {
        try
        {
            var parsed = Map(new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 48 }.DeserializeObject(text));
            if (parsed != null) return parsed;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        throw Format("响应不是约定的 JSON 对象");
    }

    internal static Dictionary<string, object> Map(object node) { return node as Dictionary<string, object>; }
    internal static object[] List(object node) { return node as object[]; }
    internal static object Field(Dictionary<string, object> node, string key)
    {
        object value;
        return node != null && node.TryGetValue(key, out value) ? value : null;
    }
    internal static string Text(Dictionary<string, object> node, string key) { return Field(node, key) as string; }
    private static string Verbatim(Dictionary<string, object> node, string key) { return Convert.ToString(Field(node, key)); }

    internal static ModelReplyException Format(string reason) { return new ModelReplyException("format", reason); }
    private static ModelReplyException BudgetExhausted()
    { return new ModelReplyException("output-budget", "模型耗尽输出配额，未取得完整最终正文。可增加输出上限或降低思考用量。"); }

    internal static ModelReplyException ApiError(int status, Dictionary<string, object> packet, PrivateModelSettings settings)
    {
        var error = Map(Field(packet, "error"));
        var code = Verbatim(error, "code");
        if (code.Length == 0) code = Verbatim(packet, "code");
        if (code.Length == 0) code = Verbatim(error, "type");
        var suffix = status == 200 ? "" : "（HTTP " + status + "）";
        if (status == 407) return new ModelReplyException("proxy-auth", "代理认证未通过" + suffix);
        if (status == 401 || code == "invalid_api_key" || code == "API_KEY_INVALID")
            return new ModelReplyException("auth", "模型 API 鉴权失败" + suffix);
        if (code == "unsupported_country_region_territory" || code == "unsupported_region" || code == "region_not_supported")
            return new ModelReplyException("region", "服务明确返回地区限制" + suffix);
        if (status == 403) return new ModelReplyException("permission", "服务拒绝访问，请检查权限、区域或账号资格" + suffix);
        if (code == "model_not_found" || code == "ModelNotFound" || code == "InvalidEndpointOrModel.NotFound")
            return new ModelReplyException("model-not-found", "服务未找到模型或当前账号无权使用它" + suffix);
        if (status == 402 || code == "insufficient_quota" || code == "insufficient_balance" ||
            code == "Arrearage" || (settings.Provider == ModelCapabilities.MiniMax &&
                Verbatim(Map(Field(packet, "base_resp")), "status_code") == "1008"))
            return new ModelReplyException("quota", "模型服务提示额度或余额不足" + suffix);
        if (status == 429) return new ModelReplyException("rate-limit", "模型请求被限流，未自动重试" + suffix);
        if (status == 408 || status == 504) return new ModelReplyException("timeout", "服务端请求超时" + suffix);
        if (status == 400 || status == 422)
            return new ModelReplyException("request-parameters", "服务拒绝请求参数或协议" + suffix +
                (settings.ThinkingMode != "default" || settings.ThinkingEffort != "default" ? "。本次包含思考参数。" : "。") +
                "未更换参数或重试。");
        if (status == 404) return new ModelReplyException("endpoint-or-model", "请求地址、资源或模型未找到" + suffix);
        return new ModelReplyException(status == 200 ? "service-error" : "http-" + status,
            "模型服务返回错误" + suffix + "，未自动重试");
    }
}

internal sealed class HttpPrivateReply : IPrivateReplyProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly PrivateModelSettings settings;
    private readonly Uri endpoint;
    private readonly Func<PrivateModelTurn[]> context;
    private readonly Action<string> log;

    internal HttpPrivateReply(PrivateModelSettings settings, Func<PrivateModelTurn[]> context, Action<string> log, bool listOnly = false)
    {
        this.settings = settings.Copy();
        this.context = context;
        this.log = log;
        endpoint = this.settings.Validate(true, !listOnly);
        client = new HttpClient(ModelNetwork.Handler(this.settings)) {
            Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 1048576
        };
    }

    public async Task<ReplyDraft> GenerateAsync(PrivateEvent incoming, CancellationToken cancellation)
    {
        if (incoming == null || String.IsNullOrWhiteSpace(incoming.Body) || incoming.Body.Length > 2000)
            throw new ModelReplyException("input", "来信文本为空或超出 2000 字，已暂停该回复");
        var messages = new List<ModelMessage>();
        if (!String.IsNullOrWhiteSpace(settings.RolePrompt))
            messages.Add(new ModelMessage("system", settings.RolePrompt));
        foreach (var turn in ModelReplyContext.Recent(context() ?? new PrivateModelTurn[0], settings.ContextTurns))
        {
            messages.Add(new ModelMessage("user", turn.User));
            messages.Add(new ModelMessage("assistant", turn.Assistant));
        }
        messages.Add(new ModelMessage("user", incoming.Body));
        var answer = await Complete(messages, incoming.LocalId, cancellation).ConfigureAwait(false);
        return new ReplyDraft(answer, null, settings.ContextTurns);
    }

    internal Task<string> TestAsync(CancellationToken cancellation)
    {
        return Complete(new[] { new ModelMessage("user", "请只回复：连接正常。") }, "connection-test", cancellation);
    }

    internal async Task<string> ProbeAsync(CancellationToken cancellation)
    {
        if (settings.Provider == PrivateModelSettings.DeepSeek && ModelCapabilities.IsOfficialDeepSeek(endpoint))
        {
            var packet = await Send(HttpMethod.Get, new UriBuilder(endpoint) { Path = "/user/balance" }.Uri, null, cancellation).ConfigureAwait(false);
            var result = ModelWire.Field(packet, "is_available") as bool?;
            if (!result.HasValue) throw ModelWire.Format("未识别到 DeepSeek 鉴权响应");
            return result.Value ? "auth-ready" : "auth-no-balance";
        }
        await ListAsync(cancellation).ConfigureAwait(false);
        return "models";
    }

    internal async Task<string[]> ListAsync(CancellationToken cancellation)
    {
        var address = ModelCapabilities.ModelsAddress(endpoint, settings.Provider);
        if (address == null) throw new ModelReplyException("models-unsupported", "当前地址的官方列表接口未配置，请手工填写模型 ID");
        var packet = await Send(HttpMethod.Get, address, null, cancellation).ConfigureAwait(false);
        if (ModelWire.Field(packet, "error") != null) throw ModelWire.ApiError(200, packet, settings);
        var qwen = settings.Provider == ModelCapabilities.Qwen;
        if (qwen && !Equals(ModelWire.Field(packet, "success"), true)) throw ModelWire.ApiError(200, packet, settings);
        var models = ModelWire.List(qwen ? ModelWire.Field(ModelWire.Map(ModelWire.Field(packet, "output")), "models") :
            ModelWire.Field(packet, "data"));
        if (models == null) throw ModelWire.Format("此地址未返回约定的模型列表");
        return models.Select(ModelWire.Map).Select(item => ModelWire.Text(item, qwen ? "model" : "id"))
            .Where(PrivateModelSettings.IsModelId)
            .Where(id => id != settings.ApiKey && id != settings.ProxyUser && id != settings.ProxyPassword)
            .Distinct(StringComparer.Ordinal).Take(250).ToArray();
    }

    private async Task<string> Complete(IEnumerable<ModelMessage> messages, string correlation, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        log("model request id=" + correlation + " protocol=" + settings.Protocol + " network=" + settings.Network + " messages=" + messages.Count());
        try
        {
            var body = new JavaScriptSerializer().Serialize(ModelWire.Request(settings, endpoint, messages));
            var packet = await Send(HttpMethod.Post, endpoint, body, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            var answer = ModelWire.Answer(packet, settings);
            cancellation.ThrowIfCancellationRequested();
            log("model ok id=" + correlation + " answer-length=" + answer.Length + " elapsed-ms=" + elapsed.ElapsedMilliseconds);
            return answer;
        }
        catch (OperationCanceledException) { log("model cancelled id=" + correlation); throw; }
        catch (ModelReplyException error) { log("model error id=" + correlation + " code=" + error.Code); throw; }
    }

    private async Task<Dictionary<string, object>> Send(HttpMethod method, Uri destination, string body, CancellationToken cancellation)
    {
        if (!ModelCapabilities.SameOrigin(endpoint.AbsoluteUri, destination.AbsoluteUri))
            throw new ModelReplyException("endpoint", "检查地址和生成地址不同源，已取消请求");
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (var request = new HttpRequestMessage(method, destination))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false))
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    deadline.Token.ThrowIfCancellationRequested();
                    if (response.IsSuccessStatusCode) return ModelWire.Decode(text);
                    Dictionary<string, object> packet = null;
                    try { packet = ModelWire.Decode(text); }
                    catch (ModelReplyException) { }
                    throw ModelWire.ApiError((int)response.StatusCode, packet, settings);
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested) throw;
                throw new ModelReplyException("timeout", "模型请求超时，未自动重试");
            }
            catch (HttpRequestException error)
            {
                cancellation.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested) throw new ModelReplyException("timeout", "模型请求超时，未自动重试");
                throw ModelNetwork.TransportError(error, settings.Network);
            }
        }
    }

    public void Dispose() { client.Dispose(); }
}
