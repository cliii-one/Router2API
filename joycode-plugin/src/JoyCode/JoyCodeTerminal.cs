using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace JoyCode;

/// <summary>
/// 将 JoyCode（京东 AI 编程助手）的多模型账号接入 Router2API。
/// 插件负责 OAuth 凭据持久化、上游协议调用和 SSE 透传；
/// 宿主负责平台路由、策略执行和插件生命周期。
/// 上游协议参考 JoyCode2Api 项目（https://github.com/vibe-coding-labs/JoyCode2Api），
/// color gateway HMAC 签名逆向自 JoyCode 2.7.5 / joycoder-editor 3.8.57。
/// </summary>
/// <param name="host">提供账号存储、HTTP 客户端和插件日志等宿主能力。</param>
[PipelinePlugin(PipelineStage.Terminal, 100, nameof(JoyCodeTerminal))]
[PlatformAdapter("joycode", PluginKey = "joycode", DisplayName = "JoyCode",
    ProbeEndpoint = "https://api-ai.jd.com/api?appid=joycode_ide&functionId=joycode_userInfo")]
[ModelCache(TtlSeconds = 3600)]
[CredentialSchema(CredentialKind.Custom)]
public sealed partial class JoyCodeTerminal(IPluginHost host) : IPlatformTerminal, IPluginModule, IAsyncDisposable
{
    // ======== 上游常量（逆向自 JoyCode IDE 2.7.5）========

