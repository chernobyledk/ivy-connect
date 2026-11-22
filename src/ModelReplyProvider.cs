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
        [DataMember(Name = "max_tokens")] internal int MaxTokens = 700;
        [DataMember(Name = "thinking")] internal Thinking ThinkingMode = new Thinking();
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

    private readonly HttpClient client;
    private readonly PrivateModelSettings settings;
    private readonly Uri endpoint;
    private readonly Func<PrivateModelTurn[]> context;
    private readonly Action<string> log;

    internal DeepSeekPrivateReply(PrivateModelSettings settings, Func<PrivateModelTurn[]> context, Action<string> log)
    {
        this.settings = settings;
        this.context = context;
        this.log = log;
        endpoint = settings.Validate();
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
                            new ChatRequest { Model = settings.Model, Messages = messages });
                        request.Content = new ByteArrayContent(data.ToArray());
                    }
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) throw StatusError(response.StatusCode);
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
            using (var buffer = new MemoryStream(bytes))
                packet = (ChatResponse)new DataContractJsonSerializer(typeof(ChatResponse)).ReadObject(buffer);
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

    public void Dispose() { client.Dispose(); }
}
