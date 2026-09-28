# WorkBuddy 插件反编译分析报告

> 来源：`/vol2/1000/05-DeepSeek/Router2API/workbuddy/Plugins.WorkBuddy.dll`（v2.0.1.0）
> 反编译工具：ILSpy 11.1（ilspycmd），完整还原 C# 源码 5,522 行
> 插件类型：**C# DLL**（runtime=dotnet），引用 Router.Contracts 2.0.0
> 反编译产物：`Router2API/workbuddy-decompiled/WorkBuddyTerminal.cs`

---

## 一、插件概述

WorkBuddy 是腾讯的一个 AI 编程助手产品（类似 Copilot），提供国内站（copilot.tencent.com / codebuddy.cn）和国际站（workbuddy.ai）两个版本。这个插件把 WorkBuddy 的 OAuth 账号接入 Router2API，使其模型（如 Claude/GPT 系列）可以通过 Router2API 的 `/v1` 接口对外提供服务。

**插件职责**：OAuth 认证、账号持久化、协议转换（OpenAI↔WorkBuddy）、以及大量的自动化任务（签到/积分/成长/旅行等）

**上游端点**：

| 站点 | API Base | 认证 Base |
|------|----------|-----------|
| 国内站 | `https://copilot.tencent.com` | `https://www.codebuddy.cn` |
| 国际站 | `https://www.workbuddy.ai` | `https://www.workbuddy.ai` |

**User-Agent 伪装**：
- 聊天：`WorkBuddy/5.5.4 WorkBuddy/5.5.4 CLI/2.137.1`
- OAuth：`CLI/2.63.2 CodeBuddy/2.63.2`
- IDE：`CodeBuddyIDE/4.12.0 CodeBuddy/4.12.0`
- 成长任务桌面版：`WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1`

---

## 二、入口与声明

```csharp
[PipelinePlugin(PipelineStage.Terminal, 100, "WorkBuddyTerminal")]
[PlatformAdapter("workbuddy", PluginKey = "workbuddy",
    DisplayName = "WorkBuddy", ProbeEndpoint = "https://copilot.tencent.com/v3/config")]
[ModelCache(TtlSeconds = 3600)]                          // 模型缓存 1 小时
[CredentialSchema(CredentialKind.OAuth)]                 // 仅接受 OAuth 凭证
public sealed class WorkBuddyTerminal
    : IPlatformTerminal, IPluginModule, IPluginMainPageProvider, IAsyncDisposable
```

**与 Contracts 的对应关系**：
- `IPlatformTerminal` = 必选三方法（InvokeAsync/GetModelsAsync/ValidateCredentialAsync）
- `IPluginModule` = 可选生命周期（Configure/StartAsync/StopAsync）
- `IPluginMainPageProvider` = 可选管理页
- `IAsyncDisposable` = 资源清理
- `[ModelCache(TtlSeconds=3600)]` = 模型缓存 1 小时（默认 300s）
- `[CredentialSchema(OAuth)]` = 只接受 OAuth 凭证

---

## 三、策略声明（Configure）

```csharp
public void Configure(IPluginBuilder builder)
{
    builder.AccountPolicy(policy => {
        // 只选 OAuth 且有可用 token 的账号
        policy.SelectAccount(account => 
            account.Credential is OAuthCredential oauth && HasUsableToken(oauth));
        // 按请求的 realm（cn/global）匹配账号
        policy.SelectForRequest(MatchesRequestedRealm);
        // 优先选择积分包到期早的账号
        policy.PreferEarlier(GetPreferredCreditExpiry);
        // 按账号亲和度加权
        policy.WeightBy(WorkBuddyAccountAffinityWeight);
    });
    builder.ProxyPolicy(policy => {
        policy.OnTransportFailure(() => 
            FailureDecision(new PluginAttemptResult(Outcome.Retry, IsTransportFailure: true)));
        policy.MaxAttempts(4).AttemptTimeoutSeconds(60).TotalTimeoutSeconds(180);
    });
}
```

---

## 四、模型发现（GetModelsAsync）

按已登录的国内/国际账号分组发现模型，然后按版本标记合并返回：

1. `host.Accounts.ListAsync("workbuddy")` 获取全部账号
2. 过滤：OAuth 凭证可用 + 状态非 Invalid/Disabled
3. 按 `realm`（cn/global）分组
4. 对每组选凭据最新的账号 → 用其 access_token 调上游模型列表 API
5. 合并去重后返回 `ModelDescriptor[]`

每个模型返回：`Id / DisplayName / ContextWindow / InputLimit / OutputLimit / SupportsReasoning / ReasoningLevels / CreditMultiplier`（积分倍率）。

---

## 五、模型调用（InvokeAsync → InvokeCoreAsync）

### 5.1 InvokeAsync 外壳

