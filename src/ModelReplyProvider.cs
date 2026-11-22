using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

internal sealed class DeepSeekPrivateReply : IPrivateReplyProvider, IDisposable
{
    [DataContract]
    private sealed class ChatMessage
    {
        [DataMember(Name = "role")] internal string Role;
        [DataMember(Name = "content")] internal string Content;
    }

    [DataContract]
    private sealed class Thinking
    {
        [DataMember(Name = "type")] internal string Type = "disabled";
    }

    [DataContract]
    private sealed class ChatRequest
    {
        [DataMember(Name = "model")] internal string Model;
        [DataMember(Name = "stream")] internal bool Stream = false;
        [DataMember(Name = "max_tokens", EmitDefaultValue = false)] internal int? MaxTokens;
        [DataMember(Name = "max_completion_tokens", EmitDefaultValue = false)] internal int? MaxCompletionTokens;
        [DataMember(Name = "thinking", EmitDefaultValue = false)] internal Thinking ThinkingMode;
        [DataMember(Name = "reasoning_effort", EmitDefaultValue = false)] internal string Effort;
        [DataMember(Name = "messages")] internal ChatMessage[] Messages;
    }

    [DataContract]
    private sealed class Choice
    {
        [DataMember(Name = "message")] internal ChatMessage Message { get; set; }
        [DataMember(Name = "finish_reason")] internal string FinishReason { get; set; }
    }

    [DataContract]
    private sealed class ChatResponse
    {
        [DataMember(Name = "choices")] internal Choice[] Choices { get; set; }
    }

    [DataContract]
    private sealed class BalanceResponse
    {
        [DataMember(Name = "is_available")] internal bool? Available { get; set; }
    }

    [DataContract]
    private sealed class ModelList
    {
        [DataMember(Name = "object")] internal string Type { get; set; }
        [DataMember(Name = "data")] internal ModelItem[] Models { get; set; }
    }

    [DataContract]
    private sealed class ModelItem
    {
        [DataMember(Name = "id")] internal string Id { get; set; }
    }

    private readonly HttpClient client;
    private readonly PrivateModelSettings settings;
    private readonly Uri endpoint;
    private readonly Func<PrivateModelTurn[]> context;
    private readonly Action<string> log;