    private const string Platform = "joycode";
    private const string ClientVersion = "2.7.5";
    private const string UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) JoyCode/2.7.5 Chrome/133.0.0.0 Electron/35.2.0 Safari/537.36";

    /// <summary>color gateway 的 appid 与 HMAC 密钥（固定值，逆向自 JoyCode 客户端）。</summary>
    private const string ColorAppID = "joycode_ide";
    private const string ColorHMACKey = "0691a3f0b37b4a85aeb63ad0fc7db3ed";

    /// <summary>上游三个可用的 base URL；color gateway 是当前推荐入口。</summary>
    private const string DefaultBaseURL = "https://joycode-api.jd.com";
    private const string DefaultColorBaseURL = "https://api-ai.jd.com";

    /// <summary>color gateway 路径前缀。</summary>
    private const string ColorGatewayPath = "/api";

    /// <summary>旧 v1 端点 → (functionId, v2 路径) 映射。</summary>
    private static readonly Dictionary<string, (string FunctionID, string V2Path)> ColorEndpoints = new()
    {
        ["/api/saas/openai/v1/chat/completions"] = ("chat_completions", "/api/saas/openai/v2/chat/completions"),
        ["/api/saas/models/v1/modelList"] = ("joycode_modelList", "/api/saas/models/v2/modelList"),
        ["/api/saas/user/v1/userInfo"] = ("joycode_userInfo", "/api/saas/user/v2/userInfo"),
        ["/api/saas/anthropic/v1/messages"] = ("anthropic_completions", "/api/saas/anthropic/v1/messages"),
    };

    /// <summary>JoyCode 平台支持的模型清单。</summary>
    internal static readonly string[] SupportedModels =
    [
        "JoyAI-Code", "Claude-Opus-4.7", "MiniMax-M2.7", "Kimi-K2.6", "Kimi-K2.5",
        "GLM-5.1", "GLM-5", "GLM-4.7", "Doubao-Seed-2.0-pro",
    ];

    private const string DefaultModel = "JoyAI-Code-1.5";

    /// <summary>模型能力表（上下文窗口 / 最大输出 / 是否推理 / 是否视觉）。</summary>
    private static readonly Dictionary<string, (int Context, int MaxOut, bool Reasoning, bool Vision)> ModelCaps = new()
    {
        ["JoyAI-Code"] = (200000, 64000, false, false),
        ["Claude-Opus-4.7"] = (200000, 32000, false, false),
        ["MiniMax-M2.7"] = (200000, 16384, true, false),
        ["Kimi-K2.6"] = (200000, 16384, true, true),
        ["Kimi-K2.5"] = (200000, 16384, false, true),
        ["GLM-5.1"] = (200000, 16384, true, false),
        ["GLM-5"] = (200000, 8192, false, false),
        ["GLM-4.7"] = (200000, 8192, false, false),
        ["Doubao-Seed-2.0-pro"] = (200000, 16384, false, false),
    };

    // ======== 运行时状态 ========

    private readonly ConcurrentDictionary<string, byte> _balanceRefreshQueued = new();
    private readonly HttpClient _directClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    // ======== IPluginTerminal：模型发现 ========

    /// <summary>返回 JoyCode 支持的模型清单（静态声明，不从上游拉取）。</summary>
    public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(
        ModelQueryContext context, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var models = new List<ModelDescriptor>();
        foreach (var id in SupportedModels)
        {
            var cap = ModelCaps.GetValueOrDefault(id);
            models.Add(new ModelDescriptor(
                id, id, cap.Context, true, 0, cap.MaxOut,
                cap.Reasoning,
                cap.Reasoning ? new[] { "enabled" } : null,
                null));
        }
        return models;
    }

    /// <summary>检查 Custom 凭证是否包含必需的 ptKey 和 userId。</summary>
    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        Credential credential, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        if (credential is not CustomCredential custom)
            return new CredentialValidationResult(false, "JoyCode 需要 Custom 凭证（包含 ptKey 和 userId）");
        if (string.IsNullOrWhiteSpace(GetField(custom, "ptKey")))
            return new CredentialValidationResult(false, "缺少 ptKey（从 JoyCode IDE 的 state.vscdb 提取）");
        if (string.IsNullOrWhiteSpace(GetField(custom, "userId")))
            return new CredentialValidationResult(false, "缺少 userId");
        return new CredentialValidationResult(true, null);
    }

    // ======== IPluginModule：生命周期 ========

    /// <summary>声明账号筛选与上游重试策略。</summary>
    public void Configure(IPluginBuilder builder)
    {
        builder.AccountPolicy(policy => policy
            .SelectAccount(account =>
                account.Credential is CustomCredential custom && HasUsableCredentials(custom)));
        builder.ProxyPolicy(policy => policy
            .OnTransportFailure(() => new PluginAttemptDecision
            {
                FailureKind = PluginFailureKind.Transport,
                Retry = PluginRetryAction.NextAttempt,
                ProxyAction = PluginProxyAction.Cooldown,
                ReasonCode = "joycode.transport",
            })
            .MaxAttempts(3).AttemptTimeoutSeconds(60).TotalTimeoutSeconds(180));
    }

    /// <summary>初始化；无需要预热的持久会话。</summary>
    public ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <summary>释放 HTTP 资源。</summary>
    public ValueTask StopAsync(CancellationToken cancellationToken)
        => DisposeAsync();

    /// <summary>释放直接 HTTP 客户端。</summary>
    public async ValueTask DisposeAsync()
    {
        _directClient.Dispose();
        await Task.CompletedTask;
    }

    // ======== 内部工具 ========

    /// <summary>从 Custom 凭证中按 key 取字段。</summary>
    internal static string? GetField(CustomCredential credential, string key)
        => credential.Fields.TryGetValue(key, out var value) ? value : null;

    /// <summary>检查 Custom 凭证是否包含 ptKey 和 userId。</summary>
    internal static bool HasUsableCredentials(CustomCredential credential)
        => !string.IsNullOrWhiteSpace(GetField(credential, "ptKey"))
        && !string.IsNullOrWhiteSpace(GetField(credential, "userId"));

    /// <summary>读取 Custom 凭证中的 tenant 字段，缺省返回 JOYCODE。</summary>
    internal static string GetTenant(CustomCredential credential)
        => GetField(credential, "tenant") is { Length: > 0 } t ? t : "JOYCODE";

    /// <summary>读取 Custom 凭证中的 colorBaseURL，缺省返回默认网关。</summary>
    internal static string GetColorBaseURL(CustomCredential credential)
        => GetField(credential, "colorBaseURL") is { Length: > 0 } u ? u : DefaultColorBaseURL;
}