```csharp
public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
{
    var result = await InvokeCoreAsync(context);
    var decision = result.Attempt.Decision 
        ?? (result.Response.IsSuccess ? new() : FailureDecision(result.Attempt));
    return result with { Attempt = decision.ToResult(...) };
}
```

### 5.2 InvokeCoreAsync 核心流程

```
1. 验证凭证：OAuth 凭证存在且有 token → 否则 401
2. Token 续期：RefreshIfNeededAsync（过期或临近过期时自动刷新）
3. 检查账号是否被禁用
4. 构建请求体：BuildChatRequestBody(request, model)
5. 发送：SendChatRequestAsync(body)
6. 处理响应：
   - 非 2xx → 缓冲错误体
   - 内容策略拦截（特定错误码）→ 替换 system prompt 重试一次
   - HttpRequestException → 传输错误决策
7. 流式 → 逐帧解析 SSE 转 StreamChunk
8. 非流式 → 聚合完整响应
```

### 5.3 请求体构建（BuildChatRequestBody）

将宿主标准化请求转换为 WorkBuddy 格式：

```csharp
var body = new JsonObject {
    ["model"] = model,
    ["messages"] = messages,   // developer→system 映射
    ["stream"] = true,
    ["stream_options"] = new { include_usage = true },
    // max_tokens / temperature 按需
};
```

消息转换：`AdapterMessage` → WorkBuddy 格式（处理 role 映射、tool_calls、reasoning_content、多模态 ContentParts）。

### 5.4 发送（SendChatRequestAsync）

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/v2/chat/completions")
{
    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
};
ApplyHeaders(request, credential, includeAuthorization: true, streaming: true);
ApplyConversationHeaders(request, context);
return await context.HttpClient.SendAsync(request, false,    // 不走代理池重试
    HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);  // SSE 用 HeadersRead