    internal DeepSeekPrivateReply(PrivateModelSettings settings, Func<PrivateModelTurn[]> context, Action<string> log)
    {
        this.settings = settings.Copy();
        this.context = context;
        this.log = log;
        endpoint = this.settings.Validate();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            UseDefaultCredentials = false, UseProxy = !endpoint.IsLoopback };
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 262144 };
    }

    public async Task<ReplyDraft> GenerateAsync(PrivateEvent incoming, CancellationToken cancellation)
    {
        if (incoming == null || String.IsNullOrWhiteSpace(incoming.Body) || incoming.Body.Length > 2000)
            throw new ModelReplyException("input", "来信文本为空或超出 2000 字，已暂停该回复");
        var messages = new List<ChatMessage>();
        if (!String.IsNullOrWhiteSpace(settings.RolePrompt))
            messages.Add(new ChatMessage { Role = "system", Content = settings.RolePrompt });
        foreach (var turn in ModelReplyContext.Recent(context() ?? new PrivateModelTurn[0], settings.ContextTurns))
        {
            messages.Add(new ChatMessage { Role = "user", Content = turn.User });
            messages.Add(new ChatMessage { Role = "assistant", Content = turn.Assistant });
        }
        messages.Add(new ChatMessage { Role = "user", Content = incoming.Body });
        var answer = await Complete(messages.ToArray(), incoming.LocalId, cancellation).ConfigureAwait(false);
        return new ReplyDraft(answer, null, settings.ContextTurns);
    }

    internal Task<string> TestAsync(CancellationToken cancellation)
    {
        return Complete(new[] { new ChatMessage { Role = "user", Content = "请只回复：连接正常。" } }, "connection-test", cancellation);
    }

    internal async Task<string> ProbeAsync(CancellationToken cancellation)
    {
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(15, settings.TimeoutSeconds)));
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, ModelCapabilities.ProbeAddress(endpoint)))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
                        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        if (ModelCapabilities.IsOfficialDeepSeek(endpoint))
                        {
                            var balance = ParseJson<BalanceResponse>(data);
                            if (balance == null || !balance.Available.HasValue) throw new ModelReplyException("format", "未识别到 DeepSeek 官方鉴权响应");
                            return balance.Available.Value ? "auth-ready" : "auth-no-balance";
                        }
                        var models = ParseJson<ModelList>(data);
                        if (models == null || models.Type != "list" || models.Models == null ||
                            models.Models.Any(item => item == null || String.IsNullOrWhiteSpace(item.Id)))
                            throw new ModelReplyException("format", "此地址未返回约定的模型列表");
                        return "models";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested) throw;
                throw new ModelReplyException("timeout", "轻量检查超时");
            }
            catch (HttpRequestException) { throw new ModelReplyException("connection", "轻量检查连接失败或响应超限"); }
            catch (SerializationException) { throw new ModelReplyException("format", "轻量检查没有返回约定格式"); }
        }
    }

    private ChatRequest Request(ChatMessage[] messages)
    {
        var value = new ChatRequest { Model = settings.Model, Messages = messages };
        if (settings.Provider == PrivateModelSettings.OpenAi) value.MaxCompletionTokens = 1200;
        else value.MaxTokens = ModelCapabilities.SupportsThinking(settings, endpoint) && settings.ThinkingMode != "disabled" ? 4096 : 700;
        if (settings.ThinkingMode != "default") value.ThinkingMode = new Thinking { Type = settings.ThinkingMode };
        if (settings.ThinkingEffort != "default") value.Effort = settings.ThinkingEffort;
        return value;
    }

    private async Task<string> Complete(ChatMessage[] messages, string correlation, CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        log("model request id=" + correlation + " messages=" + messages.Length);
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
                    using (var data = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(ChatRequest)).WriteObject(data,
                            Request(messages));
                        request.Content = new ByteArrayContent(data.ToArray());
                    }
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            int status = (int)response.StatusCode;
                            if (settings.ThinkingMode != "default" && (status == 400 || status == 422))
                                throw new ModelReplyException("request-parameters", "模型拒绝本次请求（HTTP " + status + "）。此请求含思考参数，请检查所选模型和强度。未换档或重试");
                            throw StatusError(response.StatusCode);
                        }
                        var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        deadline.Token.ThrowIfCancellationRequested();
                        string answer = Parse(data);
                        if (answer.IndexOf(settings.ApiKey, StringComparison.Ordinal) >= 0)
                            throw new ModelReplyException("sensitive", "答案疑似包含当前密钥，已跳过");
                        deadline.Token.ThrowIfCancellationRequested();
                        log("model ok id=" + correlation + " answer-length=" + answer.Length + " elapsed-ms=" + elapsed.ElapsedMilliseconds);
                        return answer;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested)
                {
                    log("model cancelled id=" + correlation);
                    throw;
                }
                log("model error id=" + correlation + " code=timeout");
                throw new ModelReplyException("timeout", "模型请求超时，未重试");
            }
            catch (HttpRequestException)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                log("model error id=" + correlation + " code=connection");
                throw new ModelReplyException("connection", "模型连接失败或响应超过大小限制");
            }
            catch (ModelReplyException error)
            {
                log("model error id=" + correlation + " code=" + error.Code);
                throw;
            }
        }
    }

    private static ModelReplyException StatusError(HttpStatusCode status)
    {
        int code = (int)status;
        if (code == 401 || code == 403) return new ModelReplyException("auth", "模型鉴权失败（HTTP " + code + "）");
        if (code == 429) return new ModelReplyException("rate-limit", "模型服务限流（HTTP 429），未自动重试");
        if (code == 408 || code == 504) return new ModelReplyException("timeout", "模型服务超时（HTTP " + code + "）");
        return new ModelReplyException("http-" + code, "模型接口返回 HTTP " + code + "，未自动重试");
    }

    private static string Parse(byte[] bytes)
    {
        ChatResponse packet;
        try
        {
            packet = ParseJson<ChatResponse>(bytes);
        }
        catch (SerializationException)
        {
            throw new ModelReplyException("format", "模型响应不是约定的 Chat Completions JSON");
        }
        var choice = packet == null || packet.Choices == null ? null : packet.Choices.FirstOrDefault();
        if (choice == null || choice.Message == null || choice.Message.Role != "assistant")
            throw new ModelReplyException("format", "模型响应缺少标准的助手消息");
        if (choice.FinishReason != "stop")
            throw new ModelReplyException("finish", "模型未正常完成文本回答，已跳过");
        string text = choice.Message.Content == null ? null : choice.Message.Content.Trim();
        if (String.IsNullOrWhiteSpace(text)) throw new ModelReplyException("empty", "模型最终回答为空");
        if (Regex.IsMatch(text, @"<\s*/?\s*(?:think|analysis|reasoning)(?:\s|>|/)", RegexOptions.IgnoreCase))
            throw new ModelReplyException("thinking", "答案夹带思考标签，已跳过");
        if (text.Length > 2000)
            throw new ModelReplyException("length", "模型最终回答超出 2000 字，已跳过");
        return text;
    }

    private static T ParseJson<T>(byte[] bytes)
    {
        using (var buffer = new MemoryStream(bytes))
            return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(buffer);
    }

    public void Dispose() { client.Dispose(); }
}
