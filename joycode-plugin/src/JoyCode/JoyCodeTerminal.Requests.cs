using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;

namespace JoyCode;

/// <summary>JoyCodeTerminal 的请求转发、SSE 透传与错误决策实现。</summary>
public sealed partial class JoyCodeTerminal
{
    // ======== color gateway 签名 ========

    /// <summary>
    /// 构造 color gateway 的 query 串与 HMAC-SHA256 签名。
    /// 规范串 = appid &amp; functionId &amp; timestamp（按 key 排序后的 value 拼接）。
    /// </summary>
    private static (string Query, string Sign) ColorSign(string functionID)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signStr = $"{ColorAppID}&{functionID}&{ts}";
        var hmac = (byte[])null!;
        using (var mac = new HMACSHA256(Encoding.UTF8.GetBytes(ColorHMACKey)))
            hmac = mac.ComputeHash(Encoding.UTF8.GetBytes(signStr));
        var sign = Convert.ToHexString(hmac).ToLowerInvariant();
        var query = $"appid={ColorAppID}&functionId={functionID}&t={ts}";
        return (query, sign);
    }

    /// <summary>根据端点路径解析最终请求 URL（gateway 签名模式或 direct v2 模式）。</summary>
    private static string RequestURL(CustomCredential credential, string endpoint)
    {
        if (!ColorEndpoints.TryGetValue(endpoint, out var ep))
            return DefaultBaseURL + endpoint;
        var colorBase = GetColorBaseURL(credential);
        var (query, sign) = ColorSign(ep.FunctionID);
        return $"{colorBase}{ColorGatewayPath}?{query}&sign={sign}";
    }

    /// <summary>构建 JoyCode 上游请求体的公共字段（tenant/userId/client 等）。</summary>
    private static JsonObject PrepareBody(CustomCredential credential, JsonObject? extra = null)
    {
        var body = new JsonObject
        {
            ["tenant"] = GetTenant(credential),
            ["orgFullName"] = GetField(credential, "orgFullName") ?? "",
            ["userId"] = GetField(credential, "userId") ?? "",
            ["client"] = "JoyCode",
            ["clientVersion"] = ClientVersion,
            ["language"] = "UNKNOWN",
        };
        if (extra is not null)
            foreach (var (key, value) in extra)
                body[key] = value.DeepClone();
        return body;
    }

    /// <summary>构建 JoyCode 上游请求头。</summary>
    private static HttpRequestMessage BuildRequest(
        CustomCredential credential, HttpMethod method, string url, JsonObject body, bool streaming = false)
    {
        var request = new HttpRequestMessage(method, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var ptKey = GetField(credential, "ptKey") ?? "";
        var loginType = GetField(credential, "loginType") ?? "N_PIN_PC";
        request.Headers.TryAddWithoutValidation("source-type", "joycoder-ide");
        request.Headers.TryAddWithoutValidation("ptKey", ptKey);
        request.Headers.TryAddWithoutValidation("loginType", loginType);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.AcceptEncoding.ParseAdd(streaming ? "identity" : "gzip, deflate");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
        return request;
    }

    // ======== IPlatformTerminal.InvokeAsync ========

    /// <summary>一次模型生成：构建请求 → 发送 → SSE 透传或缓冲 → 返回结果。</summary>
    public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
    {
        var result = await InvokeCoreAsync(context);
        var decision = result.Attempt.Decision
            ?? (result.Response.IsSuccess ? new() : FailureAttempt(result.Attempt.StatusCode));
        return result with { Attempt = decision.ToResult(result.Attempt.StatusCode, result.Attempt.Reason) };
    }

    private async Task<PluginInvocationResult> InvokeCoreAsync(PluginAttemptContext context)
    {
        var request = context.Request;
        var account = context.Account;

        if (account.Credential is not CustomCredential credential || !HasUsableCredentials(credential))
            return new(
                AdapterResponse.Unauthorized("JoyCode 账号缺少 Custom 凭证（需要 ptKey 和 userId）"),
                new(PluginAttemptOutcome.DisableAccount, 401, false, true, "joycode-missing-credential"),
                null);

        var model = NormalizeModel(request.Model);
        var endpoint = request.Endpoint;

        // 构建上游请求体：保留原始请求体中宿主无法归类的参数（tools/tool_choice/stop 等）
        var upstreamBody = PrepareBody(credential, new JsonObject
        {
            ["model"] = model,
            ["stream"] = request.Stream,
            ["messages"] = BuildMessagesJson(request.Messages),
        });
        if (request.MaxTokens is { } maxTokens) upstreamBody["max_tokens"] = maxTokens;
        if (request.Temperature is { } temp) upstreamBody["temperature"] = temp;

        // 透传原始请求体中的可选字段
        if (request.OriginalBody is { } original)
        {
            CopyIfPresent(original, upstreamBody, "top_p", "tools", "tool_choice", "stop", "thinking");
        }

        // 确定上游端点：Claude 模型走 Anthropic 通道，其余走 OpenAI 兼容
        var isAnthropic = endpoint.Contains("messages", StringComparison.OrdinalIgnoreCase)
                       || model.StartsWith("Claude", StringComparison.OrdinalIgnoreCase);
        var upstreamEndpoint = isAnthropic
            ? "/api/saas/anthropic/v1/messages"
            : "/api/saas/openai/v1/chat/completions";

        var url = RequestURL(credential, upstreamEndpoint);
        using var httpRequest = BuildRequest(credential, HttpMethod.Post, url, upstreamBody,
            streaming: request.Stream);

        HttpResponseMessage response;
        try
        {
            response = await context.HttpClient.SendAsync(httpRequest,
                useProxyPool: false, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
        }
        catch (HttpRequestException ex)
        {
            await LogAsync(context, "upstream.transport_error", $"JoyCode 上游连接失败: {ex.Message}", "Error");
            return new(
                AdapterResponse.ServerError($"JoyCode 上游连接失败: {ex.Message}"),
                new(PluginAttemptOutcome.Retry, null, true, false, "joycode-transport"),
                null);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(context.CancellationToken);
            response.Dispose();
            await LogAsync(context, "upstream.error",
                $"JoyCode 上游返回 {response.StatusCode}", "Error",
                statusCode: (int)response.StatusCode, details: errorBody[..Math.Min(errorBody.Length, 500)]);
            return new(
                AdapterResponse.ServerError($"JoyCode 上游返回 {(int)response.StatusCode}: {errorBody[..Math.Min(errorBody.Length, 300)]}"),
                FailureAttempt((int)response.StatusCode).ToResult((int)response.StatusCode, "joycode-upstream-error"), null);
        }

        // ======== SSE 透传（JoyCode 的 SSE 已经是 OpenAI 兼容格式）========
        if (request.Stream)
        {
            var stream = ReadSseStreamAsync(response, context.CancellationToken);
            return new(
                new AdapterResponse
                {
                    StatusCode = 200,
                    IsRawPassthrough = true,
                    RawStream = stream,
                    ContentType = "text/event-stream",
                    Lifetime = new StreamLifetime(response),
                },
                new(PluginAttemptOutcome.Healthy, 200, false, false, null),
                null);
        }

        // ======== 非流式：读全量 JSON ========
        var content = await response.Content.ReadAsStringAsync(context.CancellationToken);
        response.Dispose();
        return new(
            new AdapterResponse
            {
                StatusCode = 200,
                IsRawPassthrough = true,
                RawContent = Encoding.UTF8.GetBytes(content),
                ContentType = "application/json",
            },
            new(PluginAttemptOutcome.Healthy, 200, false, false, null),
            null);
    }

    /// <summary>将上游 SSE 流包装为 IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;，取消时正确释放 response。</summary>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadSseStreamAsync(
        HttpResponseMessage response,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // JoyCode 的 SSE 已经是 OpenAI 兼容格式，直接逐块透传。
        // Router2API 的 raw 透传机制会原样把字节发给下游。
        var buffer = new byte[16384];
        await using var stream = response.Content.ReadAsStream(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0) break;
            yield return new ReadOnlyMemory<byte>(buffer, 0, read);
        }
        response.Dispose();
    }

    /// <summary>包装 HttpResponseMessage，在宿主释放 StreamChunk 时关闭底层连接。</summary>
    private sealed class StreamLifetime(HttpResponseMessage response) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            response.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>把 AdapterMessage[] 序列化为 JoyCode 兼容的 messages JSON 数组。</summary>
    private static JsonArray BuildMessagesJson(IReadOnlyList<AdapterMessage> messages)
    {
        var array = new JsonArray();
        foreach (var message in messages)
        {
            var node = new JsonObject { ["role"] = message.Role };
            if (!string.IsNullOrEmpty(message.Content))
                node["content"] = message.Content;
            else if (message.ContentParts is { Count: > 0 })
            {
                var parts = new JsonArray();
                foreach (var part in message.ContentParts)
                {
                    var p = new JsonObject { ["type"] = part.Kind.ToString().ToLowerInvariant() };
                    if (part.Text is not null) p["text"] = part.Text;
                    if (part.ImageUrl is not null) p["image_url"] = new JsonObject { ["url"] = part.ImageUrl };
                    parts.Add(p);
                }
                node["content"] = parts;
            }
            if (message.ToolCallId is not null) node["tool_call_id"] = message.ToolCallId;
            if (message.ToolCalls is { Count: > 0 })
            {
                var calls = new JsonArray();
                foreach (var call in message.ToolCalls)
                    calls.Add(JsonNode.Parse(JsonSerializer.Serialize(call)));
                node["tool_calls"] = calls;
            }
            array.Add(node);
        }
        return array;
    }

    /// <summary>从原始请求体中拷贝存在的可选字段到上游请求体。</summary>
    private static void CopyIfPresent(JsonElement source, JsonObject target, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (source.TryGetProperty(key, out var value))
                target[key] = JsonNode.Parse(value.GetRawText());
        }
    }

    /// <summary>标准化模型名：空或未知时回退默认模型。</summary>
    private static string NormalizeModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return DefaultModel;
        var name = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        return SupportedModels.Contains(name, StringComparer.OrdinalIgnoreCase) ? name : DefaultModel;
    }

    // ======== 决策逻辑 ========

    /// <summary>根据上游响应状态码映射为结构化决策。</summary>
    private static PluginAttemptDecision FailureAttempt(int? statusCode)
    {
        var invalidCredential = statusCode is 401 or 403;
        var retryable = statusCode is 408 or 425 or 429 or >= 500;
        return new PluginAttemptDecision
        {
            FailureKind = invalidCredential ? PluginFailureKind.InvalidCredential : PluginFailureKind.Upstream,
            Retry = retryable ? PluginRetryAction.NextAttempt : PluginRetryAction.None,
            AccountAction = invalidCredential ? PluginAccountAction.Disable
                          : statusCode == 429 ? PluginAccountAction.Cooldown : PluginAccountAction.None,
            AccountCooldownUntil = statusCode == 429 ? DateTimeOffset.UtcNow.AddMinutes(5) : null,
            ProxyAction = PluginProxyAction.None,
            ReasonCode = "joycode.upstream",
        };
    }

    /// <summary>写插件日志（不因日志服务故障影响请求处理）。</summary>
    private async Task LogAsync(PluginAttemptContext context, string eventType, string message,
        string level = "Debug", int? statusCode = null, string? details = null)
    {
        try
        {
            await host.Services.Log.WriteAsync(new PluginLog
            {
                PluginKey = "joycode",
                EventType = eventType,
                Message = message,
                Level = level,
                TraceId = context.TraceId,
                AccountId = context.Account?.Id,
                Model = context.Request?.Model,
                StatusCode = statusCode,
                DetailsJson = details,
            });
        }
        catch { }
    }
}