```

**关键**：`HttpCompletionOption.ResponseHeadersRead` + `useProxyPool: false`——用当前 attempt 绑定的 client，不套额外 retry。

### 5.5 SSE 解析（ReadStreamAsync）

逐行读取上游 SSE `data:` 消息，转换为宿主 `StreamChunk`：

```csharp
private async IAsyncEnumerable<StreamChunk> ReadStreamAsync(response, account, context, ct)
{
    // 逐帧解析：
    // content 增量 → yield new StreamChunk(delta, ...)
    // reasoning 增量 → yield new StreamChunk(reasoningDelta: ...)
    // tool_call 增量 → yield new StreamChunk(toolCalls: ...)
    // usage → yield new StreamChunk(usage: ...)
    // [DONE] → yield new StreamChunk(finishReason: "stop")
    // 上游错误 → yield new StreamChunk(error: "...", errorType: "upstream_error")
    // EOF 但没 [DONE] → yield new StreamChunk(error:"...", errorType:"upstream_incomplete_response")
}
```

### 5.6 决策表（FailureDecision）

```
401 → InvalidCredential + DisableAccount + NextAttempt
408/425/429/≥500 → Upstream + CooldownAccount(5min) + NextAttempt
传输失败 → Transport + CooldownNode + NextAttempt
其他 → Upstream + 无惩罚
```

特殊逻辑：
- **12153 session dead**：连续 3 次 → 永久停用账号
- **积分耗尽**：冷却到本地次日 04:00
- **内容策略拦截**：替换 system prompt 后重试一次

---

## 六、OAuth 认证流程

### 6.1 开始授权（POST oauth/start）

```csharp
POST {base}/v2/plugin/auth/state?platform=CLI
→ 返回 { state, authUrl }
→ 宿主暂存 _pendingLogins[state] = {时间, realm}
→ 返回给前端 { ok, state, url, realm }
```

前端打开 `authUrl` 让用户在官方页面登录。

### 6.2 轮询授权结果（GET oauth/poll?state=xxx）

前端每 2.5 秒轮询，宿主检查上游授权状态：
- 完成 → 读取 UID/账号信息 → 安全校验 UID → 新增或更新账号 → 返回 `{done, uid, nickname, ...}`
- 未完成 → 返回 `{done: false}`

### 6.3 Token 刷新（RefreshCredentialAsync）

```csharp
POST {base}/v2/plugin/auth/token/refresh
Body: { refresh_token }
→ 返回 { accessToken, refreshToken, domain, expiresIn }
→ 构建新 OAuthCredential（缺失字段回退旧值）
```

由宿主的 `RefreshCredentialAsync(id, refresh)` 调用——宿主串行化+CAS 保护，避免并发刷新互相覆盖。

---

## 七、定时任务（9 个 ScheduledTask）

| 任务名 | Cron | 频率 | 用途 |
|--------|------|------|------|
| workbuddy-token-refresh | `0 0 * * * *` | 每小时 | 刷新 OAuth access_token |
| workbuddy-keepalive | `0 0 22 * * *` | 每天 22:00 | 保活 |
| workbuddy-balance-refresh | `0 */5 * * * *` | 每 5 分钟 | 刷新积分余额 |
| workbuddy-daily-checkin | `0 0 9,21 * * *` | 每天 9:00/21:00 | 国内站签到（国际站跳过） |
| workbuddy-activity | `0 0 10 * * *` | 每天 10:00 | 对话活跃上报 |
| workbuddy-travel | `0 0 9,21 * * *` | 每天 9:00/21:00 | Buddy 领养/旅行/领奖 |
| workbuddy-growth-tasks | `0 15 9 * * *` | 每天 9:15 | 成长任务接受/领奖 |
| workbuddy-streak-rewards | `0 20 9 * * *` | 每天 9:20 | 连续签到奖励兑换 |
| workbuddy-night-tasks | `0 0 23 * * *` | 每天 23:00 | 夜间对话补量 |

每个任务通过 `RunForAccountsAsync` 按账号顺序执行：按 realm（cn/global）过滤 → 单个失败写日志隔离，不阻塞后续。

---

## 八、管理页面

内嵌 HTML（`GetMainPage()` 返回完整自包含页面），功能：
- OAuth 一键登录（选择国内/国际站）
- 已保存账号列表（含积分余额条、积分包明细）
- 模型目录展示
- 手动触发 9 个定时任务

页面通过 `window.Router2API.request(method, route, body)` 与宿主通信。

---

## 九、与 Contracts 契约的完整对应

| 契约元素 | WorkBuddy 中的使用 |
|----------|-------------------|
| `IPlatformTerminal.InvokeAsync` | 一次模型生成（OAuth→构建→发送→SSE→决策） |
| `IPlatformTerminal.GetModelsAsync` | 按 realm 分组发现模型 |
| `IPlatformTerminal.ValidateCredentialAsync` | 检查 OAuth 凭证（不访问上游） |
| `IPluginModule.Configure` | 声明策略 |
| `IPluginModule.StartAsync` | 初始化会话+预热模型 |
| `IPluginModule.StopAsync` | 停止会话+取消后台任务 |
| `IPluginMainPageProvider.GetMainPage` | 管理页面 |
| `IAsyncDisposable.DisposeAsync` | 资源清理 |
| `host.Accounts.ListAsync` | 获取本插件账号 |
| `host.Accounts.SaveAsync` | OAuth 后保存账号 |
| `host.Accounts.RefreshCredentialAsync` | CAS 安全刷新 token |
| `host.Accounts.SetCooldownAsync` | 积分耗尽冷却 |
| `host.Accounts.DisableAsync` | 12153 连续失败停用 |
| `host.Models.ListAsync/RefreshAsync` | 模型发现 |
| `host.Log.WriteAsync` | 结构化日志（事件类型+traceId+账号） |
| `context.HttpClient.SendAsync` | 发送上游请求（attempt 绑定代理） |
| `AdapterResponse.Stream` | IAsyncEnumerable\<StreamChunk\> SSE |
| `AdapterResponse.Lifetime` | 流资源回收 |
| `PluginAttemptDecision` | 结构化决策（冷却/禁用/重试/代理动作） |
| `[ScheduledTask]` | 9 个 Cron 任务 |

---

## 十、反编译方法备忘

```bash
# 1. 安装 .NET SDK（NAS 上无 dotnet，需手动下载解包到 tmpfs）
curl -sL -o sdk.tar.gz "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-linux-arm64.tar.gz"
mkdir -p /tmp/dotnet-sdk && tar xzf sdk.tar.gz -C /tmp/dotnet-sdk

# 2. 安装 ilspycmd（反编译器）
export PATH="/tmp/dotnet-sdk:$PATH" && export DOTNET_ROOT=/tmp/dotnet-sdk
export HOME=/tmp/dotnet-home && mkdir -p $HOME
dotnet tool install --global ilspycmd

# 3. 反编译 DLL → C# 源码
export PATH="$HOME/.dotnet/tools:$PATH"
ilspycmd /path/to/Plugins.WorkBuddy.dll -o /tmp/decompiled -p --nested-directories
```

**注意事项**：
- NAS 上 `/vol2` 是 btrfs，大文件 cp 可产出 0 字节文件 → SDK 必须装到 /tmp（tmpfs）
- /tmp 空间有限（1.8G），SDK 232MB 解压后约 1.2G，可能需要先清理
- `dotnet tool install` 在 HOME 指向只读共享目录时会静默失败 → 必须设 HOME 到可写位置
- DLL 自带 PDB 调试符号 → 反编译可恢复**全部局部变量名和行号**，质量极高
- XML 文档（134 个成员注释）无需反编译即可提取，是最快了解插件功能的入口
