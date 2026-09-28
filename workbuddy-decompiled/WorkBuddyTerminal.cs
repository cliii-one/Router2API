using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

namespace Plugins.WorkBuddy;

/// <summary>
/// 将 WorkBuddy / CodeBuddy OAuth 账号接入 Router2API 聊天终端。
/// 插件负责 OAuth、账号持久化、协议转换和账号任务；宿主负责平台路由、策略执行和插件生命周期。
/// 国内与国际账号共用平台标识，实际站点由 OAuth 返回的 domain 决定。
/// 成长任务事件头与请求体按 MIT 参考项目 workbuddy2api-panel 的任务协议适配，参考版本为
/// dbd7c6800ed8071d7dd617d456b6041294781fee（https://github.com/linguo2625469/workbuddy2api-panel）。
/// 上游内容策略误拦截时的中性 system prompt 单次重试也参考该版本的 handler.go 与 prompt.go。
/// WorkBuddy 请求兼容规则参考 xiaofan6ya/workbuddy2api upstream_compat.py，提交 bc173d82670dee4ef9761e78b274b7157ddb08b6。
/// 空 function_call 与会话前缀粘性参考 ardeyouxipianyi/workbuddy2api-hub PR #4，合并提交 0914e4a4b0750c6e2cc8b62cbc70de822b1cebe2；参考仓库 main HEAD 为 650b65945e2772517ab3e8bd4dc30b57b0019461。
/// </summary>
/// <param name="host">提供账号存储、模型目录、HTTP 客户端、任务日志及其他宿主服务。</param>
[PipelinePlugin(/*Could not decode attribute arguments.*/)]
[PlatformAdapter("workbuddy", PluginKey = "workbuddy", DisplayName = "WorkBuddy", ProbeEndpoint = "https://copilot.tencent.com/v3/config")]
[ModelCache(TtlSeconds = 3600)]
[CredentialSchema(/*Could not decode attribute arguments.*/)]
public sealed class WorkBuddyTerminal : IPlatformTerminal, IPluginModule, IPluginMainPageProvider, IAsyncDisposable
{
	private sealed record GrowthRunSnapshot(string? TaskId, string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, string? Error)
	{
		public static GrowthRunSnapshot Idle { get; } = new GrowthRunSnapshot(null, "Idle", null, null, null);
	}

	private sealed record ManualTaskRunSnapshot(string TaskId, string Task, string Status, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string? Error);

	private sealed record GlobalAccountResponse(int Code, string Message, JsonNode? Data);

	private sealed record GrowthTaskSnapshot(string TaskCode, string? Status, string? AcceptStatus, bool Locked, long Current, long Target)
	{
		public bool Claimed
		{
			get
			{
				if (!string.Equals(AcceptStatus, "claimed", StringComparison.OrdinalIgnoreCase))
				{
					return string.Equals(Status, "claimed", StringComparison.OrdinalIgnoreCase);
				}
				return true;
			}
		}

		public bool IsClaimable
		{
			get
			{
				if (!Claimed)
				{
					if (!string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase) && !string.Equals(Status, "complete", StringComparison.OrdinalIgnoreCase))
					{
						if (Target > 0)
						{
							return Current >= Target;
						}
						return false;
					}
					return true;
				}
				return false;
			}
		}
	}

	private sealed record GrowthExpert(string Id, string Type, string Name, string Profession, string Version, JsonArray Categories);

	/// <summary>尚未完成的 OAuth 流程状态及其创建时间和目标站点。</summary>
	/// <param name="CreatedAt">插件创建并缓存该 state 的 UTC 时间。</param>
	/// <param name="Realm">授权所用站点，值为规范化后的 cn 或 global。</param>
	private sealed record PendingLogin(DateTimeOffset CreatedAt, string Realm);

	private sealed record WorkBuddyModelReference(string? Realm, string ModelId)
	{
		public string CacheKey
		{
			get
			{
				if (Realm != null)
				{
					return QualifiedModelId(Realm, ModelId);
				}
				return ModelId;
			}
		}
	}

	private sealed record RealmModelResult(string Realm, IReadOnlyList<ModelDescriptor> Models, string? Error);

	/// <summary>统一封装上游 JSON 结果、HTTP 状态、可读消息和业务成功标志。</summary>
	/// <param name="StatusCode">上游返回的 HTTP 状态码。</param>
	/// <param name="Data">解包后的业务数据节点；无效或缺失时为空。</param>
	/// <param name="Message">业务消息或未解析的原始响应文本。</param>
	/// <param name="Success">HTTP 和上游业务码是否均表示成功。</param>
	private sealed record UpstreamResult(int StatusCode, JsonNode? Data, string Message, bool Success);

	private sealed class WorkBuddyUpstreamException(int statusCode, string message) : InvalidOperationException(message)
	{
		public int StatusCode { get; } = statusCode;
	}

	/// <summary>保留模型目录接口的成功状态，避免把鉴权失败误缓存为空模型列表。</summary>
	private sealed record ModelFetchResult(bool Success, int StatusCode, ModelDescriptor[] Models);

	/// <summary>积分条中一个真实积分包的可用积分、已用积分、总量及有效期。</summary>
	private sealed record CreditPackageSnapshot(string Name, decimal Remain, decimal Used, decimal Total, DateTimeOffset? ExpiresAt);

	/// <summary>单个账号最近一次余额刷新得到的套餐容量快照。</summary>
	/// <param name="Remain">所有套餐剩余容量之和。</param>
	/// <param name="Used">所有套餐已用容量之和。</param>
	/// <param name="Size">所有套餐容量总和。</param>
	/// <param name="Packages">上游返回并纳入汇总的套餐数量。</param>
	/// <param name="UpdatedAt">最近一次成功数据的时间；首次失败时为失败快照创建时间。</param>
	/// <param name="Error">刷新失败原因；成功快照为空。</param>
	/// <param name="PackageDetails">用于按积分占比展示条纹和到期提示的积分包明细。</param>
	private sealed record BalanceSnapshot(decimal? Remain, decimal? Used, decimal? Size, int? Packages, DateTimeOffset UpdatedAt, string? Error, CreditPackageSnapshot[] PackageDetails)
	{
		/// <summary>最近一次查询尝试的时间；失败退避不能延长旧数据的优先排序有效期。</summary>
		public DateTimeOffset LastAttemptAt { get; init; } = UpdatedAt;

		/// <summary>移除已到期积分包并重算余额和明细；缓存读取时也检查，不延长数据新鲜度。</summary>
		/// <param name="now">本次判断使用的时间；到期时间等于此时也视为过期。</param>
		/// <returns>无过期包时返回原快照，否则返回保留原时间和错误信息的新快照。</returns>
		public BalanceSnapshot WithoutExpiredPackages(DateTimeOffset now)
		{
			CreditPackageSnapshot[] array = PackageDetails.Where((CreditPackageSnapshot package) => !package.ExpiresAt.HasValue || package.ExpiresAt > now).ToArray();
			if (array.Length == PackageDetails.Length)
			{
				return this;
			}
			return this with
			{
				Remain = array.Sum((CreditPackageSnapshot package) => package.Remain),
				Used = array.Sum((CreditPackageSnapshot package) => package.Used),
				Size = array.Sum((CreditPackageSnapshot package) => package.Total),
				Packages = array.Length,
				PackageDetails = array
			};
		}

		/// <summary>创建保留失败时间和错误原因、但不伪造余额值的快照。</summary>
		public static BalanceSnapshot Failed(string message)
		{
			return new BalanceSnapshot(null, null, null, null, DateTimeOffset.UtcNow, message, Array.Empty<CreditPackageSnapshot>());
		}
	}

	private static readonly TimeSpan BalanceRefreshInterval = TimeSpan.FromMinutes(5L);

	private static readonly TimeSpan BalancePriorityMaxAge = TimeSpan.FromMinutes(15L);

	private const string Platform = "workbuddy";

	private const string CnBase = "https://copilot.tencent.com";

	private const string GlobalBase = "https://www.workbuddy.ai";

	private const string CnBillingBase = "https://www.codebuddy.cn";

	private const string GlobalBillingBase = "https://www.workbuddy.ai";

	private const string CnOrigin = "https://www.codebuddy.cn";

	private const string GlobalOrigin = "https://www.workbuddy.ai";

	private const string WorkBuddyUserAgent = "WorkBuddy/5.5.4 WorkBuddy/5.5.4 CLI/2.137.1";

	private const string OAuthUserAgent = "CLI/2.63.2 CodeBuddy/2.63.2";

	private const string IdeUserAgent = "CodeBuddyIDE/4.12.0 CodeBuddy/4.12.0";

	private static readonly TimeSpan LoginTtl = TimeSpan.FromMinutes(10L);

	private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2L);

	private const string SessionDeadReasonPrefix = "WorkBuddy 12153 session dead: ";

	private const string SessionDeadDisabledReason = "WorkBuddy 12153 连续失败达到 3 次，账号已停用；请重新 OAuth 登录恢复";

	private const string HardCreditCooldownReason = "WorkBuddy 积分耗尽，冷却至本地次日 04:00";

	private const int SessionDeadThreshold = 3;

	private static readonly string[] HardCreditErrorMarkers = new string[13]
	{
		"credit exhausted", "credits exhausted", "insufficient credit", "insufficient credits", "insufficient quota", "quota exceeded", "insufficient balance", "credit is not enough", "积分不足", "积分已用尽",
		"额度不足", "额度已用尽", "余额不足"
	};

	private readonly IPluginServices _host;

	private readonly ConcurrentDictionary<string, PendingLogin> _pendingLogins;

	private readonly ConcurrentDictionary<string, BalanceSnapshot> _balanceCache;

	private readonly object _balanceRefreshLock;

	private readonly Dictionary<string, Task<Exception?>> _balanceRefreshTasks;

	private readonly SemaphoreSlim _balanceRefreshSlots;

	private readonly CancellationTokenSource _balanceShutdown;

	private Task? _balanceStopTask;

	private bool _balanceStopping;

	private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _modelReasoningLevels;

	private readonly object _manualTaskRunLock;

	private readonly Dictionary<string, ManualTaskRunSnapshot> _manualTaskRunSnapshots;

	private readonly Dictionary<string, string> _activeManualTaskRunIds;

	private readonly Dictionary<string, CancellationTokenSource> _manualTaskRunCancellations;

	private readonly Dictionary<string, Task> _manualTaskRunTasks;

	private readonly object _growthRunLock;

	private GrowthRunSnapshot _growthRunSnapshot;

	private CancellationTokenSource? _growthRunCancellation;

	private Task? _growthRunTask;

	private const string GlobalWebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

	private const string GrowthDesktopUserAgent = "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1";

	private const string GrowthMiniPlatform = "miniprogram";

	private const string GrowthMpReportPlatform = "mp-weixin";

	private const string GrowthWebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";

	private static readonly string[] GrowthAutoTaskCodes = new string[25]
	{
		"chat_5", "first_buddy", "Model_chat_GLM5.2", "RichMeow_Chat", "Buddy_App", "Buddy_App_QQ", "automation_1", "Library_read", "template_5", "playbook_prompt",
		"create_canvas", "expert_5", "Expert_team_use_3", "Hp_Appearance", "skill_1", "Expert_lighthouse", "black_cat", "school_season", "Sequential_Tasks_1", "Sequential_Tasks_2",
		"Sequential_Tasks_3", "Sequential_Tasks_4", "Sequential_Tasks_5", "Sequential_Tasks_6", "Sequential_Tasks_7"
	};

	private static readonly HashSet<string> GrowthMiniTaskCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "school_season", "Sequential_Tasks_1", "Sequential_Tasks_2", "Sequential_Tasks_3", "Sequential_Tasks_4", "Sequential_Tasks_5", "Sequential_Tasks_6", "Sequential_Tasks_7" };

	private static readonly Regex AnthropicBillingHeaderRegex = new Regex("(?i)x-anthropic-billing-header:[^;\\n]*;?\\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex BareAnthropicBillingHeaderRegex = new Regex("(?i)x-anthropic-billing-header", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex ClientContextKeyValueRegex = new Regex("(?i)\\bcc_[a-z0-9_]+=[^;\\n]*;?\\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private const string DegradedSystemPrompt = "You are a helpful assistant. Respond in the user's language, follow the user's instructions, and be direct and concise.";

	/// <summary>每小时调用宿主账号刷新流程，更新已保存且有 Refresh Token 的账号。</summary>
	[ScheduledTask("workbuddy-token-refresh", "0 0 * * * *", Description = "每小时刷新 WorkBuddy OAuth 访问令牌")]
	public Task RefreshAccountsTaskAsync(PluginScheduledTaskContext context)
	{
		return RefreshAccountsCoreAsync("workbuddy-token-refresh", context.CancellationToken);
	}

	/// <summary>每天晚间刷新账号令牌，作为 WorkBuddy 账号保活任务。</summary>
	[ScheduledTask("workbuddy-keepalive", "0 0 22 * * *", Description = "每天 22:00 刷新账号令牌并执行保活")]
	public Task KeepaliveTaskAsync(PluginScheduledTaskContext context)
	{
		return RefreshAccountsCoreAsync("workbuddy-keepalive", context.CancellationToken);
	}

	/// <summary>每五分钟刷新所有可用账号的积分/套餐余额快照。</summary>
	[ScheduledTask("workbuddy-balance-refresh", "0 */5 * * * *", Description = "每 5 分钟刷新 WorkBuddy 账号积分余额")]
	public Task BalanceRefreshTaskAsync(PluginScheduledTaskContext context)
	{
		return RefreshBalancesCoreAsync(context.CancellationToken);
	}

	/// <summary>每天两次为国内站账号调用官方签到接口；国际账号会被跳过。</summary>
	[ScheduledTask("workbuddy-daily-checkin", "0 0 9,21 * * *", Description = "每天 09:00 和 21:00 执行国内站签到")]
	public Task DailyCheckinTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunDailyCheckinAsync, "workbuddy-daily-checkin", cnOnly: true, context.CancellationToken);
	}

	/// <summary>每天为国内站账号上报一次对话活跃事件。</summary>
	[ScheduledTask("workbuddy-activity", "0 0 10 * * *", Description = "每天 10:00 为每个国内站账号上报一次对话活跃事件")]
	public Task ActivityTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunActivityAsync, "workbuddy-activity", cnOnly: true, context.CancellationToken);
	}

	/// <summary>每天为国内站账号执行 Buddy 领养、旅行和领奖流程。</summary>
	[ScheduledTask("workbuddy-travel", "0 0 9,21 * * *", Description = "每天 09:00 和 21:00 执行 Buddy 领养、旅行和领奖")]
	public Task TravelTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunTravelAsync, "workbuddy-travel", cnOnly: true, context.CancellationToken);
	}

	/// <summary>接受国内站账号当前可执行的成长任务，并领取已完成任务奖励。</summary>
	[ScheduledTask("workbuddy-growth-tasks", "0 15 9 * * *", Description = "每天 09:15 接受成长任务并领取已完成奖励")]
	public Task GrowthTasksTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunGrowthTasksAsync, "workbuddy-growth-tasks", cnOnly: true, context.CancellationToken);
	}

	/// <summary>为国内站账号兑换已解锁的连续签到档位并领取可用奖励。</summary>
	[ScheduledTask("workbuddy-streak-rewards", "0 20 9 * * *", Description = "每天 09:20 兑换已解锁连登档位并抽取可用奖励")]
	public Task StreakRewardsTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunStreakRewardsAsync, "workbuddy-streak-rewards", cnOnly: true, context.CancellationToken);
	}

	/// <summary>执行国内站夜间对话任务，补足前一晚 23:00 至当日 08:00 的任务量。</summary>
	[ScheduledTask("workbuddy-night-tasks", "0 0 23 * * *", Description = "每天 23:00 执行夜猫子对话任务，补足 23:00 至 08:00 的任务量")]
	public Task NightTasksTaskAsync(PluginScheduledTaskContext context)
	{
		return RunForAccountsAsync(RunNightTasksAsync, "workbuddy-night-tasks", cnOnly: true, context.CancellationToken);
	}

	/// <summary>刷新有 Refresh Token 的账号，并把调用方任务 ID 写入任务日志。</summary>
	private Task RefreshAccountsCoreAsync(string taskName, CancellationToken cancellationToken)
	{
		return RunForAccountsAsync(async (Account account, OAuthCredential _, CancellationToken token) =>
		{
			await RefreshSavedAccountAsync(account, token);
		}, taskName, cnOnly: false, cancellationToken, (OAuthCredential credential) => !string.IsNullOrWhiteSpace(credential.RefreshToken));
	}

	/// <summary>对每个具备可用凭据的账号读取并缓存当前积分余额。</summary>
	private Task RefreshBalancesCoreAsync(CancellationToken cancellationToken)
	{
		return RunForAccountsAsync(RefreshBalanceForAccountAsync, "workbuddy-balance-refresh", cnOnly: false, cancellationToken);
	}

	/// <summary>与后台同步共用账号级单次读取；取消等待不会中断其他调用仍在共享的读取。</summary>
	private async Task RefreshBalanceForAccountAsync(Account account, OAuthCredential _, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Exception ex = await GetOrStartBalanceRefresh(account.Id).WaitAsync(cancellationToken);
		if (ex != null)
		{
			ExceptionDispatchInfo.Throw(ex);
		}
	}

	/// <summary>新增账号、启动预热和缓存缺失时后台同步；按账号去重，最多四个实际查询。</summary>
	private void QueueBalanceRefresh(Account account, bool force = false)
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0010: Invalid comparison between Unknown and I4
		ResourceState state = account.Status.State;
		if (state - 3 > 1)
		{
			Credential credential = account.Credential;
			OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
			if (val != null && HasUsableToken(val) && (force || !_balanceCache.TryGetValue(account.Id, out BalanceSnapshot value) || !(DateTimeOffset.UtcNow - value.LastAttemptAt < BalanceRefreshInterval)))
			{
				GetOrStartBalanceRefresh(account.Id);
			}
		}
	}

	private Task<Exception?> GetOrStartBalanceRefresh(string accountId)
	{
		lock (_balanceRefreshLock)
		{
			if (_balanceStopping)
			{
				return Task.FromResult((Exception)new OperationCanceledException("WorkBuddy balance refresh is stopping."));
			}
			if (_balanceRefreshTasks.TryGetValue(accountId, out Task<Exception> value))
			{
				return value;
			}
			Task<Exception> task = Task.Run(() => RefreshBalanceInBackgroundAsync(accountId));
			_balanceRefreshTasks.Add(accountId, task);
			return task;
		}
	}

	private bool IsBalanceRefreshing(string accountId)
	{
		lock (_balanceRefreshLock)
		{
			return _balanceRefreshTasks.ContainsKey(accountId);
		}
	}

	private async Task<Exception?> RefreshBalanceInBackgroundAsync(string accountId)
	{
		bool entered = false;
		try
		{
			await _balanceRefreshSlots.WaitAsync(_balanceShutdown.Token);
			entered = true;
			using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(_balanceShutdown.Token);
			timeout.CancelAfter(TimeSpan.FromSeconds(60L));
			CancellationToken cancellationToken = timeout.Token;
			Account val = await _host.Accounts.GetAsync(accountId, cancellationToken);
			bool flag = val == null;
			if (!flag)
			{
				ResourceState state = val.Status.State;
				bool flag2 = state - 3 <= 1;
				flag = flag2;
			}
			if (!flag)
			{
				Credential credential = val.Credential;
				OAuthCredential credential2 = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
				if (credential2 != null && HasUsableToken(credential2))
				{
					val = await RefreshIfNeededAsync(val, credential2, cancellationToken);
					ResourceState state = val.Status.State;
					if (state - 3 <= 1)
					{
						return null;
					}
					Credential credential3 = val.Credential;
					credential2 = (OAuthCredential)(((object)((credential3 is OAuthCredential) ? credential3 : null)) ?? ((object)credential2));
					using HttpClient client = CreateDirectClient(cancellationToken);
					OAuthCredential credential4 = credential2;
					HttpMethod post = HttpMethod.Post;
					string url = BillingBaseFor(credential2) + BillingResourcePathFor(credential2);
					JsonObject jsonObject = new JsonObject
					{
						["PageNumber"] = 1,
						["PageSize"] = 100,
						["ProductCode"] = "p_tcaca"
					};
					InlineArray2<JsonNode> buffer = default;
					buffer[0] = 0;
					buffer[1] = 3;
					jsonObject["Status"] = new JsonArray(buffer);
					jsonObject["PackageEndTimeRangeBegin"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
					jsonObject["PackageEndTimeRangeEnd"] = DateTimeOffset.UtcNow.AddDays(36865.0).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
					BalanceSnapshot balanceSnapshot = ParseBalance((await SendAccountJsonAsync(client, credential4, post, url, jsonObject, cancellationToken)).Data);
					_balanceCache[accountId] = balanceSnapshot;
					if (balanceSnapshot.Remain > (decimal?)0m)
					{
						await RecoverCreditCooldownAsync(accountId, cancellationToken);
					}
					return null;
				}
			}
			_balanceCache.TryRemove(accountId, out BalanceSnapshot _);
			return null;
		}
		catch (OperationCanceledException result) when (_balanceShutdown.IsCancellationRequested)
		{
			return result;
		}
		catch (Exception ex)
		{
			Exception exception = ex;
			_balanceCache.AddOrUpdate(accountId, (string _) => BalanceSnapshot.Failed(exception.Message), (string _, BalanceSnapshot existing) => existing with
			{
				Error = exception.Message,
				LastAttemptAt = DateTimeOffset.UtcNow
			});
			await TryLogAsync("account.balance.failed", exception.Message, "Warning", null, null, accountId);
			return exception;
		}
		finally
		{
			if (entered)
			{
				_balanceRefreshSlots.Release();
			}
			lock (_balanceRefreshLock)
			{
				_balanceRefreshTasks.Remove(accountId);
			}
		}
	}

	private Task StopBalanceRefreshesAsync()
	{
		lock (_balanceRefreshLock)
		{
			_balanceStopping = true;
			return _balanceStopTask ?? (_balanceStopTask = StopBalanceRefreshesCoreAsync(_balanceRefreshTasks.Values.ToArray()));
		}
	}

	private async Task StopBalanceRefreshesCoreAsync(Task<Exception?>[] tasks)
	{
		await _balanceShutdown.CancelAsync();
		await Task.WhenAll(tasks);
		_balanceRefreshSlots.Dispose();
		_balanceShutdown.Dispose();
	}

	/// <summary>仅用仍有余额且未过期的积分包排序；获取不到明细时保留原有会话粘性兜底。</summary>
	private DateTimeOffset? GetPreferredCreditExpiry(Account account)
	{
		QueueBalanceRefresh(account);
		if (!_balanceCache.TryGetValue(account.Id, out BalanceSnapshot value))
		{
			return null;
		}
		return PreferredCreditExpiry(value, DateTimeOffset.UtcNow);
	}

	private static DateTimeOffset? PreferredCreditExpiry(BalanceSnapshot balance, DateTimeOffset now)
	{
		decimal? remain = balance.Remain;
		if (!remain.HasValue || !(remain.GetValueOrDefault() > 0m) || now - balance.UpdatedAt > BalancePriorityMaxAge)
		{
			return null;
		}
		return balance.PackageDetails.Where((CreditPackageSnapshot package) => package.Remain > 0m && package.ExpiresAt > now).Min((CreditPackageSnapshot package) => package.ExpiresAt);
	}

	/// <summary>仅当访问令牌已过期或将在刷新缓冲期内过期时尝试续期；续期失败仍返回原账号。</summary>
	private async Task<Account> RefreshIfNeededAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		DateTimeOffset? dateTimeOffset = credential.ExpiresAt ?? account.ExpiresAt;
		bool flag = string.IsNullOrWhiteSpace(credential.AccessToken);
		if (string.IsNullOrWhiteSpace(credential.RefreshToken) || (!flag && (!dateTimeOffset.HasValue || dateTimeOffset > DateTimeOffset.UtcNow.Add(RefreshSkew))))
		{
			return account;
		}
		try
		{
			return await RefreshSavedAccountAsync(account, cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			WorkBuddyTerminal workBuddyTerminal = this;
			string message = "账号令牌刷新失败，继续使用现有令牌：" + ex.Message;
			string id = account.Id;
			object details = new
			{
				exception = ex.GetType().Name
			};
			await workBuddyTerminal.TryLogAsync("account.refresh.failed", message, "Warning", null, null, id, null, null, null, details);
			return (await _host.Accounts.GetAsync(account.Id, cancellationToken)) ?? account;
		}
	}

	/// <summary>通过宿主账号存储的并发安全刷新入口更新凭据，同时保留停用与积分冷却状态。</summary>
	private async Task<Account> RefreshSavedAccountAsync(Account account, CancellationToken cancellationToken)
	{
		try
		{
			return await _host.Accounts.RefreshAsync(account.Id, (Func<Account, CancellationToken, Task<Account>>)(async (Account current, CancellationToken token) =>
			{
				Credential credential = current.Credential;
				OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
				if (val == null || string.IsNullOrWhiteSpace(val.RefreshToken))
				{
					return current;
				}
				OAuthCredential val2 = (OAuthCredential)(object)(current.Credential = (Credential)(object)(await RefreshCredentialAsync(val, token)));
				current.ExpiresAt = val2.ExpiresAt;
				current.Label = val2.Nickname ?? current.Label ?? val2.AccountId;
				if ((int)current.Status.State != 4 && IsSessionDeadStrikeReason(current.Status.Reason))
				{
					current.Status.Reason = null;
					current.Status.LastStatusCode = null;
				}
				return current;
			}), cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			int? statusCode = ((ex is WorkBuddyUpstreamException ex2) ? new int?(ex2.StatusCode) : ((int?)null));
			if (IsSessionDeadError(ex.Message))
			{
				if (await RecordSessionDeadFailureAsync(account, statusCode, cancellationToken) >= 3)
				{
					await TryLogAsync("account.disabled", "WorkBuddy 12153 连续失败达到 3 次，账号已停用；请重新 OAuth 登录恢复", "Warning", null, null, account.Id, null, statusCode ?? 401);
				}
			}
			else if (IsPermanentAccountFault(ex.Message))
			{
				await _host.Accounts.DisableAsync(account.Id, "WorkBuddy 返回 request illegal (11140)，账号已停用；请重新 OAuth 登录恢复", statusCode, cancellationToken);
			}
			throw;
		}
	}

	/// <summary>记录连续 WorkBuddy 12153 错误；第三次后由宿主持久化为永久停用。</summary>
	private async Task<int> RecordSessionDeadFailureAsync(Account account, int? statusCode, CancellationToken cancellationToken, bool applyDisable = true)
	{
		Account val = (await _host.Accounts.GetAsync(account.Id, cancellationToken)) ?? account;
		if ((int)val.Status.State == 4)
		{
			return 3;
		}
		int num = ReadSessionDeadStrikes(val.Status.Reason);
		int strikes = num + 1;
		if (strikes >= 3)
		{
			if (applyDisable)
			{
				await _host.Accounts.DisableAsync(val.Id, "WorkBuddy 12153 连续失败达到 3 次，账号已停用；请重新 OAuth 登录恢复", statusCode, cancellationToken);
			}
			return strikes;
		}
		val.Status.Reason = $"{"WorkBuddy 12153 session dead: "}{strikes}/3";
		val.Status.LastStatusCode = statusCode;
		await _host.Accounts.SaveAsync(val, cancellationToken);
		return strikes;
	}

	/// <summary>聊天成功后清除未达到停用阈值的 12153 连续失败计数。</summary>
	private async Task ClearSessionDeadFailuresAsync(Account account, CancellationToken cancellationToken)
	{
		try
		{
			Account val = await _host.Accounts.GetAsync(account.Id, cancellationToken);
			if (val == null || (int)val.Status.State == 4 || !IsSessionDeadStrikeReason(val.Status.Reason))
			{
				return;
			}
			val.Status.Reason = null;
			val.Status.LastStatusCode = null;
			await _host.Accounts.SaveAsync(val, cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			await TryLogAsync("account.session-strike.reset.failed", ex.Message, "Warning", null, null, account.Id);
		}
	}

	/// <summary>仅在真实余额已恢复且账号仍为 Active 时自动解除积分冷却。</summary>
	private async Task RecoverCreditCooldownAsync(string accountId, CancellationToken cancellationToken)
	{
		if (await _host.Accounts.ClearCooldownAsync(accountId, "WorkBuddy 积分耗尽，冷却至本地次日 04:00", cancellationToken))
		{
			await TryLogAsync("account.credit-cooldown.recovered", "余额已恢复，WorkBuddy 积分冷却已解除", "Information", null, null, accountId);
		}
	}

	/// <summary>将硬积分耗尽冷却到宿主本地时区的下一个凌晨 04:00。</summary>
	private static DateTimeOffset NextLocalFourAm()
	{
		DateTime now = DateTime.Now;
		DateTime dateTime = now.Date.AddHours(4.0);
		if (now >= dateTime)
		{
			dateTime = dateTime.AddDays(1.0);
		}
		return new DateTimeOffset(dateTime, TimeZoneInfo.Local.GetUtcOffset(dateTime));
	}

	private static int ReadSessionDeadStrikes(string? reason)
	{
		if (!IsSessionDeadStrikeReason(reason) || !int.TryParse(reason.Substring("WorkBuddy 12153 session dead: ".Length).Split('/')[0], out var result))
		{
			return 0;
		}
		return Math.Clamp(result, 0, 2);
	}

	private static bool IsSessionDeadStrikeReason(string? reason)
	{
		return reason?.StartsWith("WorkBuddy 12153 session dead: ", StringComparison.Ordinal) ?? false;
	}

	/// <summary>调用官方刷新令牌接口，并将缺失的新字段回退到原有刷新令牌、域名和到期时间。</summary>
	private async Task<OAuthCredential> RefreshCredentialAsync(OAuthCredential current, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		UpstreamResult upstreamResult = await SendOAuthAsync(HttpMethod.Post, BaseFor(current) + "/v2/plugin/auth/token/refresh", RealmFor(current), null, null, current.RefreshToken, cancellationToken, client, current);
		if (!upstreamResult.Success)
		{
			throw new WorkBuddyUpstreamException(upstreamResult.StatusCode, upstreamResult.Message);
		}
		string text = ReadNodeString(upstreamResult.Data, "accessToken", "access_token");
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("refresh response did not contain accessToken");
		}
		string refreshToken = ReadNodeString(upstreamResult.Data, "refreshToken", "refresh_token") ?? current.RefreshToken;
		string domain = ReadNodeString(upstreamResult.Data, "domain") ?? current.Domain;
		long? num = ReadNodeLong(upstreamResult.Data, "expiresIn", "expires_in");
		DateTimeOffset? expiresAt = ((num.HasValue && num.GetValueOrDefault() > 0) ? new DateTimeOffset?(DateTimeOffset.UtcNow.AddSeconds(num.Value)) : current.ExpiresAt);
		OAuthCredential val = current._003CClone_003E_0024();
		val.set_AccessToken(text);
		val.set_RefreshToken(refreshToken);
		val.set_Domain(domain);
		val.set_ExpiresAt(expiresAt);
		return val;
	}

	/// <summary>
	/// 按账号顺序运行指定任务，按凭据、站点区域及可选条件筛选账号；
	/// 单个账号失败会写日志并隔离，不会阻止后续账号执行。
	/// </summary>
	private async Task RunForAccountsAsync(Func<Account, OAuthCredential, CancellationToken, Task> action, string taskName, bool cnOnly, CancellationToken cancellationToken, Func<OAuthCredential, bool>? credentialFilter = null)
	{
		foreach (Account account in await _host.Accounts.ListAsync("workbuddy", cancellationToken))
		{
			cancellationToken.ThrowIfCancellationRequested();
			Credential credential = account.Credential;
			OAuthCredential oauth = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
			if (oauth == null || !(credentialFilter?.Invoke(oauth) ?? HasUsableToken(oauth)) || (cnOnly && IsGlobal(oauth)))
			{
				continue;
			}
			DateTimeOffset started = DateTimeOffset.UtcNow;
			await TryLogAsync("task.account.started", "账号任务开始", "Information", null, taskName, account.Id);
			try
			{
				await action(account, oauth, cancellationToken);
				object details = null;
				if (taskName.Equals("workbuddy-balance-refresh", StringComparison.OrdinalIgnoreCase) && _balanceCache.TryGetValue(account.Id, out BalanceSnapshot value))
				{
					details = new { value.Remain, value.Used, value.Size, value.Packages, value.UpdatedAt };
				}
				int durationMs = (int)Math.Max(0.0, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
				await WriteAccountTaskLogAsync(taskName, account, "Success", "账号任务执行成功", started, null, details);
				WorkBuddyTerminal workBuddyTerminal = this;
				string id = account.Id;
				int? durationMs2 = durationMs;
				await workBuddyTerminal.TryLogAsync("task.account.completed", "账号任务执行成功", "Information", null, taskName, id, null, null, durationMs2, details);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				string error = ex.Message;
				await WriteAccountTaskLogAsync(taskName, account, "Failed", "账号任务执行失败：" + error, started, error);
				WorkBuddyTerminal workBuddyTerminal2 = this;
				string message = "账号任务执行失败：" + error;
				string id2 = account.Id;
				int? durationMs2 = (int)Math.Max(0.0, (DateTimeOffset.UtcNow - started).TotalMilliseconds);
				object details2 = new
				{
					exception = ex.GetType().Name
				};
				await workBuddyTerminal2.TryLogAsync("task.account.failed", message, "Error", null, taskName, id2, null, null, durationMs2, details2);
			}
		}
	}

	/// <summary>记录单个账号任务的成功/失败、耗时及可选执行明细。</summary>
	private async Task WriteAccountTaskLogAsync(string taskName, Account account, string status, string message, DateTimeOffset started, string? error = null, object? details = null)
	{
		try
		{
			IPluginTasks tasks = _host.Tasks;
			TaskLog val = new TaskLog();
			val.set_PluginKey(_host.PluginKey);
			val.set_Platform("workbuddy");
			val.set_TaskName(taskName);
			val.set_AccountId(account.Label ?? account.Id);
			val.set_Status(status);
			val.set_Message(message);
			val.set_Error(error);
			val.set_DetailsJson((details == null) ? null : JsonSerializer.Serialize(details));
			val.set_DurationMs((int)Math.Max(0.0, (DateTimeOffset.UtcNow - started).TotalMilliseconds));
			val.set_StartedAt(started);
			val.set_FinishedAt((DateTimeOffset?)DateTimeOffset.UtcNow);
			await tasks.WriteLogAsync(val, CancellationToken.None);
		}
		catch (Exception ex)
		{
			await TryLogAsync("task.detail.log.failed", ex.Message, "Error", null, taskName, account.Id);
		}
	}

	/// <summary>调用账号所属国内/国际站对应的官方每日签到接口。</summary>
	private async Task RunDailyCheckinAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		await SendAccountJsonAsync(client, credential, HttpMethod.Post, BillingBaseFor(credential) + DailyCheckinPathFor(credential), new JsonObject(), cancellationToken);
	}

	/// <summary>通过官方活跃报告接口为账号提交一次对话活跃事件。</summary>
	private async Task RunActivityAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		await SendActivityReportAsync(client, credential, "deepseek-v4-flash", "DeepSeek V4 Flash", "router2api", cancellationToken);
	}

	/// <summary>构建并发送带稳定 trace ID 和账号信息的 WorkBuddy 活跃报告请求。</summary>
	private static async Task SendActivityReportAsync(HttpClient client, OAuthCredential credential, string modelId, string modelName, string conversationPrefix, CancellationToken cancellationToken)
	{
		string text = Guid.NewGuid().ToString("N");
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string text2 = $"{conversationPrefix}-{num}";
		JsonArray body = new JsonArray
		{
			new JsonObject
			{
				["eventCode"] = "chat_request_send",
				["timestamp"] = num,
				["reportDelay"] = 0,
				["mode"] = "craft",
				["conversationId"] = text2,
				["requestId"] = text,
				["inputLength"] = 12,
				["requestModelId"] = modelId,
				["requestModelName"] = modelName,
				["isPlan"] = false,
				["isAutoExecuteTerminal"] = false,
				["isAutoModify"] = false,
				["codebaseEnable"] = false,
				["maxToken"] = 0,
				["maxSteps"] = 0,
				["temperature"] = 0,
				["maxRetries"] = 0,
				["mentionContexts"] = new JsonArray(),
				["knowledgeId"] = new JsonArray(),
				["knowledgeName"] = new JsonArray(),
				["codebaseId"] = string.Empty,
				["mentionContextCount"] = 0,
				["command"] = string.Empty,
				["expertId"] = string.Empty,
				["recommendId"] = string.Empty,
				["skillId"] = string.Empty,
				["skillCount"] = 0,
				["totalCount"] = 0,
				["fileUri"] = string.Empty,
				["presentAt"] = num,
				["traceId"] = string.Empty,
				["rootRequestId"] = text,
				["parentConversationId"] = text2,
				["agentName"] = "default",
				["agentType"] = "conversation",
				["userId"] = credential.AccountId ?? string.Empty
			}
		};
		await SendAccountJsonAsync(client, credential, HttpMethod.Post, BillingBaseFor(credential) + ReportPathFor(credential), body, cancellationToken);
	}

	/// <summary>依次读取 Buddy 旅行状态、推进旅行操作，并领取已经满足条件的奖励。</summary>
	private async Task RunTravelAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		JsonNode jsonNode = (await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/activity/growth/buddy/info", null, cancellationToken)).Data?["buddy"];
		if (jsonNode == null || jsonNode.GetValueKindSafe() == JsonValueKind.Null)
		{
			try
			{
				await SendActivityReportAsync(client, credential, "deepseek-v4-flash", "DeepSeek V4 Flash", "router2api-adopt", cancellationToken);
				await Task.Delay(TimeSpan.FromSeconds(1L), cancellationToken);
			}
			catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
			{
				WorkBuddyTerminal workBuddyTerminal = this;
				string message = "旅行前置活跃上报失败，继续尝试领养：" + ex.Message;
				string id = account.Id;
				object details = new
				{
					operation = "activity.report"
				};
				await workBuddyTerminal.TryLogAsync("task.request.warning", message, "Warning", null, "workbuddy-travel", id, null, null, null, details);
			}
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/buddy/agreement", new JsonObject { ["agree"] = true }, cancellationToken);
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/buddy/first", new JsonObject(), cancellationToken);
			return;
		}
		UpstreamResult upstreamResult = await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/activity/growth/buddy/travel/status", null, cancellationToken);
		string a = ReadNodeString(upstreamResult.Data, "state");
		if (string.Equals(a, "arrived", StringComparison.OrdinalIgnoreCase))
		{
			long valueOrDefault = ReadNodeLong(upstreamResult.Data, "record_id").GetValueOrDefault();
			if (valueOrDefault > 0)
			{
				await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/buddy/travel/claim", new JsonObject { ["record_id"] = valueOrDefault }, cancellationToken);
			}
		}
		else if (string.Equals(a, "idle", StringComparison.OrdinalIgnoreCase) && ReadNodeBoolean(upstreamResult.Data, "daily_limit_reached") != true)
		{
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/buddy/travel/depart", new JsonObject { ["location_id"] = 4 }, cancellationToken);
		}
	}

	/// <summary>读取可接受的成长任务，逐项接受并领取当前已完成的任务奖励。</summary>
	private async Task RunGrowthTasksAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		UpstreamResult upstreamResult = await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/v2/activity/growth/tasks", null, cancellationToken);
		List<string> list = ReadTaskCodes(upstreamResult.Data);
		if (list.Count > 0)
		{
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/v2/activity/growth/tasks/accept", new JsonObject { ["task_codes"] = new JsonArray(((IEnumerable<string>)list).Select((Func<string, JsonNode>)((string code) => JsonValue.Create(code))).ToArray()) }, cancellationToken);
			upstreamResult = await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/v2/activity/growth/tasks", null, cancellationToken);
		}
		foreach (string item in ReadClaimableTaskCodes(upstreamResult.Data))
		{
			string url = "https://www.workbuddy.cn/activity/growth/tasks/" + Uri.EscapeDataString(item) + "/claim";
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, url, null, cancellationToken, webClaim: true);
		}
	}

	/// <summary>检查 7/14/28 日连登奖励状态，兑换可领取档位并执行可用抽奖。</summary>
	private async Task RunStreakRewardsAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		UpstreamResult streak = await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/activity/growth/streak", null, cancellationToken);
		if (streak.Data?["redemption_status"]?["tiers"] is JsonArray source)
		{
			foreach (JsonObject item in source.OfType<JsonObject>())
			{
				string text = ReadNodeString(item, "tier");
				string text2 = ReadNodeString(item, "status") ?? TierStatus(streak.Data?["redemption_status"] as JsonObject, text);
				bool flag = string.IsNullOrWhiteSpace(text);
				if (!flag)
				{
					bool flag2 = ((text2 == "locked" || text2 == "claimed") ? true : false);
					flag = flag2;
				}
				if (!flag)
				{
					await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/redeem", new JsonObject
					{
						["tier"] = text,
						["client_token"] = Guid.NewGuid().ToString()
					}, cancellationToken);
				}
			}
		}
		int chances = (int)Math.Clamp(ReadNodeLong((await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/activity/growth/lottery/summary", null, cancellationToken)).Data, "chances").GetValueOrDefault(), 0L, 20L);
		for (int index = 0; index < chances; index++)
		{
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/activity/growth/lottery/draw", new JsonObject { ["client_token"] = Guid.NewGuid().ToString() }, cancellationToken);
		}
	}

	/// <summary>查询夜间任务进度，并通过受控对话请求补足目标任务量。</summary>
	private async Task RunNightTasksAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		int hour = DateTimeOffset.Now.Hour;
		if (hour >= 8 && hour < 23)
		{
			return;
		}
		using HttpClient client = CreateDirectClient(cancellationToken);
		UpstreamResult upstreamResult = await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/v2/activity/growth/tasks", null, cancellationToken);
		int need = 0;
		if (upstreamResult.Data?["tasks"] is JsonArray source)
		{
			JsonObject jsonObject = source.OfType<JsonObject>().FirstOrDefault((JsonObject item) => string.Equals(ReadNodeString(item, "task_code"), "black_cat", StringComparison.OrdinalIgnoreCase));
			if (jsonObject != null)
			{
				need = (int)Math.Clamp(ReadTaskCount(jsonObject, "target") - ReadTaskCount(jsonObject, "current"), 0L, 3L);
			}
		}
		for (int index = 0; index < need; index++)
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/v2/chat/completions")
			{
				Content = new StringContent(new JsonObject
				{
					["model"] = "glm-5.2",
					["messages"] = new JsonArray((JsonNode)new JsonObject
					{
						["role"] = "user",
						["content"] = "1+1等于几？直接回答。"
					}),
					["stream"] = true
				}.ToJsonString(), Encoding.UTF8, "application/json")
			};
			ApplyHeaders(request, credential, includeAuthorization: true, streaming: true);
			using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
			if (!response.IsSuccessStatusCode)
			{
				string value = await response.Content.ReadAsStringAsync(cancellationToken);
				throw new InvalidOperationException($"夜间任务聊天请求失败（HTTP {(int)response.StatusCode}）：{value}");
			}
			await response.Content.ReadAsStringAsync(cancellationToken);
			await SendActivityReportAsync(client, credential, "glm-5.2", "GLM-5.2", "wb2api-night", cancellationToken);
		}
	}

	/// <summary>
	/// 将 WorkBuddy / CodeBuddy OAuth 账号接入 Router2API 聊天终端。
	/// 插件负责 OAuth、账号持久化、协议转换和账号任务；宿主负责平台路由、策略执行和插件生命周期。
	/// 国内与国际账号共用平台标识，实际站点由 OAuth 返回的 domain 决定。
	/// 成长任务事件头与请求体按 MIT 参考项目 workbuddy2api-panel 的任务协议适配，参考版本为
	/// dbd7c6800ed8071d7dd617d456b6041294781fee（https://github.com/linguo2625469/workbuddy2api-panel）。
	/// 上游内容策略误拦截时的中性 system prompt 单次重试也参考该版本的 handler.go 与 prompt.go。
	/// WorkBuddy 请求兼容规则参考 xiaofan6ya/workbuddy2api upstream_compat.py，提交 bc173d82670dee4ef9761e78b274b7157ddb08b6。
	/// 空 function_call 与会话前缀粘性参考 ardeyouxipianyi/workbuddy2api-hub PR #4，合并提交 0914e4a4b0750c6e2cc8b62cbc70de822b1cebe2；参考仓库 main HEAD 为 650b65945e2772517ab3e8bd4dc30b57b0019461。
	/// </summary>
	/// <param name="host">提供账号存储、模型目录、HTTP 客户端、任务日志及其他宿主服务。</param>
	public WorkBuddyTerminal(IPluginHost host)
	{
		_host = host.Services;
		_pendingLogins = new ConcurrentDictionary<string, PendingLogin>(StringComparer.Ordinal);
		_balanceCache = new ConcurrentDictionary<string, BalanceSnapshot>(StringComparer.Ordinal);
		_balanceRefreshLock = new object();
		_balanceRefreshTasks = new Dictionary<string, Task<Exception>>(StringComparer.Ordinal);
		_balanceRefreshSlots = new SemaphoreSlim(4, 4);
		_balanceShutdown = new CancellationTokenSource();
		_modelReasoningLevels = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
		_manualTaskRunLock = new object();
		_manualTaskRunSnapshots = new Dictionary<string, ManualTaskRunSnapshot>(StringComparer.Ordinal);
		_activeManualTaskRunIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		_manualTaskRunCancellations = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
		_manualTaskRunTasks = new Dictionary<string, Task>(StringComparer.Ordinal);
		_growthRunLock = new object();
		_growthRunSnapshot = GrowthRunSnapshot.Idle;
		base._002Ector();
	}

	/// <summary>
	/// 按已登录的国内/国际账号分别发现模型，再按版本标记后合并返回。
	/// </summary>
	/// <param name="context">宿主模型目录提供的可复用 HTTP 客户端（如有）。</param>
	/// <param name="cancellationToken">取消账号读取和上游模型查询的令牌。</param>
	/// <returns>包含版本前缀并按显示名称和模型 ID 排序的模型目录。</returns>
	public async Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken cancellationToken)
	{
		Account[] array = (from @group in (await _host.Accounts.ListAsync("workbuddy", cancellationToken)).Where((Account item) =>
			{
				//IL_0022: Unknown result type (might be due to invalid IL or missing references)
				//IL_0027: Unknown result type (might be due to invalid IL or missing references)
				//IL_0028: Unknown result type (might be due to invalid IL or missing references)
				//IL_002a: Unknown result type (might be due to invalid IL or missing references)
				//IL_002c: Invalid comparison between Unknown and I4
				Credential credential = item.Credential;
				OAuthCredential val2 = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
				bool flag = val2 != null && HasUsableToken(val2);
				if (flag)
				{
					ResourceState state = item.Status.State;
					bool flag2 = state - 3 <= 1;
					flag = !flag2;
				}
				return flag;
			}).GroupBy((Account item) =>
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_0010: Expected Obj, but got Unknown
				return RealmFor((OAuthCredential)item.Credential);
			})
			select @group.OrderBy((Account item) =>
			{
				//IL_0006: Unknown result type (might be due to invalid IL or missing references)
				//IL_000c: Invalid comparison between Unknown and I4
				return ((int)item.Status.State != 0) ? 1 : 0;
			}).First()).OrderBy((Account item) =>
		{
			//IL_0006: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Expected Obj, but got Unknown
			return (!(RealmFor((OAuthCredential)item.Credential) == "cn")) ? 1 : 0;
		}).ToArray();
		if (array.Length == 0)
		{
			return Array.Empty<ModelDescriptor>();
		}
		HttpClient client = context.HttpClient;
		bool ownsClient = false;
		if (client == null)
		{
			client = CreateDirectClient(cancellationToken);
			ownsClient = true;
		}
		try
		{
			RealmModelResult[] source = await Task.WhenAll(array.Select((Account account) => FetchRealmModelsAsync(client, account, cancellationToken)));
			ModelDescriptor[] array2 = (from model in source.SelectMany((RealmModelResult result) => result.Models)
				orderby model.Id.StartsWith("global/", StringComparison.OrdinalIgnoreCase) ? 1 : 0
				select model).ThenBy((ModelDescriptor model) => model.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy((ModelDescriptor model) => model.Id, StringComparer.OrdinalIgnoreCase).ToArray();
			if (array2.Length == 0 && source.Any((RealmModelResult result) => result.Error != null))
			{
				throw new HttpRequestException(string.Join("; ", from result in source
					where result.Error != null
					select result.Error));
			}
			ModelDescriptor[] array3 = array2;
			foreach (ModelDescriptor val in array3)
			{
				_modelReasoningLevels[val.Id] = val.ReasoningLevels ?? Array.Empty<string>();
			}
			return array2;
		}
		finally
		{
			if (ownsClient)
			{
				client.Dispose();
			}
		}
	}

	private async Task<RealmModelResult> FetchRealmModelsAsync(HttpClient client, Account account, CancellationToken cancellationToken)
	{
		OAuthCredential credential = (OAuthCredential)account.Credential;
		string realm = RealmFor(credential);
		try
		{
			account = await RefreshIfNeededAsync(account, credential, cancellationToken);
			Credential credential2 = account.Credential;
			credential = (OAuthCredential)(((object)((credential2 is OAuthCredential) ? credential2 : null)) ?? ((object)credential));
			ModelFetchResult[] array;
			if (IsGlobal(credential))
			{
				InlineArray3<Task<ModelFetchResult>> buffer = default;
				buffer[0] = FetchV3ModelsAsync(client, credential, "CodeBuddyIDE/4.12.0 CodeBuddy/4.12.0", cancellationToken);
				buffer[1] = FetchV3ModelsAsync(client, credential, "CLI/2.63.2 CodeBuddy/2.63.2", cancellationToken);
				buffer[2] = FetchGlobalCliModelsAsync(client, credential, cancellationToken);
				array = await Task.WhenAll<ModelFetchResult>(buffer);
			}
			else
			{
				InlineArray2<Task<ModelFetchResult>> buffer2 = default;
				buffer2[0] = FetchV3ModelsAsync(client, credential, "CodeBuddyIDE/4.12.0 CodeBuddy/4.12.0", cancellationToken);
				buffer2[1] = FetchCliModelsAsync(client, credential, "/console/enterprises/personal/models", useCliAgent: true, cancellationToken);
				array = await Task.WhenAll<ModelFetchResult>(buffer2);
			}
			ModelFetchResult[] array2 = array;
			if ((object)array2.FirstOrDefault((ModelFetchResult result) => !result.Success && result.StatusCode == 401) != null)
			{
				return new RealmModelResult(realm, Array.Empty<ModelDescriptor>(), realm + " model discovery failed with HTTP 401");
			}
			if (array2.All((ModelFetchResult result) => !result.Success))
			{
				return new RealmModelResult(realm, Array.Empty<ModelDescriptor>(), $"{realm} model discovery failed with HTTP {array2[0].StatusCode}");
			}
			Dictionary<string, ModelDescriptor> dictionary = new Dictionary<string, ModelDescriptor>(StringComparer.OrdinalIgnoreCase);
			foreach (ModelFetchResult item in array2.Where((ModelFetchResult result) => result.Success))
			{
				ModelDescriptor[] models = item.Models;
				foreach (ModelDescriptor val in models)
				{
					string key = QualifiedModelId(realm, val.Id);
					ModelDescriptor val2 = val._003CClone_003E_0024();
					val2.set_Id(QualifiedModelId(realm, val.Id));
					dictionary.TryAdd(key, val2);
				}
			}
			return new RealmModelResult(realm, dictionary.Values.ToArray(), null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			return new RealmModelResult(realm, Array.Empty<ModelDescriptor>(), realm + " model discovery failed: " + ex2.Message);
		}
	}

	/// <summary>检查 OAuth 凭据是否至少包含访问令牌或刷新令牌；不在此方法内访问上游。</summary>
	/// <param name="credential">宿主保存的 OAuth 凭据。</param>
	/// <param name="cancellationToken">符合接口签名的取消令牌；本地格式检查不执行异步操作。</param>
	/// <returns>凭据是否可用于请求或后续刷新。</returns>
	public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken cancellationToken)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0033: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Expected Obj, but got Unknown
		OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
		return Task.FromResult<CredentialValidationResult>((val != null && (!string.IsNullOrWhiteSpace(val.AccessToken) || !string.IsNullOrWhiteSpace(val.RefreshToken))) ? new CredentialValidationResult(true, (string)null) : new CredentialValidationResult(false, "WorkBuddy OAuth access token and refresh token are empty"));
	}

	/// <summary>注册 OAuth 账号筛选、上游错误冷却和代理请求重试策略。</summary>
	/// <param name="builder">宿主提供的插件策略注册器。</param>
	public void Configure(IPluginBuilder builder)
	{
		builder.AccountPolicy((Action<PluginAccountPolicyBuilder>)((PluginAccountPolicyBuilder policy) =>
		{
			policy.SelectAccount((Func<Account, bool>)((Account account) =>
			{
				Credential credential = account.Credential;
				OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
				return val != null && HasUsableToken(val);
			})).SelectForRequest((Func<Account, AdapterRequest, bool>)MatchesRequestedRealm).PreferEarlier((Func<Account, DateTimeOffset?>)GetPreferredCreditExpiry)
				.WeightBy((Func<Account, AdapterRequest, int>)WorkBuddyAccountAffinityWeight);
		}));
		builder.ProxyPolicy((Action<PluginProxyPolicyBuilder>)((PluginProxyPolicyBuilder policy) =>
		{
			policy.OnTransportFailure((Func<PluginAttemptDecision>)(() =>
			{
				//IL_000d: Unknown result type (might be due to invalid IL or missing references)
				//IL_0017: Expected Obj, but got Unknown
				return FailureDecision(new PluginAttemptResult((PluginAttemptOutcome)1, (int?)null, true, false, (string)null));
			})).MaxAttempts(4).AttemptTimeoutSeconds(60)
				.TotalTimeoutSeconds(180);
		}));
	}

	/// <summary>初始化登录会话，并在已有可用账号时预热平台模型目录。</summary>
	/// <param name="context">插件启动信息。</param>
	/// <param name="cancellationToken">取消启动阶段服务解析或模型预热的令牌。</param>
	public async ValueTask StartAsync(PluginStartContext context, CancellationToken cancellationToken)
	{
		_pendingLogins.Clear();
		try
		{
			IReadOnlyList<Account> readOnlyList = await _host.Accounts.ListAsync("workbuddy", cancellationToken);
			foreach (Account item in readOnlyList)
			{
				QueueBalanceRefresh(item);
			}
			if (readOnlyList.Any((Account item) =>
			{
				Credential credential = item.Credential;
				OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
				return val != null && HasUsableToken(val);
			}))
			{
				_host.Models.Invalidate("workbuddy");
				await _host.Models.RefreshAsync("workbuddy", cancellationToken);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch
		{
		}
	}

	/// <summary>停止 OAuth 登录会话并取消、等待后台任务；已持久化账号凭据保留在宿主账号存储中。</summary>
	/// <param name="cancellationToken">宿主停止流程的取消令牌。</param>
	public async ValueTask StopAsync(CancellationToken cancellationToken)
	{
		_pendingLogins.Clear();
		Task[] manualTaskRuns;
		CancellationTokenSource[] array;
		lock (_manualTaskRunLock)
		{
			manualTaskRuns = _manualTaskRunTasks.Values.ToArray();
			array = _manualTaskRunCancellations.Values.ToArray();
		}
		CancellationTokenSource[] array2 = array;
		foreach (CancellationTokenSource cancellationTokenSource in array2)
		{
			try
			{
				cancellationTokenSource.Cancel();
			}
			catch (ObjectDisposedException)
			{
			}
		}
		await StopBalanceRefreshesAsync();
		if (manualTaskRuns.Length != 0)
		{
			try
			{
				await Task.WhenAll(manualTaskRuns).WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				await Task.WhenAll(manualTaskRuns);
			}
		}
		Task growthTask;
		CancellationTokenSource growthCancellation;
		lock (_growthRunLock)
		{
			growthTask = _growthRunTask;
			growthCancellation = _growthRunCancellation;
		}
		growthCancellation?.Cancel();
		if (growthTask != null)
		{
			try
			{
				await growthTask.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				await growthTask;
			}
		}
		lock (_growthRunLock)
		{
			if (_growthRunCancellation == growthCancellation)
			{
				_growthRunCancellation?.Dispose();
				_growthRunCancellation = null;
				_growthRunTask = null;
			}
		}
	}

	/// <summary>取消并等待插件拥有的后台工作，释放余额同步资源。</summary>
	/// <returns>所有后台工作退出后的完成通知。</returns>
	public ValueTask DisposeAsync()
	{
		return StopAsync(CancellationToken.None);
	}

	/// <summary>返回插件平台标识、当前账号数和未过期 OAuth 流程数。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>WorkBuddy 插件当前状态。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> StatusAsync(PluginHttpContext context)
	{
		IReadOnlyList<Account> readOnlyList = await _host.Accounts.ListAsync("workbuddy", context.CancellationToken);
		return context.Ok((object)new
		{
			platform = "workbuddy",
			pluginKey = _host.PluginKey,
			accounts = readOnlyList.Count,
			pendingOAuth = _pendingLogins.Count
		});
	}

	/// <summary>列出管理页面所需的账号、站点、令牌到期和最近积分快照。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>账号视图数组；凭据 Token 不包含在返回结果中。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> AccountsAsync(PluginHttpContext context)
	{
		IReadOnlyList<Account> readOnlyList = await _host.Accounts.ListAsync("workbuddy", context.CancellationToken);
		foreach (Account item in readOnlyList)
		{
			QueueBalanceRefresh(item);
		}
		return context.Ok((object)new
		{
			accounts = readOnlyList.Select(ToAccountView).ToArray()
		});
	}

	/// <summary>读取宿主模型目录中已缓存的 WorkBuddy 模型。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>管理页面展示格式的模型列表。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> ModelsAsync(PluginHttpContext context)
	{
		return context.Ok((object)new
		{
			models = (await _host.Models.ListAsync("workbuddy", context.CancellationToken)).Select(ToModelView).ToArray()
		});
	}

	/// <summary>强制刷新 WorkBuddy 模型目录并返回刷新后的完整列表。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>刷新后的模型列表和成功标志。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> RefreshModelsAsync(PluginHttpContext context)
	{
		return context.Ok((object)new
		{
			models = (await _host.Models.RefreshAsync("workbuddy", context.CancellationToken)).Select(ToModelView).ToArray(),
			refreshed = true
		});
	}

	/// <summary>为国内或国际站创建有时限的 OAuth 状态，并返回官方授权地址。</summary>
	/// <param name="context">请求体可通过 `realm` 选择 `cn` 或 `global`。</param>
	/// <returns>需在后续轮询中提交的 state、授权 URL 和规范化后的站点。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> StartOAuthAsync(PluginHttpContext context)
	{
		string realm = ReadBodyString(context.Body, "realm") ?? "cn";
		if (!string.Equals(realm, "global", StringComparison.OrdinalIgnoreCase))
		{
			realm = "cn";
		}
		UpstreamResult upstreamResult = await SendOAuthAsync(HttpMethod.Post, BaseForRealm(realm) + "/v2/plugin/auth/state?platform=CLI", realm, new JsonObject(), null, null, context.CancellationToken);
		if (!upstreamResult.Success)
		{
			return Error(context, 502, "获取 OAuth 授权地址失败：" + upstreamResult.Message);
		}
		string text = ReadNodeString(upstreamResult.Data, "state");
		string text2 = ReadNodeString(upstreamResult.Data, "authUrl", "auth_url");
		if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(text2))
		{
			return Error(context, 502, "上游 OAuth 响应缺少 state 或 authUrl");
		}
		CleanupPendingLogins();
		_pendingLogins[text] = new PendingLogin(DateTimeOffset.UtcNow, realm);
		return context.Ok((object)new
		{
			ok = true,
			state = text,
			url = text2,
			realm = realm
		});
	}

	/// <summary>
	/// 查询 OAuth 授权状态。授权完成后读取 UID 和账号信息，安全校验 UID，
	/// 再新增账号或按 UID 更新已有账号。
	/// </summary>
	/// <param name="context">查询字符串中必须包含由 `oauth/start` 返回的 state。</param>
	/// <returns>等待状态或已保存账号的 ID、UID、昵称和令牌到期时间。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> PollOAuthAsync(PluginHttpContext context)
	{
		string state = (context.Query.TryGetValue("state", out var value) ? value : string.Empty);
		if (string.IsNullOrWhiteSpace(state))
		{
			return Error(context, 400, "缺少 OAuth state");
		}
		if (!_pendingLogins.TryGetValue(state, out PendingLogin pending))
		{
			return Error(context, 404, "OAuth state 已过期，请重新登录");
		}
		PendingLogin value2;
		if (DateTimeOffset.UtcNow - pending.CreatedAt > LoginTtl)
		{
			_pendingLogins.TryRemove(state, out value2);
			return Error(context, 410, "OAuth state 已过期，请重新登录");
		}
		UpstreamResult upstreamResult = await SendOAuthAsync(HttpMethod.Get, BaseForRealm(pending.Realm) + "/v2/plugin/auth/token?state=" + Uri.EscapeDataString(state), pending.Realm, null, null, null, context.CancellationToken);
		if (!upstreamResult.Success)
		{
			int statusCode = upstreamResult.StatusCode;
			if ((statusCode == 200 || (uint)(statusCode - 400) <= 1u || statusCode == 409) ? true : false)
			{
				return context.Ok((object)new
				{
					done = false,
					message = "等待官方登录完成"
				});
			}
			return Error(context, 502, "OAuth 轮询失败：" + upstreamResult.Message);
		}
		string accessToken = ReadNodeString(upstreamResult.Data, "accessToken", "access_token");
		if (string.IsNullOrWhiteSpace(accessToken))
		{
			return context.Ok((object)new
			{
				done = false,
				message = "等待官方登录完成"
			});
		}
		string refreshToken = ReadNodeString(upstreamResult.Data, "refreshToken", "refresh_token");
		string domain = ReadNodeString(upstreamResult.Data, "domain") ?? BaseForRealm(pending.Realm);
		long expiresIn = ReadNodeLong(upstreamResult.Data, "expiresIn", "expires_in").GetValueOrDefault();
		UpstreamResult upstreamResult2 = await SendOAuthAsync(HttpMethod.Get, BaseForRealm(pending.Realm) + "/v2/plugin/login/account?state=" + Uri.EscapeDataString(state), pending.Realm, null, accessToken, null, context.CancellationToken);
		if (!upstreamResult2.Success)
		{
			return Error(context, 502, "OAuth 已完成但读取账号信息失败：" + upstreamResult2.Message);
		}
		string uid = ReadNodeString(upstreamResult2.Data, "uid", "userId", "user_id");
		if (!IsSafeAccountId(uid))
		{
			return Error(context, 502, "上游返回的 UID 无效，拒绝保存账号");
		}
		string text = ReadNodeString(upstreamResult2.Data, "enterpriseId", "enterprise_id");
		string nickname = ReadNodeString(upstreamResult2.Data, "nickname", "displayName", "display_name");
		DateTimeOffset? expiresAt = ((expiresIn > 0) ? new DateTimeOffset?(DateTimeOffset.UtcNow.AddSeconds(expiresIn)) : ((DateTimeOffset?)null));
		OAuthCredential credential = new OAuthCredential(accessToken, expiresAt, refreshToken, (string)null, uid, domain, text, nickname);
		Account val = (await _host.Accounts.ListAsync("workbuddy", context.CancellationToken)).FirstOrDefault((Account item) =>
		{
			Credential credential2 = item.Credential;
			OAuthCredential val3 = (OAuthCredential)(object)((credential2 is OAuthCredential) ? credential2 : null);
			return val3 != null && string.Equals(val3.AccountId, uid, StringComparison.OrdinalIgnoreCase) && string.Equals(RealmFor(val3), RealmFor(credential), StringComparison.OrdinalIgnoreCase);
		});
		Account val2 = new Account();
		val2.set_Id(((val != null) ? val.Id : null) ?? Guid.NewGuid().ToString("N"));
		val2.PluginKey = _host.PluginKey;
		val2.Platform = "workbuddy";
		val2.Credential = (Credential)(object)credential;
		val2.Status = ((val != null) ? val.Status : null) ?? new ResourceStatus();
		val2.ExpiresAt = expiresAt;
		val2.Label = nickname ?? uid;
		Account account = val2;
		account.Status.State = (ResourceState)0;
		account.Status.CooldownUntil = null;
		account.Status.DisabledUntil = null;
		account.Status.Reason = null;
		account.Status.LastStatusCode = null;
		account.Status.ConsecutiveFailures = 0;
		await _host.Accounts.SaveAsync(account, context.CancellationToken);
		_host.Models.Invalidate("workbuddy");
		_pendingLogins.TryRemove(state, out value2);
		if (IsGlobal(credential))
		{
			try
			{
				await ActivateGlobalAccountAndClaimTrialAsync(credential, context.CancellationToken);
			}
			catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex2)
			{
				await TryLogAsync("account.global-activation.failed", "国际账号注册激活或 trial 领取失败：" + ex2.Message, "Warning", null, null, account.Id);
			}
		}
		QueueBalanceRefresh(account, force: true);
		return context.Ok((object)new
		{
			done = true,
			accountId = account.Id,
			uid = uid,
			nickname = nickname,
			realm = pending.Realm,
			expiresAt = expiresAt,
			creditsRefreshing = IsBalanceRefreshing(account.Id)
		});
	}

	/// <summary>手动刷新可刷新的账号令牌，并立即刷新所有账号的积分余额。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>两个刷新流程都已被当前请求执行后的成功标志。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public async Task<PluginResult> RefreshAccountsEndpointAsync(PluginHttpContext context)
	{
		await RefreshAccountsCoreAsync("workbuddy-token-refresh", context.CancellationToken);
		await RefreshBalancesCoreAsync(context.CancellationToken);
		return context.Ok((object)new
		{
			refreshed = true
		});
	}

	/// <summary>启动指定 WorkBuddy 定时任务的后台执行，并立即返回任务 ID。</summary>
	/// <param name="context">请求体中需包含已注册的 `task` ID。</param>
	/// <returns>已接受的后台任务 ID 和当前状态。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public Task<PluginResult> RunTaskEndpointAsync(PluginHttpContext context)
	{
		string task = ReadBodyString(context.Body, "task");
		if (string.IsNullOrWhiteSpace(task))
		{
			return Task.FromResult<PluginResult>(Error(context, 400, "缺少 task"));
		}
		string text = "Running";
		bool alreadyRunning = false;
		string taskId;
		lock (_manualTaskRunLock)
		{
			if (_activeManualTaskRunIds.TryGetValue(task, out string value))
			{
				taskId = value;
				if (_manualTaskRunSnapshots.TryGetValue(taskId, out ManualTaskRunSnapshot value2))
				{
					text = value2.Status;
				}
				alreadyRunning = text == "Running";
			}
			else
			{
				TrimManualTaskRunHistory();
				taskId = Guid.NewGuid().ToString("N");
				CancellationTokenSource cancellation = new CancellationTokenSource();
				_manualTaskRunSnapshots[taskId] = new ManualTaskRunSnapshot(taskId, task, "Running", DateTimeOffset.UtcNow, null, null);
				_activeManualTaskRunIds[task] = taskId;
				_manualTaskRunCancellations[taskId] = cancellation;
				_manualTaskRunTasks[taskId] = Task.Run(() => ExecuteManualTaskRunInBackgroundAsync(taskId, task, cancellation));
			}
		}
		return Task.FromResult<PluginResult>(context.Json(202, (object)new
		{
			accepted = true,
			alreadyRunning = alreadyRunning,
			taskId = taskId,
			task = task,
			status = text
		}));
	}

	/// <summary>读取手动启动的 WorkBuddy 定时任务状态。</summary>
	/// <param name="context">包含任务 ID 的管理端点上下文。</param>
	/// <returns>后台任务状态和执行失败信息。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public Task<PluginResult> ManualTaskRunStatusEndpointAsync(PluginHttpContext context)
	{
		if (!context.Query.TryGetValue("taskId", out var value) || string.IsNullOrWhiteSpace(value))
		{
			return Task.FromResult<PluginResult>(Error(context, 400, "缺少 taskId"));
		}
		ManualTaskRunSnapshot value2;
		lock (_manualTaskRunLock)
		{
			_manualTaskRunSnapshots.TryGetValue(value, out value2);
		}
		if ((object)value2 == null)
		{
			return Task.FromResult<PluginResult>(Error(context, 404, "手动任务不存在或状态已过期"));
		}
		return Task.FromResult<PluginResult>(context.Ok((object)new
		{
			taskId = value2.TaskId,
			task = value2.Task,
			status = value2.Status,
			startedAt = value2.StartedAt,
			finishedAt = value2.FinishedAt,
			error = value2.Error
		}));
	}

	private async Task ExecuteManualTaskRunInBackgroundAsync(string taskId, string taskName, CancellationTokenSource cancellation)
	{
		CancellationToken cancellationToken = cancellation.Token;
		try
		{
			bool flag = await _host.Tasks.RunAsync(taskName, "workbuddy", cancellationToken);
			if (cancellationToken.IsCancellationRequested)
			{
				UpdateManualTaskRunSnapshot(taskId, "Cancelled");
				WorkBuddyTerminal workBuddyTerminal = this;
				object details = new { taskId };
				await workBuddyTerminal.TryLogAsync("task.manual.cancelled", "手动定时任务后台执行已取消", "Warning", null, taskName, null, null, null, null, details);
				return;
			}
			UpdateManualTaskRunSnapshot(taskId, flag ? "Completed" : "Failed", flag ? null : "任务未成功执行，可能未找到任务或该任务已由其他入口启动。");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			UpdateManualTaskRunSnapshot(taskId, "Cancelled");
			WorkBuddyTerminal workBuddyTerminal2 = this;
			object details = new { taskId };
			await workBuddyTerminal2.TryLogAsync("task.manual.cancelled", "手动定时任务后台执行已取消", "Warning", null, taskName, null, null, null, null, details);
		}
		catch (Exception ex2)
		{
			UpdateManualTaskRunSnapshot(taskId, "Failed", ex2.Message);
			WorkBuddyTerminal workBuddyTerminal3 = this;
			string message = "手动定时任务后台执行失败：" + ex2.Message;
			object details = new
			{
				taskId = taskId,
				exception = ex2.GetType().Name
			};
			await workBuddyTerminal3.TryLogAsync("task.manual.failed", message, "Error", null, taskName, null, null, null, null, details);
		}
		finally
		{
			lock (_manualTaskRunLock)
			{
				if (_activeManualTaskRunIds.TryGetValue(taskName, out string value) && value == taskId)
				{
					_activeManualTaskRunIds.Remove(taskName);
				}
				_manualTaskRunTasks.Remove(taskId);
				if (_manualTaskRunCancellations.Remove(taskId, out CancellationTokenSource value2))
				{
					value2.Dispose();
				}
			}
		}
	}

	private void UpdateManualTaskRunSnapshot(string taskId, string status, string? error = null)
	{
		lock (_manualTaskRunLock)
		{
			if (_manualTaskRunSnapshots.TryGetValue(taskId, out ManualTaskRunSnapshot value))
			{
				_manualTaskRunSnapshots[taskId] = value with
				{
					Status = status,
					FinishedAt = DateTimeOffset.UtcNow,
					Error = error
				};
			}
		}
	}

	private void TrimManualTaskRunHistory()
	{
		int num = _manualTaskRunSnapshots.Count - 64 + 1;
		if (num > 0)
		{
			ManualTaskRunSnapshot[] array = (from snapshot in _manualTaskRunSnapshots.Values
				where snapshot.Status != "Running"
				orderby snapshot.StartedAt
				select snapshot).Take(num).ToArray();
			foreach (ManualTaskRunSnapshot manualTaskRunSnapshot in array)
			{
				_manualTaskRunSnapshots.Remove(manualTaskRunSnapshot.TaskId);
			}
		}
	}

	/// <summary>启动国内站成长任务后台执行，并立即返回任务 ID。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>已接受的后台任务 ID 和当前状态。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public Task<PluginResult> CompleteGrowthTasksEndpointAsync(PluginHttpContext context)
	{
		string taskId;
		lock (_growthRunLock)
		{
			if (_growthRunSnapshot.Status == "Running")
			{
				return Task.FromResult<PluginResult>(context.Json(202, (object)new
				{
					accepted = true,
					alreadyRunning = true,
					taskId = _growthRunSnapshot.TaskId,
					status = _growthRunSnapshot.Status
				}));
			}
			_growthRunCancellation?.Dispose();
			CancellationTokenSource growthRunCancellation = new CancellationTokenSource();
			_growthRunCancellation = growthRunCancellation;
			taskId = Guid.NewGuid().ToString("N");
			_growthRunSnapshot = new GrowthRunSnapshot(taskId, "Running", DateTimeOffset.UtcNow, null, null);
			_growthRunTask = Task.Run(() => ExecuteGrowthTasksOneClickInBackgroundAsync(taskId, growthRunCancellation.Token));
		}
		return Task.FromResult<PluginResult>(context.Json(202, (object)new
		{
			accepted = true,
			alreadyRunning = false,
			taskId = taskId,
			status = "Running"
		}));
	}

	/// <summary>读取成长任务一键执行的后台状态。</summary>
	/// <param name="context">管理员管理端点上下文。</param>
	/// <returns>最近一次执行的任务 ID、状态、开始/结束时间和失败信息。</returns>
	[PluginEndpoint(/*Could not decode attribute arguments.*/)]
	public Task<PluginResult> GrowthTasksStatusEndpointAsync(PluginHttpContext context)
	{
		GrowthRunSnapshot growthRunSnapshot;
		lock (_growthRunLock)
		{
			growthRunSnapshot = _growthRunSnapshot;
		}
		return Task.FromResult<PluginResult>(context.Ok((object)new
		{
			taskId = growthRunSnapshot.TaskId,
			status = growthRunSnapshot.Status,
			startedAt = growthRunSnapshot.StartedAt,
			finishedAt = growthRunSnapshot.FinishedAt,
			error = growthRunSnapshot.Error
		}));
	}

	/// <summary>
	/// 刷新临近过期凭据，将标准聊天请求映射到 WorkBuddy Chat Completions，
	/// 并把普通或 SSE 上游响应转换为宿主标准响应。
	/// </summary>
	/// <param name="context">本次尝试的账号、请求、宿主管理的 HTTP 客户端、追踪信息和取消令牌。</param>
	/// <returns>聊天响应以及宿主用于成功、重试、冷却账号/节点或停用账号的执行结果。</returns>
	public async Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
	{
		PluginInvocationResult val = await InvokeCoreAsync(context);
		PluginAttemptDecision val2 = (PluginAttemptDecision)(val.Attempt.Decision ?? ((!val.Response.IsSuccess) ? ((object)FailureDecision(val.Attempt)) : ((object)new PluginAttemptDecision())));
		PluginInvocationResult val3 = val._003CClone_003E_0024();
		val3.set_Attempt(val2.ToResult(val.Attempt.StatusCode, val.Attempt.Reason));
		return val3;
	}

	private static PluginAttemptDecision FailureDecision(PluginAttemptResult attempt)
	{
		//IL_006d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0074: Expected Obj, but got Unknown
		bool indicatesInvalidCredential = attempt.IndicatesInvalidCredential;
		bool flag = !indicatesInvalidCredential;
		bool flag2;
		if (flag)
		{
			int? statusCode = attempt.StatusCode;
			if (statusCode.HasValue)
			{
				int valueOrDefault = statusCode.GetValueOrDefault();
				if (valueOrDefault >= 500)
				{
					goto IL_0060;
				}
				if (valueOrDefault <= 408)
				{
					if (valueOrDefault == 401 || valueOrDefault == 408)
					{
						goto IL_0060;
					}
				}
				else if (valueOrDefault == 425 || valueOrDefault == 429)
				{
					goto IL_0060;
				}
			}
			flag2 = false;
			goto IL_0068;
		}
		goto IL_006b;
		IL_0068:
		flag = flag2;
		goto IL_006b;
		IL_0060:
		flag2 = true;
		goto IL_0068;
		IL_00e3:
		PluginAttemptDecision val;
		val.set_Retry((PluginRetryAction)(flag ? 1 : 0));
		PluginAttemptDecision val2;
		bool flag3;
		val2.set_AccountAction((PluginAccountAction)(indicatesInvalidCredential ? 2 : (flag3 ? 1 : 0)));
		val2.set_AccountCooldownUntil(flag3 ? new DateTimeOffset?(DateTimeOffset.UtcNow.AddMinutes(5.0)) : ((DateTimeOffset?)null));
		val2.set_ProxyAction((PluginProxyAction)((attempt.IsTransportFailure || attempt.StatusCode == 407) ? 1 : 0));
		val2.set_ReasonCode("workbuddy.upstream");
		return val2;
		IL_006b:
		flag3 = flag;
		val2 = new PluginAttemptDecision();
		val2.set_FailureKind((PluginFailureKind)(attempt.IsTransportFailure ? 3 : ((!indicatesInvalidCredential) ? 1 : 2)));
		val = val2;
		flag = attempt.IsTransportFailure;
		if (!flag)
		{
			int? statusCode = attempt.StatusCode;
			if (statusCode.HasValue)
			{
				int valueOrDefault = statusCode.GetValueOrDefault();
				if (valueOrDefault >= 500 || valueOrDefault == 408 || valueOrDefault == 425 || valueOrDefault == 429)
				{
					flag2 = true;
					goto IL_00e0;
				}
			}
			flag2 = false;
			goto IL_00e0;
		}
		goto IL_00e3;
		IL_00e0:
		flag = flag2;
		goto IL_00e3;
	}

	private async Task<PluginInvocationResult> InvokeCoreAsync(PluginAttemptContext context)
	{
		Stopwatch stopwatch = Stopwatch.StartNew();
		string requestedModel = NormalizeModel(context.Request.Model);
		await TryLogAsync("request.started", "WorkBuddy 上游请求开始", "Debug", context.TraceId, null, context.Account.Id, requestedModel);
		Credential credential = context.Account.Credential;
		OAuthCredential credential2 = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
		if (credential2 == null || !HasUsableToken(credential2))
		{
			await TryLogAsync("credential.invalid", "账号缺少可用 WorkBuddy OAuth 凭证", "Error", context.TraceId, null, context.Account.Id, requestedModel);
			return new PluginInvocationResult(AdapterResponse.Unauthorized("missing WorkBuddy OAuth credential"), new PluginAttemptResult((PluginAttemptOutcome)4, (int?)401, false, true, "missing-oauth-credential"), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		Account account = await RefreshIfNeededAsync(context.Account, credential2, context.CancellationToken);
		Credential credential3 = account.Credential;
		credential2 = (OAuthCredential)(((object)((credential3 is OAuthCredential) ? credential3 : null)) ?? ((object)credential2));
		if ((int)account.Status.State == 4)
		{
			return new PluginInvocationResult(AdapterResponse.Unauthorized("WorkBuddy account is disabled; complete OAuth login to restore it"), new PluginAttemptResult((PluginAttemptOutcome)5, (int?)null, false, false, "workbuddy-account-disabled"), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		string model = NormalizeModel(context.Request.Model);
		JsonObject requestBody;
		try
		{
			requestBody = BuildChatRequestBody(context.Request, model);
		}
		catch (NotSupportedException ex)
		{
			return new PluginInvocationResult(AdapterResponse.BadRequest(ex.Message), new PluginAttemptResult((PluginAttemptOutcome)5, (int?)400, false, false, ex.Message), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		string bufferedError = null;
		HttpResponseMessage response;
		try
		{
			response = await SendChatRequestAsync(requestBody);
			if (!response.IsSuccessStatusCode)
			{
				try
				{
					bufferedError = await response.Content.ReadAsStringAsync(context.CancellationToken);
				}
				catch
				{
					response.Dispose();
					throw;
				}
				if (IsContentBlockedResponse((int)response.StatusCode, bufferedError))
				{
					int statusCode = (int)response.StatusCode;
					response.Dispose();
					await TryLogAsync("upstream.content-blocked.retry", "WorkBuddy 内容策略拦截，替换 system prompt 后重试一次", "Warning", context.TraceId, null, context.Account.Id, model, statusCode);
					response = await SendChatRequestAsync(CreateDegradedChatRequestBody(requestBody));
					bufferedError = null;
				}
			}
		}
		catch (HttpRequestException ex2)
		{
			WorkBuddyTerminal workBuddyTerminal = this;
			string message = ex2.Message;
			string traceId = context.TraceId;
			string id = context.Account.Id;
			int? durationMs = (int)stopwatch.ElapsedMilliseconds;
			await workBuddyTerminal.TryLogAsync("upstream.exception", message, "Error", traceId, null, id, model, null, durationMs);
			AdapterResponse val = AdapterResponse.ServerError(ex2.Message);
			string message2 = ex2.Message;
			return new PluginInvocationResult(val, new PluginAttemptResult((PluginAttemptOutcome)2, (int?)null, true, false, message2), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		catch (OperationCanceledException) when (!context.CancellationToken.IsCancellationRequested)
		{
			WorkBuddyTerminal workBuddyTerminal2 = this;
			string traceId2 = context.TraceId;
			string id2 = context.Account.Id;
			int? durationMs = (int)stopwatch.ElapsedMilliseconds;
			await workBuddyTerminal2.TryLogAsync("upstream.timeout", "WorkBuddy 上游请求超时", "Error", traceId2, null, id2, model, null, durationMs);
			return new PluginInvocationResult(AdapterResponse.ServerError("WorkBuddy upstream request timed out"), new PluginAttemptResult((PluginAttemptOutcome)2, (int?)null, true, false, "timeout"), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		if (!response.IsSuccessStatusCode)
		{
			try
			{
				string message2 = bufferedError;
				if (message2 == null)
				{
					message2 = await response.Content.ReadAsStringAsync(context.CancellationToken);
				}
				string error = message2;
				int statusCode2 = (int)response.StatusCode;
				bool flag = IsSessionDeadError(error);
				bool flag2 = IsPermanentAccountFault(error);
				bool flag3 = IsHardCreditError(statusCode2, error);
				bool flag4 = IsTrialNotActivated(error);
				bool invalidCredential = false;
				string attemptReason = error;
				PluginAttemptDecision decision = null;
				int num;
				PluginAttemptOutcome outcome;
				PluginAttemptDecision val3;
				PluginAttemptDecision val2;
				bool flag5;
				if (flag)
				{
					num = await RecordSessionDeadFailureAsync(context.Account, statusCode2, context.CancellationToken, applyDisable: false);
					outcome = (PluginAttemptOutcome)5;
					attemptReason = $"WorkBuddy 12153 session dead ({num}/3)";
					val2 = new PluginAttemptDecision();
					val2.set_FailureKind((PluginFailureKind)1);
					val3 = val2;
					if (statusCode2 < 500)
					{
						int num2 = statusCode2;
						if (num2 != 408 && num2 != 425 && num2 != 429)
						{
							flag5 = false;
							goto IL_0b14;
						}
					}
					flag5 = true;
					goto IL_0b14;
				}
				PluginAttemptOutcome val4;
				if (!flag2)
				{
					if (flag3)
					{
						if (statusCode2 < 500)
						{
							int num2 = statusCode2;
							if (num2 != 408 && num2 != 425 && num2 != 429)
							{
								flag5 = false;
								goto IL_0bb9;
							}
						}
						flag5 = true;
						goto IL_0bb9;
					}
					if (flag4)
					{
						outcome = (PluginAttemptOutcome)5;
						val3 = new PluginAttemptDecision();
						val3.set_FailureKind((PluginFailureKind)1);
						val2 = val3;
						if (statusCode2 < 500)
						{
							int num2 = statusCode2;
							if (num2 != 408 && num2 != 425 && num2 != 429)
							{
								flag5 = false;
								goto IL_0c8d;
							}
						}
						flag5 = true;
						goto IL_0c8d;
					}
					if (statusCode2 != 407)
					{
						if (statusCode2 < 500)
						{
							int num2 = statusCode2;
							if (num2 != 408 && num2 != 425 && num2 != 429)
							{
								flag5 = false;
								goto IL_0d33;
							}
						}
						flag5 = true;
						goto IL_0d33;
					}
					val4 = (PluginAttemptOutcome)2;
					goto IL_0d3d;
				}
				invalidCredential = true;
				outcome = (PluginAttemptOutcome)4;
				attemptReason = "WorkBuddy 返回 request illegal (11140)，账号已停用；请重新 OAuth 登录恢复";
				goto IL_0df3;
				IL_0d3d:
				outcome = val4;
				goto IL_0df3;
				IL_0df3:
				await TryLogAsync("upstream.response", error, "Error", context.TraceId, null, context.Account.Id, model, statusCode2, (int)stopwatch.ElapsedMilliseconds);
				AdapterResponse val5 = new AdapterResponse();
				val5.set_StatusCode(statusCode2);
				val5.set_Error(error);
				PluginAttemptResult val6 = new PluginAttemptResult(outcome, (int?)statusCode2, false, invalidCredential, attemptReason);
				val6.set_Decision(decision);
				return new PluginInvocationResult(val5, val6, (IReadOnlyList<RequestAttemptDetail>)null);
				IL_0bb9:
				outcome = (PluginAttemptOutcome)(flag5 ? 1 : 5);
				attemptReason = "WorkBuddy 积分耗尽，冷却至本地次日 04:00: " + error;
				PluginAttemptDecision val7 = new PluginAttemptDecision();
				val7.set_FailureKind((PluginFailureKind)1);
				val7.set_Retry((PluginRetryAction)((int)outcome == 1));
				val7.set_AccountAction((PluginAccountAction)1);
				val7.set_AccountCooldownUntil((DateTimeOffset?)NextLocalFourAm());
				val7.set_AccountReason("WorkBuddy 积分耗尽，冷却至本地次日 04:00");
				val7.set_ReasonCode("workbuddy.credit-exhausted");
				decision = val7;
				goto IL_0df3;
				IL_0d33:
				val4 = (PluginAttemptOutcome)(flag5 ? 1 : 5);
				goto IL_0d3d;
				IL_0c8d:
				val2.set_Retry((PluginRetryAction)(flag5 ? 1 : 0));
				val3.set_AccountAction((PluginAccountAction)1);
				val3.set_AccountCooldownUntil((DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(5.0));
				val3.set_AccountReason("workbuddy-trial-not-activated");
				val3.set_ReasonCode("workbuddy.trial-not-activated");
				decision = val3;
				goto IL_0df3;
				IL_0b14:
				val3.set_Retry((PluginRetryAction)(flag5 ? 1 : 0));
				val2.set_AccountAction((PluginAccountAction)((num >= 3) ? 2 : 0));
				val2.set_AccountReason("WorkBuddy 12153 连续失败达到 3 次，账号已停用；请重新 OAuth 登录恢复");
				val2.set_ReasonCode("workbuddy.session-dead");
				decision = val2;
				goto IL_0df3;
			}
			finally
			{
				response.Dispose();
			}
		}
		try
		{
			if (context.Request.Stream)
			{
				await TryLogAsync("upstream.response", "WorkBuddy SSE 请求已建立", "Debug", context.TraceId, null, context.Account.Id, model, (int)response.StatusCode, (int)stopwatch.ElapsedMilliseconds);
				AdapterResponse val8 = new AdapterResponse();
				val8.set_StatusCode((int)response.StatusCode);
				val8.set_IsStreaming(true);
				val8.set_Stream(ReadStreamAsync(response, account, context, context.CancellationToken));
				return new PluginInvocationResult(val8, new PluginAttemptResult((PluginAttemptOutcome)0, (int?)(int)response.StatusCode, false, false, (string)null), (IReadOnlyList<RequestAttemptDetail>)null);
			}
			AdapterCompletion completion = await ReadCompletionAsync(response, model, account, context.CancellationToken);
			if (string.IsNullOrWhiteSpace(completion.Content))
			{
				IReadOnlyList<AdapterToolCall> toolCalls = completion.ToolCalls;
				if (toolCalls == null || toolCalls.Count <= 0)
				{
					Usage usage = completion.Usage;
					string value = ((usage != null) ? usage.CompletionTokens.ToString(CultureInfo.InvariantCulture) : "unknown");
					string attemptReason = $"WorkBuddy upstream returned no answer content or tool calls (finish_reason={completion.FinishReason}, reasoning_present={completion.ReasoningContent != null}, completion_tokens={value})";
					await TryLogAsync("upstream.empty-response", attemptReason, "Error", context.TraceId, null, context.Account.Id, model, 502, (int)stopwatch.ElapsedMilliseconds, new
					{
						downstreamRequest = new
						{
							body = context.Request.OriginalBody
						}
					});
					AdapterResponse val9 = new AdapterResponse();
					val9.set_StatusCode(502);
					val9.set_Error(attemptReason);
					val9.set_ErrorType("server_error");
					PluginAttemptDecision val10 = new PluginAttemptDecision();
					val10.set_FailureKind((PluginFailureKind)4);
					val10.set_AccountAction((PluginAccountAction)0);
					val10.set_ProxyAction((PluginProxyAction)0);
					val10.set_Retry((PluginRetryAction)0);
					val10.set_ReasonCode("workbuddy.empty-response");
					return new PluginInvocationResult(val9, val10.ToResult((int?)502, "upstream-empty-response"), (IReadOnlyList<RequestAttemptDetail>)null);
				}
			}
			await TryLogAsync("upstream.response", "WorkBuddy 请求成功", "Debug", context.TraceId, null, context.Account.Id, model, 200, (int)stopwatch.ElapsedMilliseconds);
			AdapterResponse val11 = new AdapterResponse();
			val11.set_StatusCode(200);
			val11.set_Completion(completion);
			return new PluginInvocationResult(val11, new PluginAttemptResult((PluginAttemptOutcome)0, (int?)200, false, false, (string)null), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		catch (JsonException ex4)
		{
			response.Dispose();
			WorkBuddyTerminal workBuddyTerminal3 = this;
			string message3 = ex4.Message;
			string traceId3 = context.TraceId;
			string id3 = context.Account.Id;
			int? durationMs = (int)stopwatch.ElapsedMilliseconds;
			await workBuddyTerminal3.TryLogAsync("response.invalid", message3, "Error", traceId3, null, id3, model, null, durationMs);
			return new PluginInvocationResult(AdapterResponse.ServerError("WorkBuddy response parse failed: " + ex4.Message), new PluginAttemptResult((PluginAttemptOutcome)1, (int?)502, false, false, ex4.Message), (IReadOnlyList<RequestAttemptDetail>)null);
		}
		async Task<HttpResponseMessage> SendChatRequestAsync(JsonObject body)
		{
			using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential2) + "/v2/chat/completions")
			{
				Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
			};
			ApplyHeaders(request, credential2, includeAuthorization: true, streaming: true);
			ApplyConversationHeaders(request, context);
			return await context.HttpClient.SendAsync(request, false, HttpCompletionOption.ResponseHeadersRead, context.CancellationToken);
		}
	}

	/// <summary>将 WorkBuddy 诊断事件写入宿主日志；日志服务故障不改变请求处理结果。</summary>
	private async Task TryLogAsync(string eventType, string message, string level = "Debug", string? traceId = null, string? taskName = null, string? accountId = null, string? model = null, int? statusCode = null, int? durationMs = null, object? details = null)
	{
		try
		{
			await PluginLogExtensions.LogAsync(_host, "workbuddy", eventType, message, level, traceId, taskName, accountId, model, statusCode, durationMs, details, CancellationToken.None);
		}
		catch
		{
		}
	}

	/// <summary>按 panel 的国际账号登录流程补全注册地区并尝试领取一次性 trial 加油包。</summary>
	private async Task ActivateGlobalAccountAndClaimTrialAsync(OAuthCredential credential, CancellationToken cancellationToken)
	{
		using HttpClient client = CreateDirectClient(cancellationToken);
		GlobalAccountResponse globalAccountResponse = await SendGlobalAccountRequestAsync(client, credential, HttpMethod.Get, "/auth/realms/copilot/overseas/user/register?userId=" + Uri.EscapeDataString(credential.AccountId ?? string.Empty), null, webClient: true, includeUserId: true, cancellationToken);
		if (globalAccountResponse.Code != 200)
		{
			if (globalAccountResponse.Code != 500 && !globalAccountResponse.Message.Contains("region required", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException($"国际账号注册未激活：{globalAccountResponse.Message} (code={globalAccountResponse.Code})");
			}
			GlobalAccountResponse globalAccountResponse2 = await SendGlobalAccountRequestAsync(client, credential, HttpMethod.Post, "/billing/area/get-country-code", new JsonObject { ["filterForbidden"] = 1 }, webClient: true, includeUserId: false, cancellationToken);
			EnsureGlobalSuccess(globalAccountResponse2, "读取国际账号可选地区");
			JsonObject jsonObject = SelectGlobalCountry(globalAccountResponse2.Data);
			if (jsonObject == null)
			{
				throw new InvalidOperationException("国际账号没有可用于注册激活的地区");
			}
			EnsureGlobalSuccess(await SendGlobalAccountRequestAsync(client, credential, HttpMethod.Post, "/console/login/account", new JsonObject { ["attributes"] = new JsonObject
			{
				["countryCode"] = new JsonArray((JsonNode)JsonValue.Create(ReadNodeString(jsonObject, "Code"))),
				["countryFullName"] = new JsonArray((JsonNode)JsonValue.Create(ReadNodeString(jsonObject, "EnName"))),
				["countryName"] = new JsonArray((JsonNode)JsonValue.Create(ReadNodeString(jsonObject, "IOS2")))
			} }, webClient: true, includeUserId: false, cancellationToken), "提交国际账号注册地区");
			globalAccountResponse = await SendGlobalAccountRequestAsync(client, credential, HttpMethod.Get, "/auth/realms/copilot/overseas/user/register?userId=" + Uri.EscapeDataString(credential.AccountId ?? string.Empty), null, webClient: true, includeUserId: true, cancellationToken);
			if (globalAccountResponse.Code != 200)
			{
				throw new InvalidOperationException($"国际账号提交地区后仍未激活：{globalAccountResponse.Message} (code={globalAccountResponse.Code})");
			}
		}
		GlobalAccountResponse globalAccountResponse3 = await SendGlobalAccountRequestAsync(client, credential, HttpMethod.Post, "/billing/ide/trial", null, webClient: false, includeUserId: false, cancellationToken);
		int code = globalAccountResponse3.Code;
		if ((code != 0 && code != 14051) || 1 == 0)
		{
			throw new InvalidOperationException($"国际账号 trial 加油包领取失败：{globalAccountResponse3.Message} (code={globalAccountResponse3.Code})");
		}
		await TryLogAsync("account.global-activation.completed", (globalAccountResponse3.Code == 14051) ? "国际账号已激活，trial 加油包此前已领取" : "国际账号已激活并领取 trial 加油包", "Information", null, null, credential.AccountId);
	}

	private async Task<GlobalAccountResponse> SendGlobalAccountRequestAsync(HttpClient client, OAuthCredential credential, HttpMethod method, string path, JsonNode? body, bool webClient, bool includeUserId, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new HttpRequestMessage(method, "https://www.workbuddy.ai" + path);
		if (body != null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}
		if (webClient)
		{
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
			request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
			request.Headers.TryAddWithoutValidation("Origin", "https://www.workbuddy.ai");
			request.Headers.TryAddWithoutValidation("Referer", "https://www.workbuddy.ai/");
			request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
			if (!string.IsNullOrWhiteSpace(credential.AccessToken))
			{
				request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
			}
			if (includeUserId && !string.IsNullOrWhiteSpace(credential.AccountId))
			{
				request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
			}
		}
		else
		{
			ApplyHeaders(request, credential, includeAuthorization: true, streaming: false);
		}
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
		string text = await response.Content.ReadAsStringAsync(cancellationToken);
		JsonNode jsonNode;
		try
		{
			jsonNode = JsonNode.Parse(text);
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException("国际账号接口返回了无效 JSON：" + ex.Message);
		}
		if (!response.IsSuccessStatusCode)
		{
			throw new HttpRequestException($"国际账号接口 HTTP {(int)response.StatusCode}：{text}");
		}
		long? num = ReadNodeLong(jsonNode, "code");
		int code;
		if (num.HasValue)
		{
			long valueOrDefault = num.GetValueOrDefault();
			code = (int)valueOrDefault;
		}
		else
		{
			code = 0;
		}
		return new GlobalAccountResponse(code, ReadNodeString(jsonNode, "msg", "message") ?? string.Empty, jsonNode?["data"]?.DeepClone());
	}

	private static void EnsureGlobalSuccess(GlobalAccountResponse response, string operation)
	{
		if (response.Code != 0)
		{
			throw new InvalidOperationException($"{operation}失败：{response.Message} (code={response.Code})");
		}
	}

	private static JsonObject? SelectGlobalCountry(JsonNode? data)
	{
		if (data is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value))
		{
			try
			{
				data = JsonNode.Parse(value);
			}
			catch (JsonException)
			{
				return null;
			}
		}
		JsonArray jsonArray = (data?["data"]?["list"] as JsonArray) ?? (data?["list"] as JsonArray);
		if (jsonArray == null)
		{
			return null;
		}
		JsonObject[] source = jsonArray.OfType<JsonObject>().ToArray();
		string[] array = new string[7] { "HK", "MO", "SG", "TH", "PH", "MY", "ID" };
		foreach (string code in array)
		{
			JsonObject jsonObject = source.FirstOrDefault((JsonObject item) => string.Equals(ReadNodeString(item, "IOS2"), code, StringComparison.OrdinalIgnoreCase));
			if (jsonObject != null && !string.IsNullOrWhiteSpace(ReadNodeString(jsonObject, "Code")) && !string.IsNullOrWhiteSpace(ReadNodeString(jsonObject, "EnName")))
			{
				return jsonObject;
			}
		}
		return null;
	}

	private async Task ExecuteGrowthTasksOneClickInBackgroundAsync(string taskId, CancellationToken cancellationToken)
	{
		_ = 2;
		try
		{
			await RunForAccountsAsync(RunGrowthTasksOneClickAsync, "workbuddy-growth-one-click", cnOnly: true, cancellationToken);
			UpdateGrowthRunSnapshot(taskId, "Completed");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			UpdateGrowthRunSnapshot(taskId, "Cancelled");
			WorkBuddyTerminal workBuddyTerminal = this;
			object details = new { taskId };
			await workBuddyTerminal.TryLogAsync("growth.run.cancelled", "成长任务后台执行已取消", "Warning", null, "workbuddy-growth-one-click", null, null, null, null, details);
		}
		catch (Exception ex2)
		{
			UpdateGrowthRunSnapshot(taskId, "Failed", ex2.Message);
			WorkBuddyTerminal workBuddyTerminal2 = this;
			string message = "成长任务后台执行失败：" + ex2.Message;
			object details = new
			{
				taskId = taskId,
				exception = ex2.GetType().Name
			};
			await workBuddyTerminal2.TryLogAsync("growth.run.failed", message, "Error", null, "workbuddy-growth-one-click", null, null, null, null, details);
		}
		finally
		{
			lock (_growthRunLock)
			{
				if (_growthRunSnapshot.TaskId == taskId)
				{
					_growthRunTask = null;
				}
			}
		}
	}

	private void UpdateGrowthRunSnapshot(string taskId, string status, string? error = null)
	{
		lock (_growthRunLock)
		{
			if (!(_growthRunSnapshot.TaskId != taskId))
			{
				_growthRunSnapshot = _growthRunSnapshot with
				{
					Status = status,
					FinishedAt = DateTimeOffset.UtcNow,
					Error = error
				};
			}
		}
	}

	private async Task RunGrowthTasksOneClickAsync(Account account, OAuthCredential credential, CancellationToken cancellationToken)
	{
		Credential credential2 = (await RefreshIfNeededAsync(account, credential, cancellationToken)).Credential;
		credential = (OAuthCredential)(((object)((credential2 is OAuthCredential) ? credential2 : null)) ?? ((object)credential));
		using HttpClient client = CreateDirectClient(cancellationToken);
		JsonArray regularTasks = await FetchGrowthTasksAsync(client, credential, miniProgram: false, cancellationToken);
		JsonArray miniTasks;
		try
		{
			miniTasks = await FetchGrowthTasksAsync(client, credential, miniProgram: true, cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			miniTasks = new JsonArray();
			await TryLogAsync("growth.tasks.mp-list.failed", ex.Message, "Warning", null, "workbuddy-growth-one-click", account.Id);
		}
		await AcceptGrowthTasksAsync(client, credential, regularTasks, miniProgram: false, cancellationToken);
		await AcceptGrowthTasksAsync(client, credential, miniTasks, miniProgram: true, cancellationToken);
		if (miniTasks.Count > 0)
		{
			try
			{
				miniTasks = await FetchGrowthTasksAsync(client, credential, miniProgram: true, cancellationToken);
			}
			catch (Exception ex2) when (!cancellationToken.IsCancellationRequested)
			{
				await TryLogAsync("growth.tasks.mp-refresh.failed", ex2.Message, "Warning", null, "workbuddy-growth-one-click", account.Id);
			}
		}
		string[] growthAutoTaskCodes = GrowthAutoTaskCodes;
		foreach (string taskCode in growthAutoTaskCodes)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool isMiniTask = GrowthMiniTaskCodes.Contains(taskCode);
			GrowthTaskSnapshot task = FindGrowthTask(isMiniTask ? miniTasks : regularTasks, taskCode);
			if ((object)task == null)
			{
				WorkBuddyTerminal workBuddyTerminal = this;
				string message = "未下发成长任务：" + taskCode;
				string id = account.Id;
				object details = new { taskCode };
				await workBuddyTerminal.TryLogAsync("growth.task.skipped", message, "Debug", null, "workbuddy-growth-one-click", id, null, null, null, details);
				continue;
			}
			if (task.Locked)
			{
				WorkBuddyTerminal workBuddyTerminal2 = this;
				string message2 = "成长任务尚未解锁：" + taskCode;
				string id2 = account.Id;
				object details = new { taskCode, task.Current, task.Target };
				await workBuddyTerminal2.TryLogAsync("growth.task.locked", message2, "Debug", null, "workbuddy-growth-one-click", id2, null, null, null, details);
				continue;
			}
			try
			{
				object details;
				if (task.Claimed)
				{
					WorkBuddyTerminal workBuddyTerminal3 = this;
					string message3 = "成长任务已领奖：" + taskCode;
					string id3 = account.Id;
					details = new { taskCode, task.Current, task.Target };
					await workBuddyTerminal3.TryLogAsync("growth.task.already-claimed", message3, "Debug", null, "workbuddy-growth-one-click", id3, null, null, null, details);
					continue;
				}
				if (task.IsClaimable)
				{
					await ClaimGrowthTaskAsync(client, credential, taskCode, isMiniTask, cancellationToken);
					continue;
				}
				bool flag = isMiniTask && task.AcceptStatus != null && !task.AcceptStatus.Equals("accepted", StringComparison.OrdinalIgnoreCase) && !task.AcceptStatus.Equals("completed", StringComparison.OrdinalIgnoreCase);
				bool flag2 = isMiniTask && !taskCode.Equals("Sequential_Tasks_2", StringComparison.OrdinalIgnoreCase) && (flag || string.IsNullOrWhiteSpace(task.AcceptStatus));
				if (flag2)
				{
					flag2 = !(await EnsureMiniTaskAcceptedAsync(client, credential, taskCode, cancellationToken));
				}
				if (flag2)
				{
					await TryLogAsync("growth.task.accept-unverified", "小程序任务接受状态未确认，跳过本轮：" + taskCode, "Warning", null, "workbuddy-growth-one-click", account.Id);
					continue;
				}
				string message4 = await ExecuteGrowthTaskActionAsync(client, account, credential, task, cancellationToken);
				GrowthTaskSnapshot after = await WaitForGrowthTaskAsync(client, credential, taskCode, isMiniTask, cancellationToken);
				if (after?.IsClaimable ?? false)
				{
					await ClaimGrowthTaskAsync(client, credential, taskCode, isMiniTask, cancellationToken);
				}
				WorkBuddyTerminal workBuddyTerminal4 = this;
				string id4 = account.Id;
				details = new
				{
					taskCode = taskCode,
					before = $"{task.Current}/{task.Target}",
					after = (((object)after == null) ? "未回读" : $"{after.Current}/{after.Target}"),
					claimed = (after?.Claimed ?? false)
				};
				await workBuddyTerminal4.TryLogAsync("growth.task.completed", message4, "Information", null, "workbuddy-growth-one-click", id4, null, null, null, details);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex4)
			{
				WorkBuddyTerminal workBuddyTerminal5 = this;
				string message5 = "成长任务执行失败：" + taskCode + "：" + ex4.Message;
				string id5 = account.Id;
				object details = new
				{
					taskCode = taskCode,
					exception = ex4.GetType().Name
				};
				await workBuddyTerminal5.TryLogAsync("growth.task.failed", message5, "Warning", null, "workbuddy-growth-one-click", id5, null, null, null, details);
			}
			await Task.Delay(TimeSpan.FromMilliseconds(1050L), cancellationToken);
		}
		await ClaimReadyGrowthTasksAsync(client, account, credential, miniProgram: false, cancellationToken);
		await ClaimReadyGrowthTasksAsync(client, account, credential, miniProgram: true, cancellationToken);
	}

	private async Task ClaimReadyGrowthTasksAsync(HttpClient client, Account account, OAuthCredential credential, bool miniProgram, CancellationToken cancellationToken)
	{
		JsonArray tasks;
		try
		{
			tasks = await FetchGrowthTasksAsync(client, credential, miniProgram, cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex2)
		{
			WorkBuddyTerminal workBuddyTerminal = this;
			string message = ex2.Message;
			string id = account.Id;
			object details = new { miniProgram };
			await workBuddyTerminal.TryLogAsync("growth.tasks.final-list.failed", message, "Warning", null, "workbuddy-growth-one-click", id, null, null, null, details);
			return;
		}
		foreach (GrowthTaskSnapshot task in from item in tasks.OfType<JsonObject>().Select(ReadGrowthTask)
			where (object)item != null && item.IsClaimable && (!miniProgram || GrowthMiniTaskCodes.Contains(item.TaskCode))
			select item)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await ClaimGrowthTaskAsync(client, credential, task.TaskCode, miniProgram, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex4)
			{
				WorkBuddyTerminal workBuddyTerminal2 = this;
				string message2 = "成长任务领奖失败：" + task.TaskCode + "：" + ex4.Message;
				string id2 = account.Id;
				object details = new
				{
					taskCode = task.TaskCode,
					miniProgram = miniProgram
				};
				await workBuddyTerminal2.TryLogAsync("growth.task.claim.failed", message2, "Warning", null, "workbuddy-growth-one-click", id2, null, null, null, details);
			}
		}
	}

	private async Task<JsonArray> FetchGrowthTasksAsync(HttpClient client, OAuthCredential credential, bool miniProgram, CancellationToken cancellationToken)
	{
		return ((await SendAccountJsonAsync(client, credential, HttpMethod.Get, BaseFor(credential) + "/v2/activity/growth/tasks", null, cancellationToken, webClaim: false, miniProgram ? "miniprogram" : null)).Data?["tasks"] as JsonArray) ?? new JsonArray();
	}

	private async Task AcceptGrowthTasksAsync(HttpClient client, OAuthCredential credential, JsonArray tasks, bool miniProgram, CancellationToken cancellationToken)
	{
		string[] codes = (from item in (from item in tasks.OfType<JsonObject>()
				where ReadNodeBoolean(item, "locked") != true
				select item).Where((JsonObject item) =>
			{
				string text = ReadNodeString(item, "accept_status");
				return !(text == "accepted") && !(text == "completed") && !(text == "claimed");
			})
			select ReadNodeString(item, "task_code") into code
			where !string.IsNullOrWhiteSpace(code)
			select (code) into code
			where !miniProgram || (GrowthMiniTaskCodes.Contains(code) && !code.Equals("Sequential_Tasks_2", StringComparison.OrdinalIgnoreCase))
			select code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		if (codes.Length == 0)
		{
			return;
		}
		try
		{
			await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/v2/activity/growth/tasks/accept", new JsonObject { ["task_codes"] = new JsonArray(((IEnumerable<string>)codes).Select((Func<string, JsonNode>)((string code) => JsonValue.Create(code))).ToArray()) }, cancellationToken, webClaim: false, miniProgram ? "miniprogram" : null);
			await Task.Delay(TimeSpan.FromMilliseconds(1050L), cancellationToken);
		}
		catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
		{
			WorkBuddyTerminal workBuddyTerminal = this;
			string message = ex.Message;
			object details = new
			{
				miniProgram = miniProgram,
				count = codes.Length
			};
			await workBuddyTerminal.TryLogAsync("growth.tasks.accept.failed", message, "Warning", null, "workbuddy-growth-one-click", null, null, null, null, details);
		}
	}

	private async Task<bool> EnsureMiniTaskAcceptedAsync(HttpClient client, OAuthCredential credential, string taskCode, CancellationToken cancellationToken)
	{
		for (int attempt = 0; attempt < 2; attempt++)
		{
			try
			{
				await SendAccountJsonAsync(client, credential, HttpMethod.Post, BaseFor(credential) + "/v2/activity/growth/tasks/accept", new JsonObject { ["task_codes"] = new JsonArray((JsonNode)JsonValue.Create(taskCode)) }, cancellationToken, webClaim: false, "miniprogram");
				await Task.Delay(TimeSpan.FromSeconds(2L), cancellationToken);
				GrowthTaskSnapshot growthTaskSnapshot = await GetGrowthTaskAsync(client, credential, taskCode, miniProgram: true, cancellationToken);
				if ((object)growthTaskSnapshot != null && growthTaskSnapshot.AcceptStatus != null && !growthTaskSnapshot.AcceptStatus.Equals("not_accepted", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
			}
		}
		return false;
	}

	private async Task<GrowthTaskSnapshot?> WaitForGrowthTaskAsync(HttpClient client, OAuthCredential credential, string taskCode, bool miniProgram, CancellationToken cancellationToken)
	{
		GrowthTaskSnapshot current = await GetGrowthTaskAsync(client, credential, taskCode, miniProgram, cancellationToken);
		if ((object)current == null || current.IsClaimable || current.Claimed)
		{
			return current;
		}
		int attempts = (miniProgram ? 2 : 4);
		for (int attempt = 1; attempt < attempts; attempt++)
		{
			await Task.Delay(TimeSpan.FromSeconds(3L), cancellationToken);
			try
			{
				current = (await GetGrowthTaskAsync(client, credential, taskCode, miniProgram, cancellationToken)) ?? current;
				if (current.IsClaimable || current.Claimed)
				{
					return current;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch
			{
			}
		}
		return current;
	}

	private async Task<GrowthTaskSnapshot?> GetGrowthTaskAsync(HttpClient client, OAuthCredential credential, string taskCode, bool miniProgram, CancellationToken cancellationToken)
	{
		return FindGrowthTask(await FetchGrowthTasksAsync(client, credential, miniProgram, cancellationToken), taskCode);
	}

	private static GrowthTaskSnapshot? FindGrowthTask(JsonArray tasks, string taskCode)
	{
		return (from item in tasks.OfType<JsonObject>()
			where string.Equals(ReadNodeString(item, "task_code"), taskCode, StringComparison.OrdinalIgnoreCase)
			select item).Select(ReadGrowthTask).FirstOrDefault();
	}

	private static GrowthTaskSnapshot? ReadGrowthTask(JsonObject item)
	{
		string text = ReadNodeString(item, "task_code");
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}
		return new GrowthTaskSnapshot(text, ReadNodeString(item, "status"), ReadNodeString(item, "accept_status"), ReadNodeBoolean(item, "locked") == true, ReadTaskCount(item, "current"), ReadTaskCount(item, "target"));
	}

	private async Task ClaimGrowthTaskAsync(HttpClient client, OAuthCredential credential, string taskCode, bool miniProgram, CancellationToken cancellationToken)
	{
		if (miniProgram)
		{
			using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/activity/growth/tasks/" + Uri.EscapeDataString(taskCode) + "/claim"))
			{
				ApplyHeaders(request, credential, includeAuthorization: true, streaming: false, webClaim: false, "miniprogram");
				UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
				if (!upstreamResult.Success && upstreamResult.StatusCode == 400)
				{
					await SendAccountJsonAsync(client, credential, HttpMethod.Post, "https://www.workbuddy.cn/activity/growth/tasks/" + Uri.EscapeDataString(taskCode) + "/claim", null, cancellationToken, webClaim: true);
				}
				else if (!upstreamResult.Success)
				{
					throw new InvalidOperationException(upstreamResult.Message);
				}
				return;
			}
		}
		await SendAccountJsonAsync(client, credential, HttpMethod.Post, "https://www.workbuddy.cn/activity/growth/tasks/" + Uri.EscapeDataString(taskCode) + "/claim", null, cancellationToken, webClaim: true);
	}

	private async Task<string> ExecuteGrowthTaskActionAsync(HttpClient client, Account account, OAuthCredential credential, GrowthTaskSnapshot task, CancellationToken cancellationToken)
	{
		string code = task.TaskCode;
		switch (code)
		{
		case "chat_5":
			return await ReportChatTaskAsync(client, credential, task, 5L, "growth-chat5", cancellationToken);
		case "first_buddy":
			await RunTravelAsync(account, credential, cancellationToken);
			return "已执行 Buddy 领养前置与旅行检查";
		case "Model_chat_GLM5.2":
			await SendGrowthChatAndReadRequestIdAsync(client, credential, "glm-5.2", null, cancellationToken);
			await Task.Delay(TimeSpan.FromMilliseconds(1050L), cancellationToken);
			await SendActivityReportAsync(client, credential, "glm-5.2", "GLM-5.2", "growth-glm52", cancellationToken);
			return "已完成 GLM-5.2 对话并上报模型活跃事件";
		case "RichMeow_Chat":
			await ReportDesktopEventsAsync(client, credential, BuildDesktopChatSequence(NewGrowthId("rm"), NewGrowthId("rm-req"), NewGrowthId("rm-msg"), "fast-model", "fast-model"), cancellationToken);
			return "已上报桌面端成功对话事件链";
		case "Buddy_App":
		case "Buddy_App_QQ":
			await ReportDesktopEventsAsync(client, credential, BuildDesktopBuddyAppSequence(), cancellationToken);
			return "已上报 Buddy 应用进入事件链";
		case "automation_1":
		case "Sequential_Tasks_4":
			await ReportDesktopEventsAsync(client, credential, new JsonArray { GrowthEvent("automated_task_create_suc", ("name", "Router2API 自动化"), ("source", "manually"), ("modelId", "fast-model"), ("modelIsThinking", true), ("connectorCount", 0), ("skills", ""), ("skillCount", 0), ("scheduleType", "once"), ("mode", "LOCAL")) }, cancellationToken);
			return "已上报定时任务创建事件";
		case "Library_read":
			await ReportWebEventAsync(client, credential, "web_element_click", "https://www.workbuddy.cn/space/d/o0KWYeynteVv06UnAZqIFm", "library_doc_intro_click", "WorkBuddy资料库介绍", cancellationToken);
			return "已上报资料库介绍页面事件";
		case "template_5":
			return await RunTemplateTaskAsync(client, credential, task, cancellationToken);
		case "playbook_prompt":
			await ReportDesktopEventsAsync(client, credential, BuildDesktopPlaybookSequence(NewGrowthId("pb"), NewGrowthId("pb-req"), "pm-gtm-launch-plan", "新产品上市 GTM 发布计划一页纸"), cancellationToken);
			return "已上报灵感案例事件链";
		case "create_canvas":
			await ReportDesktopEventsAsync(client, credential, BuildDesktopCanvasSequence(NewGrowthId("canvas"), NewGrowthId("canvas-req")), cancellationToken);
			return "已上报设计画布事件链";
		case "expert_5":
			return await RunExpertTaskAsync(client, credential, task, "agent", 5L, cancellationToken);
		case "Expert_team_use_3":
			return await RunExpertTaskAsync(client, credential, task, "team", 3L, cancellationToken);
		case "Hp_Appearance":
			await ApplyAppearanceTaskAsync(client, credential, cancellationToken);
			return "已设置主题并上报皮肤生效事件";
		case "skill_1":
			await RunSkillTaskAsync(client, credential, cancellationToken);
			return "已执行对话并上报 skill_info 事件";
		case "Expert_lighthouse":
			await RunLighthouseTaskAsync(client, credential, cancellationToken);
			return "已执行轻量云专家对话及事件链";
		case "black_cat":
			await RunNightTasksAsync(account, credential, cancellationToken);
			return "已检查并执行夜间对话任务";
		case "school_season":
			return await RunMiniChatGrowthTaskAsync(client, credential, task, withActivityId: true, 1L, cancellationToken);
		case "Sequential_Tasks_1":
			return await RunMiniChatGrowthTaskAsync(client, credential, task, withActivityId: false, 1L, cancellationToken);
		case "Sequential_Tasks_3":
			return await RunMiniChatGrowthTaskAsync(client, credential, task, withActivityId: false, 5L, cancellationToken);
		case "Sequential_Tasks_6":
			return await RunMiniChatGrowthTaskAsync(client, credential, task, withActivityId: false, 10L, cancellationToken);
		case "Sequential_Tasks_2":
			return await RunMiniExpertTaskAsync(client, credential, task, cancellationToken);
		case "Sequential_Tasks_5":
			await ReportMiniEventsAsync(client, credential, new JsonArray { BuildMiniChatEvent(NewGrowthId("mp-glm"), "glm-5.2", "GLM-5.2") }, cancellationToken);
			await Task.Delay(TimeSpan.FromSeconds(3L), cancellationToken);
			if (!((await GetGrowthTaskAsync(client, credential, code, miniProgram: true, cancellationToken))?.IsClaimable ?? false))
			{
				await SendActivityReportAsync(client, credential, "glm-5.2", "GLM-5.2", "growth-mp-glm52", cancellationToken);
			}
			return "已上报小程序 GLM-5.2 模型事件";
		case "Sequential_Tasks_7":
			await ReportDesktopEventsAsync(client, credential, BuildDesktopPlaybookSequence(NewGrowthId("seq-pb"), NewGrowthId("seq-pb-req"), "pm-gtm-launch-plan", "新产品上市 GTM 发布计划一页纸"), cancellationToken);
			await Task.Delay(TimeSpan.FromSeconds(3L), cancellationToken);
			if (!((await GetGrowthTaskAsync(client, credential, code, miniProgram: true, cancellationToken))?.IsClaimable ?? false))
			{
				await ReportMiniEventsAsync(client, credential, BuildMiniPlaybookEvents("pm-gtm-launch-plan", "新产品上市 GTM 发布计划一页纸"), cancellationToken);
			}
			return "已尝试 PC 与小程序灵感事件判据";
		default:
			return "当前任务没有已知自动化动作";
		}
	}

	private async Task<string> ReportChatTaskAsync(HttpClient client, OAuthCredential credential, GrowthTaskSnapshot task, long defaultTarget, string conversationPrefix, CancellationToken cancellationToken)
	{
		long count = GrowthTaskNeed(task, defaultTarget, 10L);
		for (int index = 0; index < count; index++)
		{
			await SendActivityReportAsync(client, credential, "deepseek-v4-flash", "DeepSeek V4 Flash", $"{conversationPrefix}-{index}", cancellationToken);
			if (index + 1 < count)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(1050L), cancellationToken);
			}
		}
		return (count == 0L) ? "任务进度已达标" : $"已补齐 {count} 条对话活跃事件";
	}

	private async Task<string> RunMiniChatGrowthTaskAsync(HttpClient client, OAuthCredential credential, GrowthTaskSnapshot task, bool withActivityId, long defaultTarget, CancellationToken cancellationToken)
	{
		long count = GrowthTaskNeed(task, defaultTarget, 10L);
		for (int index = 0; index < count; index++)
		{
			JsonArray jsonArray = BuildMiniChatEvent(NewGrowthId("mp"), null, null);
			if (withActivityId)
			{
				jsonArray["activityId"] = "school_open_day_2026";
			}
			await ReportMiniEventsAsync(client, credential, new JsonArray { jsonArray }, cancellationToken);
			if (index + 1 < count)
			{
				await Task.Delay(TimeSpan.FromSeconds(2L), cancellationToken);
			}
		}
		return (count == 0L) ? "任务进度已达标" : $"已补齐 {count} 条小程序对话事件";
	}

	private async Task<string> RunTemplateTaskAsync(HttpClient client, OAuthCredential credential, GrowthTaskSnapshot task, CancellationToken cancellationToken)
	{
		(string Id, string Name)[] templates = new (string, string)[5]
		{
			("1", "深度研究"),
			("2", "周报生成"),
			("3", "竞品分析"),
			("4", "活动策划"),
			("5", "代码评审")
		};
		int count = (int)GrowthTaskNeed(task, 5L, templates.Length);
		for (int index = 0; index < count; index++)
		{
			(string, string) tuple = templates[index];
			await ReportDesktopEventsAsync(client, credential, BuildDesktopTemplateSequence(NewGrowthId("tpl"), NewGrowthId("tpl-req"), tuple.Item1, tuple.Item2), cancellationToken);
			if (index + 1 < count)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(300L), cancellationToken);
			}
		}
		return (count == 0) ? "模板任务进度已达标" : $"已上报 {count} 组模板事件";
	}

	private async Task<string> RunExpertTaskAsync(HttpClient client, OAuthCredential credential, GrowthTaskSnapshot task, string expertType, long defaultTarget, CancellationToken cancellationToken)
	{
		int count = (int)GrowthTaskNeed(task, defaultTarget, 5L);
		if (count == 0)
		{
			return "专家任务进度已达标";
		}
		List<GrowthExpert> list = await GetMarketExpertsAsync(client, credential, expertType, cancellationToken);
		if (list.Count == 0)
		{
			throw new InvalidOperationException("WorkBuddy 专家市场没有返回可用专家");
		}
		int completed = 0;
		foreach (GrowthExpert expert in list)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (completed >= count)
			{
				break;
			}
			if (!string.IsNullOrWhiteSpace(expert.Id))
			{
				try
				{
					await ReportDesktopEventsAsync(client, credential, BuildDesktopExpertSummonSequence(expert), cancellationToken);
					(string, string) tuple = await SendGrowthChatAndReadRequestIdAsync(client, credential, "fast-model", expert.Id, cancellationToken);
					string item = tuple.Item1;
					string item2 = tuple.Item2;
					string item3 = tuple.Item2;
					JsonArray jsonArray = BuildDesktopChatSequence(item, item2, "msg-" + item3.Substring(item3.Length - 8), "fast-model", "fast-model");
					jsonArray.Add(BuildDesktopExpertUseEvent(expert, tuple.Item1, tuple.Item2, local: false));
					await ReportDesktopEventsAsync(client, credential, jsonArray, cancellationToken);
					completed++;
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					throw;
				}
				catch (Exception ex2)
				{
					WorkBuddyTerminal workBuddyTerminal = this;
					string message = ex2.Message;
					string accountId = credential.AccountId;
					object details = new
					{
						expert = expert.Id,
						expertType = expertType
					};
					await workBuddyTerminal.TryLogAsync("growth.expert.action.failed", message, "Warning", null, "workbuddy-growth-one-click", accountId, null, null, null, details);
				}
				if (completed < count)
				{
					await Task.Delay(TimeSpan.FromSeconds(6L), cancellationToken);
				}
			}
		}
		return $"已完成 {completed}/{count} 组专家召唤与对话事件";
	}

	private async Task<string> RunMiniExpertTaskAsync(HttpClient client, OAuthCredential credential, GrowthTaskSnapshot task, CancellationToken cancellationToken)
	{
		GrowthExpert expert = (await GetMarketExpertsAsync(client, credential, null, cancellationToken)).FirstOrDefault((GrowthExpert growthExpert) => !string.IsNullOrWhiteSpace(growthExpert.Id));
		if ((object)expert == null)
		{
			return "专家市场暂无可用专家，跳过小程序任务";
		}
		bool flag = task.AcceptStatus == null || (!task.AcceptStatus.Equals("accepted", StringComparison.OrdinalIgnoreCase) && !task.AcceptStatus.Equals("completed", StringComparison.OrdinalIgnoreCase));
		if (flag)
		{
			flag = !(await EnsureMiniTaskAcceptedAsync(client, credential, task.TaskCode, cancellationToken));
		}
		if (flag)
		{
			return "小程序任务接受状态未确认，跳过本轮";
		}
		string item = (string.IsNullOrWhiteSpace(expert.Name) ? expert.Id : expert.Name);
		await ReportMiniEventsAsync(client, credential, new JsonArray { GrowthEvent("expert_actual_use", ("reportDelay", 0), ("extVersion", "2.2.8"), ("source", "mini_program"), ("id", expert.Id), ("name", expert.Id), ("expertTitle", item), ("type", "send_message"), ("characterCount", 12), ("expertType", string.IsNullOrWhiteSpace(expert.Type) ? "agent" : expert.Type)) }, cancellationToken);
		return "已上报小程序专家使用事件";
	}

	private async Task ApplyAppearanceTaskAsync(HttpClient client, OAuthCredential credential, CancellationToken cancellationToken)
	{
		JsonObject jsonObject = new JsonObject
		{
			["kind"] = "theme",
			["resource_key"] = "theme-tkmw7j"
		};
		using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/v2/user-asset/appearance/set")
		{
			Content = new StringContent(jsonObject.ToJsonString(), Encoding.UTF8, "application/json")
		})
		{
			ApplyHeaders(request, credential, includeAuthorization: true, streaming: false);
			request.Headers.Remove("User-Agent");
			request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1");
			request.Headers.Remove("X-Product");
			request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
			UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
			if (!upstreamResult.Success)
			{
				throw new InvalidOperationException(upstreamResult.Message);
			}
		}
		await Task.Delay(TimeSpan.FromSeconds(2L), cancellationToken);
		await ReportDesktopEventsAsync(client, credential, new JsonArray { GrowthEvent("appearance_skin_apply", ("action", "apply"), ("source", "settings_close"), ("id", "theme-tkmw7j"), ("vipLevel", 0), ("series", ""), ("type", "unknown")) }, cancellationToken);
	}

	private async Task RunSkillTaskAsync(HttpClient client, OAuthCredential credential, CancellationToken cancellationToken)
	{
		(string, string) tuple = await SendGrowthChatAndReadRequestIdAsync(client, credential, "fast-model", null, cancellationToken);
		string item = tuple.Item2;
		string text = "msg-" + item.Substring(item.Length - 8);
		JsonArray jsonArray = BuildDesktopChatSequence(tuple.Item1, tuple.Item2, text, "fast-model", "fast-model");
		foreach (JsonObject item2 in jsonArray.OfType<JsonObject>())
		{
			if (ReadNodeString(item2, "eventCode") == "chat_message_response")
			{
				item2["finishReason"] = "tool_calls";
			}
		}
		jsonArray.Add(GrowthEvent("skill_info", ("id", "润泽小馆·日报撰写"), ("skillId", "skill_2097350077599879168"), ("skillVersion", "1.0.0"), ("toolStatus", "success"), ("fileCount", 56), ("source", "workbuddy-desktop"), ("conversationId", tuple.Item1), ("requestId", tuple.Item2), ("messageId", text), ("requestModelId", "fast-model"), ("requestModelName", "fast-model"), ("traceId", tuple.Item2)));
		await ReportDesktopEventsAsync(client, credential, jsonArray, cancellationToken);
	}

	private async Task RunLighthouseTaskAsync(HttpClient client, OAuthCredential credential, CancellationToken cancellationToken)
	{
		GrowthExpert expert = new GrowthExpert("ex_2cvvUZQhDyeJ", "agent", "腾讯轻量云专家", "腾讯轻量云专家", "1.0.2", new JsonArray());
		try
		{
			expert = (await GetMarketExpertsAsync(client, credential, "agent", cancellationToken)).FirstOrDefault((GrowthExpert growthExpert) => growthExpert.Id == "ex_2cvvUZQhDyeJ") ?? expert;
		}
		catch
		{
		}
		await ReportDesktopEventsAsync(client, credential, BuildDesktopExpertSummonSequence(expert), cancellationToken);
		(string, string) tuple = await SendGrowthChatAndReadRequestIdAsync(client, credential, "fast-model", "ex_2cvvUZQhDyeJ", cancellationToken);
		string item = tuple.Item1;
		string item2 = tuple.Item2;
		string item3 = tuple.Item2;
		JsonArray jsonArray = BuildDesktopChatSequence(item, item2, "msg-" + item3.Substring(item3.Length - 8), "fast-model", "fast-model");
		if (jsonArray[0] is JsonObject jsonObject)
		{
			jsonObject["has_expert"] = true;
			jsonObject["expert_id"] = expert.Id;
			jsonObject["expert_name"] = expert.Name;
			jsonObject["expert_industry_id"] = "";
		}
		jsonArray.Add(BuildDesktopExpertUseEvent(expert, tuple.Item1, tuple.Item2, local: true));
		await ReportDesktopEventsAsync(client, credential, jsonArray, cancellationToken);
	}

	private async Task<(string ConversationId, string RequestId)> SendGrowthChatAndReadRequestIdAsync(HttpClient client, OAuthCredential credential, string model, string? expertId, CancellationToken cancellationToken)
	{
		string conversationId = NewGrowthId("conv");
		JsonObject jsonObject = new JsonObject { ["model"] = model };
		InlineArray2<JsonNode> buffer = default;
		buffer[0] = new JsonObject
		{
			["role"] = "system",
			["content"] = "You are a helpful assistant. 当前处于中文环境，使用简体中文回答。"
		};
		buffer[1] = new JsonObject
		{
			["role"] = "user",
			["content"] = "1+1等于几？直接回答。"
		};
		jsonObject["messages"] = new JsonArray(buffer);
		jsonObject["agent"] = "cli";
		jsonObject["temperature"] = 1;
		jsonObject["stream"] = true;
		jsonObject["stream_options"] = new JsonObject { ["include_usage"] = true };
		JsonObject jsonObject2 = jsonObject;
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/v2/chat/completions")
		{
			Content = new StringContent(jsonObject2.ToJsonString(), Encoding.UTF8, "application/json")
		};
		ApplyHeaders(request, credential, includeAuthorization: true, streaming: true);
		request.Headers.Remove("User-Agent");
		request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1");
		request.Headers.Remove("X-Domain");
		request.Headers.TryAddWithoutValidation("X-Domain", BaseFor(credential));
		request.Headers.Remove("X-Product");
		request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
		request.Headers.TryAddWithoutValidation("X-Conversation-ID", conversationId);
		request.Headers.TryAddWithoutValidation("X-Request-ID", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
		request.Headers.TryAddWithoutValidation("X-Agent-Intent", "craft");
		request.Headers.TryAddWithoutValidation("X-Agent-Type", "main");
		request.Headers.TryAddWithoutValidation("X-IDE-Version", "5.5.6");
		request.Headers.TryAddWithoutValidation("x-codebuddy-request", "1");
		if (!string.IsNullOrWhiteSpace(expertId))
		{
			request.Headers.TryAddWithoutValidation("X-Expert-Id", expertId);
		}
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		if (!response.IsSuccessStatusCode)
		{
			object arg = (int)response.StatusCode;
			throw new InvalidOperationException($"WorkBuddy 对话失败（HTTP {arg}）：{await response.Content.ReadAsStringAsync(cancellationToken)}");
		}
		string requestId = null;
		(string ConversationId, string RequestId) result;
		await using (Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken))
		{
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			while (true)
			{
				string text = await reader.ReadLineAsync(cancellationToken);
				if (text == null)
				{
					break;
				}
				string text2 = (text.StartsWith("data:", StringComparison.Ordinal) ? text.Substring(5).Trim() : text);
				if (text2.Length == 0 || text2 == "[DONE]")
				{
					continue;
				}
				try
				{
					string text3 = ReadNodeString(JsonNode.Parse(text2), "id");
					if (!string.IsNullOrWhiteSpace(text3) && Regex.IsMatch(text3, "^(cmb-)?[0-9a-fA-F]{32}$", RegexOptions.CultureInvariant) && requestId == null)
					{
						requestId = text3;
					}
				}
				catch (JsonException)
				{
				}
			}
			if (requestId == null)
			{
				throw new InvalidOperationException("WorkBuddy SSE 响应中没有可用于事件关联的 requestId");
			}
			result = (ConversationId: conversationId, RequestId: requestId);
		}
		return result;
	}

	private async Task<List<GrowthExpert>> GetMarketExpertsAsync(HttpClient client, OAuthCredential credential, string? expertType, CancellationToken cancellationToken)
	{
		JsonObject jsonObject = new JsonObject
		{
			["page"] = 1,
			["page_size"] = 20,
			["sort_by"] = "reco_rank",
			["sort_order"] = "desc"
		};
		if (!string.IsNullOrWhiteSpace(expertType))
		{
			jsonObject["expert_type"] = expertType;
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/portal/operation-platform/market/expert/list")
		{
			Content = new StringContent(jsonObject.ToJsonString(), Encoding.UTF8, "application/json")
		};
		ApplyHeaders(request, credential, includeAuthorization: true, streaming: false);
		request.Headers.Remove("User-Agent");
		request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1");
		request.Headers.Remove("X-Domain");
		request.Headers.TryAddWithoutValidation("X-Domain", BaseFor(credential));
		request.Headers.Remove("X-Product");
		request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		if (!upstreamResult.Success)
		{
			throw new InvalidOperationException(upstreamResult.Message);
		}
		if (!(upstreamResult.Data?["experts"] is JsonArray source))
		{
			return new List<GrowthExpert>();
		}
		return (from item in source.OfType<JsonObject>()
			select new GrowthExpert(ReadNodeString(item, "expert_id") ?? "", ReadNodeString(item, "expert_type") ?? "agent", ReadNodeString(item, "display_name_zh") ?? "", ReadNodeString(item, "profession_zh") ?? "", ReadNodeString(item, "version") ?? "1.0.0", (item["categories"] as JsonArray) ?? new JsonArray()) into item
			where !string.IsNullOrWhiteSpace(item.Id)
			select item).ToList();
	}

	private async Task ReportDesktopEventsAsync(HttpClient client, OAuthCredential credential, JsonArray events, CancellationToken cancellationToken)
	{
		if (events.Count == 0)
		{
			return;
		}
		JsonArray jsonArray = new JsonArray();
		foreach (JsonObject item in events.OfType<JsonObject>())
		{
			JsonObject jsonObject = BuildDesktopFingerprint(credential);
			foreach (KeyValuePair<string, JsonNode> item2 in item)
			{
				jsonObject[item2.Key] = item2.Value?.DeepClone();
			}
			jsonArray.Add(jsonObject);
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BaseFor(credential) + "/v2/report")
		{
			Content = new StringContent(jsonArray.ToJsonString(), Encoding.UTF8, "application/json")
		};
		request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json;charset=UTF-8");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		request.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
		request.Headers.TryAddWithoutValidation("User-Agent", "WorkBuddy/5.5.6 WorkBuddy/5.5.6 CLI/2.137.1");
		request.Headers.TryAddWithoutValidation("X-Domain", BaseFor(credential));
		request.Headers.TryAddWithoutValidation("X-Product", "SaaS");
		HttpRequestHeaders headers = request.Headers;
		string text = DeriveGrowthId(credential.AccountId ?? "", "req");
		string text2 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
		headers.TryAddWithoutValidation("X-Request-ID", text + text2.Substring(text2.Length - 6));
		if (!string.IsNullOrWhiteSpace(credential.AccountId))
		{
			request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
		}
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		if (!upstreamResult.Success)
		{
			throw new InvalidOperationException(upstreamResult.Message);
		}
	}

	private async Task ReportWebEventAsync(HttpClient client, OAuthCredential credential, string eventCode, string pageUrl, string elementId, string elementName, CancellationToken cancellationToken)
	{
		JsonObject jsonObject = GrowthEvent(eventCode, ("timestamp", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("reportDelay", 0), ("pageURL", pageUrl), ("elementId", elementId), ("elementName", elementName), ("os", "Win32"), ("arch", ""), ("osVersion", "10.0"), ("userAgent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36"), ("machineId", DeriveGrowthId(credential.AccountId ?? "", "webmachine")), ("userId", credential.AccountId ?? ""), ("userNickname", credential.Nickname ?? ""), ("enterpriseId", credential.EnterpriseId ?? ""));
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "https://www.workbuddy.cn/v2/report")
		{
			Content = new StringContent(new JsonArray((JsonNode)jsonObject).ToJsonString(), Encoding.UTF8, "application/json")
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		request.Headers.TryAddWithoutValidation("Accept", "application/json");
		request.Headers.TryAddWithoutValidation("x-client-platform", "web");
		request.Headers.TryAddWithoutValidation("Origin", "https://www.workbuddy.cn");
		request.Headers.TryAddWithoutValidation("Referer", pageUrl);
		request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36");
		if (!string.IsNullOrWhiteSpace(credential.AccountId))
		{
			request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
		}
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		if (!upstreamResult.Success)
		{
			throw new InvalidOperationException(upstreamResult.Message);
		}
	}

	private async Task ReportMiniEventsAsync(HttpClient client, OAuthCredential credential, JsonArray events, CancellationToken cancellationToken)
	{
		JsonArray jsonArray = new JsonArray();
		foreach (JsonObject item in events.OfType<JsonObject>())
		{
			JsonObject jsonObject = new JsonObject
			{
				["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				["ideType"] = "WorkBuddy_MP",
				["ideVersion"] = "2.4.0",
				["extName"] = "workbuddy-mp",
				["extVersion"] = "2.4.0",
				["product"] = "SaaS",
				["ideName"] = "wx_app_cloud",
				["platform"] = "mini_program",
				["os"] = "windows",
				["osVersion"] = "11",
				["arch"] = "x64",
				["machineId"] = "0655736a-607f-4d9d-b430-58176ee9a090",
				["timezone"] = "Asia/Shanghai",
				["userId"] = credential.AccountId ?? "",
				["userNickname"] = credential.Nickname ?? ""
			};
			foreach (KeyValuePair<string, JsonNode> item2 in item)
			{
				jsonObject[item2.Key] = item2.Value?.DeepClone();
			}
			jsonArray.Add(jsonObject);
		}
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, BillingBaseFor(credential) + "/v2/report")
		{
			Content = new StringContent(jsonArray.ToJsonString(), Encoding.UTF8, "application/json")
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		request.Headers.TryAddWithoutValidation("Accept", "application/json");
		if (!string.IsNullOrWhiteSpace(credential.AccountId))
		{
			request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
		}
		request.Headers.TryAddWithoutValidation("X-Client-Product", "workbuddy-mp");
		request.Headers.TryAddWithoutValidation("X-Client-Version", "2.4.0");
		request.Headers.TryAddWithoutValidation("X-Client-Platform", "mp-weixin");
		request.Headers.TryAddWithoutValidation("X-Platform", "wechatmp");
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		if (!upstreamResult.Success)
		{
			throw new InvalidOperationException(upstreamResult.Message);
		}
	}

	private static JsonObject BuildDesktopFingerprint(OAuthCredential credential)
	{
		long num = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		return new JsonObject
		{
			["timezone"] = "Asia/Shanghai",
			["reportDelay"] = 2000,
			["userId"] = credential.AccountId ?? "",
			["username"] = credential.Nickname ?? "",
			["userNickname"] = credential.Nickname ?? "",
			["product"] = "SaaS",
			["releaseDate"] = 1789036585355L,
			["commit"] = "5f9692923c93033111c51ad7b003eb80204a9b75",
			["ideName"] = "WorkBuddy",
			["ideType"] = "WorkBuddy",
			["ideVersion"] = "5.5.6",
			["machineId"] = DeriveGrowthId(credential.AccountId ?? "", "machine"),
			["sessionId"] = DeriveGrowthId(credential.AccountId ?? "", "session"),
			["extName"] = "workbuddy-desktop",
			["extVersion"] = "5.5.6",
			["os"] = "win32",
			["arch"] = "x64",
			["osVersion"] = "10.0.26220",
			["cpuCores"] = 20,
			["memorySize"] = 24,
			["timestamp"] = num,
			["presentAt"] = num
		};
	}

	private static JsonArray BuildDesktopChatSequence(string conversationId, string requestId, string messageId, string modelId, string modelName)
	{
		JsonArray jsonArray = new JsonArray();
		jsonArray.Add(GrowthEvent("agent_task_created", ("source", "LOCAL"), ("name", "working"), ("task_target", "local"), ("mode", "craft"), ("requestModelId", modelId), ("requestModelName", modelName), ("has_repo", false), ("repo_type", "none"), ("workspace_type", "empty"), ("has_connector", false), ("connector_types", Array.Empty<string>()), ("has_mention", false), ("mention_types", Array.Empty<string>()), ("has_template", false), ("action", ""), ("template_name", ""), ("has_expert", false), ("expert_id", ""), ("expert_name", ""), ("expert_industry_id", ""), ("has_skill", false), ("skill_names", Array.Empty<string>()), ("conversationId", conversationId), ("messageId", messageId), ("buddyId", ""), ("buddyName", "")));
		jsonArray.Add(GrowthEvent("chat_message_send", ("messageId", messageId + "-assistant"), ("historyCount", 0), ("isContextTruncated", false), ("currentStepCount", 1), ("traceId", requestId), ("rootRequestId", requestId), ("parentConversationId", conversationId), ("agentName", "cli"), ("agentType", "main")));
		jsonArray.Add(GrowthEvent("chat_request_send", ("inputLength", 24), ("isPlan", false), ("isAutoExecuteTerminal", false), ("isAutoModify", false), ("codebaseEnable", false), ("maxToken", 0), ("maxSteps", 500), ("temperature", 0), ("maxRetries", 0), ("mentionContexts", Array.Empty<string>()), ("knowledgeId", Array.Empty<string>()), ("knowledgeName", Array.Empty<string>()), ("codebaseId", ""), ("mentionContextCount", 0), ("command", ""), ("recommendId", ""), ("skillId", ""), ("skillCount", 0), ("totalCount", 0), ("traceId", requestId), ("rootRequestId", requestId), ("parentConversationId", conversationId), ("agentName", "cli"), ("agentType", "main"), ("codebuddy.session_id", conversationId), ("codebuddy.conversation_request_id", requestId)));
		jsonArray.Add(GrowthEvent("chat_message_response", ("messageId", messageId + "-assistant"), ("responseModelId", modelId), ("inputToken", 120), ("outputToken", 80), ("totalToken", 200), ("cachedTokens", 0), ("cachedWriteTokens", 0), ("cachedMissTokens", 0), ("isSuccessful", true), ("messageErrorCode", ""), ("finishReason", "stop"), ("firstTokenAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("traceId", requestId), ("conversationId", conversationId), ("rootRequestId", requestId), ("parentConversationId", conversationId), ("agentName", "cli"), ("agentType", "main"), ("codebuddy.session_id", conversationId), ("codebuddy.conversation_request_id", requestId)));
		jsonArray.Add(GrowthEvent("chat_message_status", ("messageId", messageId + "-assistant"), ("messageErrorCode", "0"), ("traceId", requestId), ("rootRequestId", requestId), ("parentConversationId", conversationId), ("agentName", "cli"), ("agentType", "main")));
		jsonArray.Add(GrowthEvent("chat_request_response", ("mode", "craft"), ("toolCallCount", 0), ("inputToken", 120), ("outputToken", 80), ("totalToken", 200), ("cachedTokens", 0), ("cachedWriteTokens", 0), ("cachedMissTokens", 0), ("isSuccessful", true), ("messageErrorCode", ""), ("finishReason", "stop"), ("rootRequestId", requestId), ("parentConversationId", conversationId)));
		return jsonArray;
	}

	private static JsonArray BuildDesktopBuddyAppSequence()
	{
		JsonArray jsonArray = new JsonArray();
		jsonArray.Add(Event("buddyapp_discover_click", Array.Empty<(string, object)>()));
		jsonArray.Add(Event("buddyapp_show", new (string, object)[3]
		{
			("elementId", "cb_y5Dy46tPQGGWtueMxXbe"),
			("elementName", "企鹅教师助手"),
			("position", 2)
		}));
		jsonArray.Add(Event("buddyapp_enter_click", new (string, object)[4]
		{
			("elementId", "cb_y5Dy46tPQGGWtueMxXbe"),
			("elementName", "企鹅教师助手"),
			("position", 2),
			("isFirstPage", "1")
		}));
		jsonArray.Add(Event("buddyapp_auth_confirm_click", new (string, object)[2]
		{
			("elementId", "cb_y5Dy46tPQGGWtueMxXbe"),
			("elementName", "企鹅教师助手")
		}));
		jsonArray.Add(Event("buddyapp_bindaccount_skip_click", new (string, object)[2]
		{
			("elementId", "cb_y5Dy46tPQGGWtueMxXbe"),
			("elementName", "企鹅教师助手")
		}));
		return jsonArray;
		static JsonObject Event(string code, params (string Key, object? Value)[] fields)
		{
			(string, object)[] first = new (string, object)[3]
			{
				("mode", "LOCAL"),
				("buddyId", "cb_y5Dy46tPQGGWtueMxXbe"),
				("buddyName", "企鹅教师助手")
			};
			return GrowthEvent(code, first.Concat(fields).ToArray());
		}
	}

	private static JsonArray BuildDesktopTemplateSequence(string conversationId, string requestId, string templateId, string templateName)
	{
		JsonArray jsonArray = BuildDesktopChatSequence(conversationId, requestId, "msg-" + templateId, "fast-model", "fast-model");
		jsonArray.Add(GrowthEvent("agent_task_created_with_template", ("mode", "working"), ("isCustomModel", false), ("id", templateId), ("name", templateName), ("requestId", requestId)));
		jsonArray.Add(GrowthEvent("template_used", ("template_id", templateId), ("task_mode", "working")));
		return jsonArray;
	}

	private static JsonArray BuildDesktopPlaybookSequence(string conversationId, string requestId, string caseId, string caseName)
	{
		JsonArray jsonArray = BuildDesktopChatSequence(conversationId, requestId, "msg-pb", "fast-model", "fast-model");
		(string, object)[] first = new (string, object)[5]
		{
			("id", caseId),
			("name", caseName),
			("type", "document"),
			("categoryId", ""),
			("categoryName", "")
		};
		jsonArray.Add(GrowthEvent("web_element_click", ("pageName", "playbook_detail"), ("elementId", "playbook_ctaClick"), ("elementName", caseName), ("source", "discover")));
		jsonArray.Add(GrowthEvent("playbook_cta_click", first.Concat(new _003C_003Ez__ReadOnlyArray<(string, object)>(new (string, object)[2]
		{
			("source", "discover"),
			("position", 0)
		})).ToArray()));
		jsonArray.Add(GrowthEvent("playbook_prompt_send", first.Concat(new _003C_003Ez__ReadOnlyArray<(string, object)>(new (string, object)[2]
		{
			("conversationId", conversationId),
			("requestId", requestId)
		})).ToArray()));
		return jsonArray;
	}

	private static JsonArray BuildDesktopCanvasSequence(string conversationId, string requestId)
	{
		JsonArray jsonArray = BuildDesktopChatSequence(conversationId, requestId, "msg-canvas", "fast-model", "fast-model");
		jsonArray.Add(GrowthEvent("wbx_design_canvas_task_create", ("conversationId", conversationId), ("requestId", requestId), ("source", "summon_keyword"), ("cost", 12000), ("isSuccessful", true)));
		(string, object)[] array = new (string, object)[7]
		{
			("conversationId", conversationId),
			("requestId", requestId),
			default,
			default,
			default,
			default,
			default
		};
		array[2] = ("id", "ardot-file-" + requestId.Substring(requestId.Length - 8));
		array[3] = ("source", "summon_keyword");
		array[4] = ("type", "page");
		array[5] = ("cost", 13000);
		array[6] = ("isSuccessful", true);
		jsonArray.Add(GrowthEvent("wbx_design_canvas_open", array));
		return jsonArray;
	}

	private static JsonArray BuildDesktopExpertSummonSequence(GrowthExpert expert)
	{
		string item = ((expert.Categories.FirstOrDefault() is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value)) ? value : "expert-all");
		string item2 = (string.IsNullOrWhiteSpace(expert.Version) ? "1.0.0" : expert.Version);
		JsonArray jsonArray = new JsonArray();
		jsonArray.Add(GrowthEvent("web_element_click", ("source", expert.Id), ("type", item), ("version", item2), ("elementId", "expert_summon_click"), ("elementName", "立即召唤"), ("pageURL", "/C:/Program%20Files/WorkBuddy/resources/app.asar/renderer/index.html")));
		jsonArray.Add(GrowthEvent("expert_summon_click", ("id", expert.Id), ("name", expert.Name), ("expertTitle", expert.Profession), ("type", "expert-all"), ("position", 0), ("expertType", expert.Type), ("version", item2), ("mode", "LOCAL")));
		jsonArray.Add(GrowthEvent("expert_summoned", ("id", expert.Id), ("name", expert.Name), ("expertTitle", expert.Profession), ("type", "expert-all")));
		return jsonArray;
	}

	private static JsonObject BuildDesktopExpertUseEvent(GrowthExpert expert, string conversationId, string requestId, bool local)
	{
		string item = ((expert.Categories.FirstOrDefault() is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value)) ? value : "expert-all");
		(string, object)[] array = new (string, object)[15]
		{
			("id", expert.Id),
			("name", expert.Name),
			("expertTitle", expert.Profession),
			("type", item),
			("expertType", expert.Type),
			("source", "builtin"),
			("version", string.IsNullOrWhiteSpace(expert.Version) ? "1.0.0" : expert.Version),
			("cost", 9000),
			("characterCount", 14),
			("conversationId", conversationId),
			("requestId", requestId),
			default,
			default,
			default,
			default
		};
		array[11] = ("messageId", "msg-" + requestId.Substring(requestId.Length - 8));
		array[12] = ("requestModelId", "fast-model");
		array[13] = ("requestModelName", "fast-model");
		array[14] = ("mode", local ? "LOCAL" : "craft");
		JsonObject jsonObject = GrowthEvent("expert_actual_use", array);
		if (local)
		{
			jsonObject["type"] = "";
			jsonObject["cost"] = 0;
		}
		return jsonObject;
	}

	private static JsonArray BuildMiniChatEvent(string conversationId, string? modelId, string? modelName)
	{
		string text = NewGrowthId("mp-req");
		(string, object)[] array = new (string, object)[28]
		{
			("inputLength", 14),
			("isPlan", false),
			("isAutoExecuteTerminal", false),
			("isAutoModify", false),
			("codebaseEnable", false),
			("maxToken", 0),
			("maxSteps", 500),
			("temperature", 0),
			("maxRetries", 0),
			("mentionContexts", Array.Empty<string>()),
			("knowledgeId", Array.Empty<string>()),
			("knowledgeName", Array.Empty<string>()),
			("codebaseId", ""),
			("mentionContextCount", 0),
			("command", ""),
			("recommendId", ""),
			("skillId", ""),
			("skillCount", 0),
			("totalCount", 0),
			("traceId", text),
			("rootRequestId", text),
			("parentConversationId", conversationId),
			("conversationId", conversationId),
			default,
			default,
			default,
			default,
			default
		};
		array[23] = ("messageId", "msg-" + text.Substring(text.Length - 8));
		array[24] = ("agentName", "mp");
		array[25] = ("agentType", "main");
		array[26] = ("codebuddy.session_id", conversationId);
		array[27] = ("codebuddy.conversation_request_id", text);
		JsonObject jsonObject = GrowthEvent("chat_request_send", array);
		if (!string.IsNullOrWhiteSpace(modelId))
		{
			jsonObject["requestModelId"] = modelId;
		}
		if (!string.IsNullOrWhiteSpace(modelName))
		{
			jsonObject["requestModelName"] = modelName;
		}
		return new JsonArray { jsonObject };
	}

	private static JsonArray BuildMiniPlaybookEvents(string caseId, string caseName)
	{
		JsonObject value = GrowthEvent("playbook_cta_click", ("source", "discover"), ("position", 1), ("extVersion", "2.2.8"), ("id", caseId), ("name", caseName), ("type", "document"), ("categoryId", ""), ("categoryName", ""), ("skills", ""), ("skillNames", ""));
		JsonObject value2 = GrowthEvent("playbook_prompt_send", ("source", "discover"), ("promptLength", 96), ("isOfficial", 1), ("conversationId", NewGrowthId("mp-pb")), ("extVersion", "2.2.8"), ("id", caseId), ("name", caseName), ("type", "document"), ("categoryId", ""), ("categoryName", ""), ("skills", ""), ("skillNames", ""));
		return new JsonArray { value, value2 };
	}

	private static JsonObject GrowthEvent(string code, params (string Key, object? Value)[] fields)
	{
		JsonObject jsonObject = new JsonObject { ["eventCode"] = code };
		for (int i = 0; i < fields.Length; i++)
		{
			(string Key, object? Value) tuple = fields[i];
			string item = tuple.Key;
			object item2 = tuple.Value;
			jsonObject[item] = ((item2 is JsonNode jsonNode) ? jsonNode.DeepClone() : JsonSerializer.SerializeToNode(item2));
		}
		return jsonObject;
	}

	private static long GrowthTaskNeed(GrowthTaskSnapshot task, long defaultTarget, long maximum)
	{
		return Math.Clamp(((task.Target > 0) ? task.Target : defaultTarget) - task.Current, 0L, maximum);
	}

	private static string NewGrowthId(string prefix)
	{
		return $"wb2api-{prefix}-{Guid.NewGuid():N}";
	}

	private static string DeriveGrowthId(string accountId, string salt)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + accountId))[..18]).ToLowerInvariant();
	}

	/// <summary>
	/// 创建 WorkBuddy 管理页面，提供 OAuth 授权、账号/积分查看、模型刷新和任务快捷操作。
	/// </summary>
	public PluginMainPage GetMainPage()
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected Obj, but got Unknown
		return new PluginMainPage("WorkBuddy 账号", "<!doctype html>\r\n<html lang=\"zh-CN\">\r\n<head>\r\n  <meta charset=\"utf-8\">\r\n  <meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">\r\n  <style>\r\n    :root{--green:#15803d;--green-dark:#14532d;--green-soft:#f0fdf4;--ink:#17221b;--muted:#64748b;--line:#dbe7df;--danger:#b91c1c;--stripe:#86ce91;--stripe-line:#60ad6c}\r\n    *{box-sizing:border-box}html,body{margin:0;min-width:0;width:100%;background:transparent;color:var(--ink);font:14px/1.55 \"Microsoft YaHei\",system-ui,sans-serif}\r\n    main{display:grid;gap:16px;width:100%;min-width:0;padding:2px 0 24px}\r\n    .intro{display:flex;justify-content:space-between;align-items:flex-start;gap:16px}.eyebrow{margin:0 0 4px;color:var(--green);font-size:11px;font-weight:700;letter-spacing:.12em;text-transform:uppercase}\r\n    .intro h1{margin:0;font-size:24px;letter-spacing:-.03em}.intro p{margin:6px 0 0;color:var(--muted);font-size:13px}\r\n    .badge{display:inline-flex;align-items:center;gap:7px;padding:6px 10px;border:1px solid var(--line);border-radius:999px;color:var(--green-dark);background:var(--green-soft);font-size:12px;white-space:nowrap}\r\n    .badge i{width:6px;height:6px;border-radius:50%;background:var(--green)}\r\n    .panel{min-width:0;border:1px solid var(--line);border-radius:14px;background:#fff;padding:16px}\r\n    .panel-head{display:flex;justify-content:space-between;align-items:center;gap:10px;margin-bottom:12px}.panel-head h2{margin:0;font-size:15px}.panel-head small{color:var(--muted);font-size:12px}\r\n    .toolbar{display:flex;flex-wrap:wrap;align-items:center;gap:10px}.toolbar select{flex:1;min-width:0;max-width:360px;padding:0 11px;border:1px solid var(--line);background:#fff;color:var(--ink)}\r\n    button,.button-link,.toolbar select{min-height:44px;border-radius:9px;font:inherit}\r\n    button,.button-link{display:inline-flex;align-items:center;justify-content:center;border:1px solid transparent;padding:8px 14px;background:var(--green);color:#fff;font-weight:600;text-decoration:none;cursor:pointer;transition:background .15s}\r\n    button:hover,.button-link:hover{background:var(--green-dark)}button.secondary,.button-link.secondary{border-color:var(--line);background:#fff;color:var(--green-dark)}\r\n    button.secondary:hover,.button-link.secondary:hover{background:var(--green-soft)}button:disabled{opacity:.55;cursor:not-allowed}.panel-head button{flex:none;font-size:12px}\r\n    .login-link{display:none}.hint{margin:10px 0 0;color:var(--muted);font-size:12px;overflow-wrap:anywhere}\r\n    .notice{display:none;margin-top:10px;padding:10px 12px;border-radius:9px;background:var(--green-soft);color:var(--green-dark);font-size:12px;overflow-wrap:anywhere}.notice.error{background:#fef2f2;color:var(--danger)}\r\n    .account-list,.model-list,.task-list{display:grid;gap:14px;min-width:0}.catalog-group{min-width:0}.catalog-group h3{margin:0 0 9px;font-size:12px;font-weight:500;color:var(--muted)}\r\n    .catalog-items{display:grid;gap:10px}.account-card{min-width:0;padding:14px 16px;border:1px solid var(--line);border-radius:11px;background:#fff}\r\n    .account-heading{display:flex;align-items:center;justify-content:space-between;gap:12px}.account-name{min-width:0;margin:0;font-size:15px;font-weight:650;overflow-wrap:anywhere}\r\n    .account-state{flex:none;padding:2px 8px;border-radius:999px;background:var(--green-soft);color:var(--green-dark);font-size:11px}.account-state.inactive{background:#f1f5f9;color:var(--muted)}\r\n    .account-validity{margin:5px 0 12px;color:var(--muted);font-size:12px;overflow-wrap:anywhere}.account-validity time{color:var(--ink);font-variant-numeric:tabular-nums}\r\n    .credit-summary{display:flex;align-items:baseline;justify-content:space-between;gap:12px}.credit-summary>div{min-width:0}.credit-label{margin-right:7px;font-size:12px;color:var(--muted)}\r\n    .credit-balance{font-size:20px;font-weight:650;letter-spacing:-.02em;font-variant-numeric:tabular-nums}.credit-used{font-size:13px;font-weight:600;font-variant-numeric:tabular-nums}\r\n    .credit-bar{display:flex;align-items:center;width:100%;height:34px;min-width:0}\r\n    .credit-segment{position:relative;flex:none;min-width:0;min-height:34px;height:34px;padding:0;border:0;border-radius:0;background:transparent}\r\n    .credit-segment::before{content:\"\";position:absolute;inset:13px min(4px,35%) 13px 0;border:1px solid var(--stripe-line);background:var(--stripe)}\r\n    .credit-segment:first-child::before{border-radius:3px 0 0 3px}.credit-segment:last-child::before{right:0;border-radius:0 3px 3px 0}.credit-segment:only-child::before{border-radius:3px}\r\n    .credit-segment:hover,.credit-segment:focus-visible{background:transparent}.credit-segment:hover::before,.credit-segment:focus-visible::before{background:var(--green)}\r\n    .credit-bar-empty{width:100%;height:8px;border-radius:4px;background:#edf2ee}\r\n    .account-footer{display:flex;flex-wrap:wrap;justify-content:space-between;align-items:center;gap:4px 12px;color:var(--muted);font-size:11px}.credits-warning{margin:8px 0 0;color:var(--danger);font-size:12px}\r\n    .credit-details{margin-top:6px}.credit-details summary{display:flex;align-items:center;gap:7px;min-height:44px;width:fit-content;max-width:100%;color:var(--green-dark);font-size:12px;cursor:pointer;list-style:none}\r\n    .credit-details summary::-webkit-details-marker{display:none}.credit-details summary::before{content:\"\";width:6px;height:6px;border-right:1.5px solid currentColor;border-bottom:1.5px solid currentColor;transform:rotate(-45deg)}\r\n    .credit-details[open] summary::before{transform:rotate(45deg)}.credit-detail-list{display:grid;gap:0;margin:0;padding:0;list-style:none}\r\n    .credit-detail{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px 12px;padding:12px 0;border-top:1px solid var(--line);font-size:12px}.credit-detail strong{font-weight:500;overflow-wrap:anywhere}.credit-detail-amount{font-variant-numeric:tabular-nums;white-space:nowrap}.credit-detail small{grid-column:1/-1;color:var(--muted);font-size:11px;overflow-wrap:anywhere}\r\n    .credit-tooltip{position:fixed;z-index:100;max-width:min(340px,calc(100vw - 24px));padding:10px 12px;border-radius:8px;background:#17221b;color:#fff;box-shadow:0 5px 18px #0003;font-size:12px;line-height:1.7;white-space:pre-line;overflow-wrap:anywhere;pointer-events:none}\r\n    .credit-tooltip[hidden]{display:none}\r\n    .model-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:9px}.model{min-width:0;padding:13px;border:1px solid var(--line);border-radius:10px;background:var(--green-soft)}\r\n    .model strong{display:block;font-size:13px;overflow-wrap:anywhere}.model code{display:block;margin-top:4px;color:var(--green-dark);overflow-wrap:anywhere;font-size:11px}.model span{display:block;margin-top:9px;color:var(--muted);font-size:12px;line-height:1.8}\r\n    .task-list{grid-template-columns:repeat(3,minmax(0,1fr));gap:8px}.task-list button{min-width:0;min-height:46px;background:#fff;border-color:var(--line);color:var(--green-dark);font-size:12px}.task-list button:hover{background:var(--green-soft)}\r\n    .empty{padding:18px 0;color:var(--muted);text-align:center;font-size:12px}\r\n    :focus-visible{outline:2px solid var(--green);outline-offset:3px}\r\n    @media(max-width:800px){.model-grid{grid-template-columns:repeat(2,minmax(0,1fr))}.task-list{grid-template-columns:repeat(2,minmax(0,1fr))}}\r\n    @media(max-width:520px){main{gap:12px}.intro{flex-wrap:wrap;gap:10px}.intro h1{font-size:21px}.intro p{font-size:12px}.badge{padding:4px 8px}\r\n      .panel{padding:12px;border-radius:12px}.panel-head{flex-wrap:wrap;gap:8px;margin-bottom:10px}.panel-head h2{font-size:14px}.panel-head button{padding:7px 10px}\r\n      .toolbar{display:grid;grid-template-columns:minmax(0,1fr);gap:8px}.toolbar>*{width:100%;max-width:none}.toolbar select{max-width:none;font-size:16px}\r\n      .account-card{padding:12px}.account-heading{gap:8px}.account-name{font-size:14px}.account-validity{margin-bottom:10px}\r\n      .credit-summary{align-items:flex-start;gap:8px}.credit-label{display:block;margin:0 0 3px}.credit-balance{font-size:22px}.credit-summary>div:last-child{text-align:right}.credit-used{display:block;padding-top:4px}\r\n      .credit-bar{height:44px}.credit-segment{height:44px;min-height:44px}.credit-segment::before{top:18px;bottom:18px}\r\n      .model-grid{grid-template-columns:1fr}.task-list button{padding:9px 7px}.credit-details{margin-top:2px}}\r\n    @media(max-width:300px){.credit-summary{flex-wrap:wrap}.credit-summary>div:last-child{text-align:left}.task-list{grid-template-columns:1fr}.panel-head button{width:100%}}\r\n    @media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important;transition:none!important;animation-duration:.01ms!important}}\r\n  </style>\r\n</head>\r\n<body>\r\n  <main>\r\n    <section class=\"intro\"><div><p class=\"eyebrow\">WorkBuddy connector</p><h1>WorkBuddy 账号与任务</h1><p>管理 OAuth 账号、积分包与自动任务。</p></div><span class=\"badge\"><i></i><span id=\"health\">插件已就绪</span></span></section>\r\n    <section class=\"panel\">\r\n      <div class=\"panel-head\"><h2>OAuth 一键登录</h2><small id=\"login-state\">未开始</small></div>\r\n      <div class=\"toolbar\"><select id=\"realm\" aria-label=\"登录站点\"><option value=\"cn\">国内 WorkBuddy / CodeBuddy</option><option value=\"global\">国际 WorkBuddy AI</option></select><button id=\"login\">打开授权页面</button><a id=\"login-link\" class=\"button-link secondary login-link\" target=\"_blank\" rel=\"noopener noreferrer\">重新打开授权页面</a></div>\r\n      <p class=\"hint\">在官方页面完成登录后，保持此页面打开，账号将自动保存。</p><div id=\"notice\" class=\"notice\" role=\"status\"></div>\r\n    </section>\r\n    <section class=\"panel\"><div class=\"panel-head\"><h2>已保存账号</h2><button id=\"reload-accounts\" class=\"secondary\">刷新账号与积分</button></div><div id=\"accounts\" class=\"account-list\"><div class=\"empty\">加载中…</div></div></section>\r\n    <section class=\"panel\"><div class=\"panel-head\"><h2>模型目录</h2><button id=\"refresh-models\" class=\"secondary\">刷新模型缓存</button></div><div id=\"models\" class=\"model-list\"><div class=\"empty\">加载中…</div></div></section>\r\n    <section class=\"panel\"><div class=\"panel-head\"><h2>定时任务</h2><small>后台执行 · 结果写入任务日志</small></div><div class=\"task-list\"><button data-task=\"workbuddy-token-refresh\">刷新 OAuth 令牌</button><button data-task=\"workbuddy-balance-refresh\">刷新账号积分</button><button data-task=\"workbuddy-daily-checkin\">立即签到</button><button data-task=\"workbuddy-activity\">活跃上报</button><button data-task=\"workbuddy-travel\">猫猫旅行</button><button id=\"growth-auto\">成长任务一键完成</button><button data-task=\"workbuddy-night-tasks\">夜间任务</button></div></section>\r\n  </main>\r\n  <div id=\"credit-tooltip\" class=\"credit-tooltip\" role=\"tooltip\" hidden></div>\r\n  <script>\r\n    const $=id=>document.getElementById(id), sleep=ms=>new Promise(r=>setTimeout(r,ms));\r\n    async function api(method,route,body){return await window.Router2API.request(method,route,body)}\r\n    function notice(message,error=false){const el=$('notice');el.textContent=message;el.className='notice'+(error?' error':'');el.style.display='block'}\r\n    function setBusy(button,busy,label){button.disabled=busy;if(busy){button.dataset.label=button.textContent;button.textContent=label}else if(button.dataset.label)button.textContent=button.dataset.label}\r\n    function escapeHtml(value){return String(value??'').replace(/[&<>'\"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',\"'\":'&#39;','\"':'&quot;'}[c]))}\r\n    function displayName(value){\r\n      const name=String(value??'').trim(), compact=name.replace(/[\\s()-]/g,'');\r\n      const phone=/^(?:\\+?86)?1[3-9*][\\d*]{9}$/.test(compact)?compact.slice(-11):/^\\+[\\d*]{8,15}$/.test(compact)?compact:null;\r\n      return phone?phone.slice(0,1)+'*'.repeat(phone.length-2)+phone.slice(-1):name||'WorkBuddy 账号';\r\n    }\r\n    function parseDate(value){if(!value)return null;const date=new Date(value);return Number.isNaN(date.getTime())?null:date}\r\n    function formatDate(value){const date=parseDate(value);if(!date)return '未提供到期时间';const pad=n=>String(n).padStart(2,'0');return date.getFullYear()+'-'+pad(date.getMonth()+1)+'-'+pad(date.getDate())+' '+pad(date.getHours())+':'+pad(date.getMinutes())}\r\n    function formatCredits(value){return value!=null&&Number.isFinite(Number(value))?Number(value).toLocaleString('zh-CN',{minimumFractionDigits:2,maximumFractionDigits:2}):'—'}\r\n    function expiryText(value){\r\n      const date=parseDate(value);if(!date)return '未提供到期时间';\r\n      const remaining=date.getTime()-Date.now(), days=Math.ceil(remaining/86400000);\r\n      return '到期时间 '+formatDate(value)+'（'+(remaining<=0?'已到期':remaining<86400000?'不足 1 天':'剩余 '+days+' 天')+'）';\r\n    }\r\n    function packageDescription(item){return (item.name||'未命名积分包')+'\\n'+formatCredits(item.remain)+' 积分\\n'+expiryText(item.expiresAt)}\r\n    function renderCreditPackages(account){\r\n      const packages=Array.isArray(account.creditPackageDetails)?account.creditPackageDetails:[], available=packages.filter(p=>Number.isFinite(Number(p.remain))&&Number(p.remain)>0);\r\n      const total=available.reduce((sum,p)=>sum+Number(p.remain),0);\r\n      // 宽度严格按可用积分占比计算，不设等宽或最小宽度；小包也可通过下方明细查看。\r\n      const segments=total>0?available.map(p=>'<button type=\"button\" class=\"credit-segment\" style=\"width:'+(Number(p.remain)/total*100)+'%\" aria-label=\"'+escapeHtml(packageDescription(p))+'\" data-tooltip=\"'+escapeHtml(packageDescription(p))+'\"></button>').join(''):'<span class=\"credit-bar-empty\"></span>';\r\n      const count=packages.length||account.creditPackages||0;\r\n      const details=packages.length?'<details class=\"credit-details\"><summary>查看 '+count+' 个积分包的到期时间</summary><ul class=\"credit-detail-list\">'+packages.map(p=>'<li class=\"credit-detail\"><strong>'+escapeHtml(p.name||'未命名积分包')+'</strong><span class=\"credit-detail-amount\">'+formatCredits(p.remain)+' 积分</span><small>'+escapeHtml(expiryText(p.expiresAt))+'</small></li>').join('')+'</ul></details>':'';\r\n      return '<div class=\"credit-bar\" role=\"group\" aria-label=\"积分包剩余积分占比，点击查看到期时间\">'+segments+'</div><div class=\"account-footer\"><span>'+(packages.length?'按剩余积分占比分段':account.credits==null?'积分包未同步':'暂无可用积分包明细')+'</span><span>'+(account.creditsRefreshing?'积分明细同步中…':account.creditsUpdatedAt?'更新于 '+escapeHtml(formatDate(account.creditsUpdatedAt)):'等待同步')+'</span></div>'+details;\n    }\r\n    function renderAccount(account){\r\n      const states={Active:'可用',Disabled:'已停用',Invalid:'凭证失效'}, state=account.state||'Active';\r\n      const balance=account.credits==null?(account.creditsRefreshing?'同步中…':account.creditsError?'读取失败':'未同步'):formatCredits(account.credits);\n      return '<article class=\"account-card\"><div class=\"account-heading\"><h4 class=\"account-name\">'+escapeHtml(displayName(account.nickname||account.accountId))+'</h4><span class=\"account-state'+(state==='Active'?'':' inactive')+'\">'+escapeHtml(states[state]||state)+'</span></div>'\r\n        +'<p class=\"account-validity\">有效期至 <time>'+escapeHtml(formatDate(account.expiresAt))+'</time></p>'\r\n        +'<div class=\"credit-summary\"><div><span class=\"credit-label\">剩余积分</span><strong class=\"credit-balance\">'+balance+'</strong></div><div><span class=\"credit-label\">已用积分</span><strong class=\"credit-used\">'+formatCredits(account.creditsUsed)+'</strong></div></div>'\r\n        +renderCreditPackages(account)+(account.creditsError?'<p class=\"credits-warning\">积分刷新失败'+(account.credits!=null?'，当前显示上次同步的数据':'')+'，请稍后重试。</p>':'')+'</article>';\r\n    }\r\n    let balancePollTimer=null;\n    async function loadAccounts(){\n      clearTimeout(balancePollTimer);balancePollTimer=null;\n      const box=$('accounts');hidePackageTooltip();\n      try{const data=await api('GET','accounts'), rows=data.accounts||[], groups=[['cn','国内版'],['global','国际版']];\n        box.innerHTML=rows.length?groups.map(([realm,label])=>{const items=rows.filter(a=>(a.realm||'cn')===realm);return items.length?'<section class=\"catalog-group\"><h3>'+label+' · '+items.length+' 个账号</h3><div class=\"catalog-items\">'+items.map(renderAccount).join('')+'</div></section>':''}).join(''):'<div class=\"empty\">还没有账号，请先使用 OAuth 登录。</div>';\n        if(rows.some(account=>account.creditsRefreshing))balancePollTimer=setTimeout(loadAccounts,2000);\n      }catch(e){box.innerHTML='<div class=\"empty\">账号加载失败，请点击刷新重试。</div>';notice(e.message,true)}\r\n    }\r\n    let tooltipTarget=null;\r\n    function hidePackageTooltip(){if(tooltipTarget)tooltipTarget.removeAttribute('aria-describedby');tooltipTarget=null;$('credit-tooltip').hidden=true}\r\n    function showPackageTooltip(target){\r\n      if(!target)return;hidePackageTooltip();tooltipTarget=target;\r\n      const tip=$('credit-tooltip');tip.textContent=target.dataset.tooltip;tip.hidden=false;target.setAttribute('aria-describedby','credit-tooltip');\r\n      const rect=target.getBoundingClientRect(), size=tip.getBoundingClientRect();\r\n      tip.style.left=Math.max(12,Math.min(rect.left,window.innerWidth-size.width-12))+'px';\r\n      tip.style.top=Math.max(8,rect.top-size.height-8>=8?rect.top-size.height-8:Math.min(rect.bottom+8,window.innerHeight-size.height-8))+'px';\r\n    }\r\n    $('accounts').addEventListener('pointerover',e=>{if(e.pointerType!=='touch')showPackageTooltip(e.target.closest('.credit-segment'))});\r\n    $('accounts').addEventListener('pointerout',e=>{if(e.pointerType!=='touch'&&e.target.closest('.credit-segment')&&!e.target.contains(e.relatedTarget))hidePackageTooltip()});\r\n    $('accounts').addEventListener('focusin',e=>showPackageTooltip(e.target.closest('.credit-segment')));\r\n    $('accounts').addEventListener('focusout',hidePackageTooltip);\r\n    document.addEventListener('click',e=>{const target=e.target.closest('.credit-segment');if(target)showPackageTooltip(target);else hidePackageTooltip()});\r\n    document.addEventListener('keydown',e=>{if(e.key==='Escape')hidePackageTooltip()});\r\n    window.addEventListener('resize',hidePackageTooltip);window.addEventListener('scroll',hidePackageTooltip,true);\r\n    async function refreshAccounts(){const button=$('reload-accounts');setBusy(button,true,'刷新积分中…');try{await api('POST','accounts/refresh');await loadAccounts();notice('账号令牌和积分已刷新')}catch(e){await loadAccounts();notice(e.message,true)}finally{setBusy(button,false)}}\r\n    async function loadModels(){const box=$('models');try{const data=await api('GET','models');const rows=data.models||[];const groups=[['cn','国内版'],['global','国际版']];box.innerHTML=rows.length?groups.map(([realm,label])=>{const items=rows.filter(m=>(m.realm||'cn')===realm);return items.length?'<section class=\"catalog-group\"><h3>'+label+' · '+items.length+' 个模型</h3><div class=\"model-grid\">'+items.map(m=>'<article class=\"model\"><strong>'+escapeHtml(m.displayName||m.id)+'</strong><code>'+escapeHtml('workbuddy/'+m.id)+'</code><span>积分倍率 '+escapeHtml(m.creditMultiplier||'上游未提供')+' · 上下文 '+(m.contextWindow||'-')+' · 输入 '+(m.inputLimit||'-')+' · 输出 '+(m.outputLimit||'-')+(m.supportsReasoning?' · 支持推理':'')+'</span></article>').join('')+'</div></section>':''}).join(''):'<div class=\"empty\">暂无模型；登录账号后刷新模型缓存。</div>'}catch(e){box.innerHTML='<div class=\"empty\">模型加载失败</div>';notice(e.message,true)}}\r\n    async function startLogin(){const button=$('login');setBusy(button,true,'准备授权…');$('login-state').textContent='正在获取授权地址';try{const data=await api('POST','oauth/start',{realm:$('realm').value});$('login-link').href=data.url;$('login-link').style.display='inline-flex';const popup=window.open(data.url,'_blank','noopener,noreferrer');$('login-state').textContent=popup?'等待官方登录完成':'浏览器阻止了弹窗，请点击“重新打开授权页面”';notice('授权地址已生成，登录完成后此页面会自动保存账号。');await pollLogin(data.state)}catch(e){$('login-state').textContent='授权失败';notice(e.message,true)}finally{setBusy(button,false)}}\r\n    async function pollLogin(state){for(let i=0;i<100;i++){await sleep(2500);const data=await api('GET','oauth/poll?state='+encodeURIComponent(state));if(data.done){$('login-state').textContent='登录成功';notice('账号 '+displayName(data.nickname||data.uid)+' 已保存，积分和明细正在后台同步');await loadAccounts();await loadModels();return}}$('login-state').textContent='轮询超时';notice('登录等待已超时，请重新发起 OAuth。',true)}\n    async function refreshModels(){const button=$('refresh-models');setBusy(button,true,'刷新中…');try{await api('POST','models/refresh');notice('WorkBuddy 模型缓存已刷新');await loadModels()}catch(e){notice(e.message,true)}finally{setBusy(button,false)}}\r\n    async function watchTaskRun(run,statusRoute,label){notice(run.alreadyRunning?label+' 已在后台运行…':label+' 已启动后台执行…');while(true){await sleep(2500);const state=await api('GET',statusRoute(run.taskId));if(state.taskId!==run.taskId){notice('后台任务状态已重置或过期，请查看宿主任务日志',true);break}if(state.status==='Running')continue;if(state.status==='Completed')notice(label+'流程已结束；账号和单项结果请查看宿主任务日志');else if(state.status==='Failed')notice(label+'失败：'+(state.error||'未知错误'),true);else if(state.status==='Cancelled')notice(label+'已取消',true);break}}\r\n    async function runTask(name,button){const label=button.textContent;setBusy(button,true,'后台执行中…');try{const run=await api('POST','tasks/run',{task:name});await watchTaskRun(run,id=>'tasks/status?taskId='+encodeURIComponent(id),label)}catch(e){notice(e.message,true)}finally{setBusy(button,false)}}\r\n    async function completeGrowthTasks(){const button=$('growth-auto'),label=button.textContent;setBusy(button,true,'后台执行中…');try{const run=await api('POST','growth/complete',{});await watchTaskRun(run,()=>'growth/status',label)}catch(e){notice(e.message,true)}finally{setBusy(button,false)}}\r\n    $('login').onclick=startLogin;$('reload-accounts').onclick=refreshAccounts;$('refresh-models').onclick=refreshModels;$('growth-auto').onclick=completeGrowthTasks;document.querySelectorAll('[data-task]').forEach(button=>button.onclick=()=>runTask(button.dataset.task,button));loadAccounts();loadModels();\n  </script>\r\n</body>\r\n</html>", "1");
	}

	/// <summary>发送 OAuth 状态、令牌、账号资料或刷新令牌请求，并统一解析官方响应包络。</summary>
	private async Task<UpstreamResult> SendOAuthAsync(HttpMethod method, string url, string realm, JsonNode? body, string? bearer, string? refreshToken, CancellationToken cancellationToken, HttpClient? providedClient = null, OAuthCredential? credential = null)
	{
		using HttpClient ownedClient = ((providedClient == null) ? CreateDirectClient(cancellationToken) : null);
		HttpClient client = providedClient ?? ownedClient;
		using HttpRequestMessage request = new HttpRequestMessage(method, url);
		if (body != null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}
		ApplyOAuthHeaders(request, realm, bearer, refreshToken, credential);
		if (refreshToken != null)
		{
			request.Headers.TryAddWithoutValidation("X-Auth-Refresh-Source", "plugin");
		}
		return await ReadUpstreamAsync(client, request, cancellationToken);
	}

	/// <summary>发送需要账号 OAuth 令牌的 JSON 业务请求；上游失败时抛出包含错误摘要的异常。</summary>
	private static async Task<UpstreamResult> SendAccountJsonAsync(HttpClient client, OAuthCredential credential, HttpMethod method, string url, JsonNode? body, CancellationToken cancellationToken, bool webClaim = false, string? clientPlatform = null)
	{
		using HttpRequestMessage request = new HttpRequestMessage(method, url);
		if (body != null)
		{
			request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		}
		ApplyHeaders(request, credential, includeAuthorization: true, streaming: false, webClaim, clientPlatform);
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		if (!upstreamResult.Success)
		{
			throw new InvalidOperationException(upstreamResult.Message);
		}
		return upstreamResult;
	}

	/// <summary>
	/// 读取完整上游响应，兼容普通 JSON 和带 `code`/`data` 的业务包装；
	/// HTTP 或业务错误保留原始消息供调用方记录。
	/// </summary>
	private static async Task<UpstreamResult> ReadUpstreamAsync(HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
	{
		using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
		string text = await response.Content.ReadAsStringAsync(cancellationToken);
		JsonNode jsonNode = null;
		try
		{
			jsonNode = (string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text));
		}
		catch (JsonException)
		{
		}
		if (!response.IsSuccessStatusCode)
		{
			return new UpstreamResult((int)response.StatusCode, null, text, Success: false);
		}
		if (jsonNode is JsonObject jsonObject && TryReadInt(jsonObject, "code", out var value))
		{
			string message = ReadNodeString(jsonObject, "msg", "message") ?? text;
			if (value != 0)
			{
				return new UpstreamResult((int)response.StatusCode, jsonObject["data"]?.DeepClone(), message, Success: false);
			}
			return new UpstreamResult((int)response.StatusCode, jsonObject["data"]?.DeepClone(), message, Success: true);
		}
		return new UpstreamResult((int)response.StatusCode, jsonNode, string.Empty, Success: true);
	}

	/// <summary>聚合非流式 SSE/JSON 输出中的文本、推理、工具调用、结束原因和用量。</summary>
	private async Task<AdapterCompletion> ReadCompletionAsync(HttpResponseMessage response, string model, Account account, CancellationToken cancellationToken)
	{
		StringBuilder content = new StringBuilder();
		StringBuilder reasoning = new StringBuilder();
		string finishReason = "stop";
		Usage usage = null;
		SortedDictionary<int, ToolCallAccumulator> toolCalls = new SortedDictionary<int, ToolCallAccumulator>();
		bool sawContentDelta = false;
		bool sawMessageContent = false;
		bool sawReasoningDelta = false;
		bool sawMessageReasoning = false;
		try
		{
			await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			string eventName = null;
			while (true)
			{
				string text = await reader.ReadLineAsync(cancellationToken);
				if (text == null)
				{
					break;
				}
				if (text.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
				{
					eventName = text.Substring(6).Trim();
					continue;
				}
				string text2 = SseData(text);
				if (text2 == null)
				{
					continue;
				}
				string eventName2 = eventName;
				eventName = null;
				if (text2 == "[DONE]")
				{
					break;
				}
				JsonNode root = JsonNode.Parse(text2);
				string text3 = ReadSseError(root, eventName2);
				if (text3 != null)
				{
					throw new HttpRequestException(text3);
				}
				usage = ReadUsage(root) ?? usage;
				JsonNode jsonNode = FirstChoice(root);
				if (jsonNode == null)
				{
					continue;
				}
				JsonNode node = jsonNode["delta"];
				JsonNode node2 = jsonNode["message"];
				string value = ReadNodeString(node, "content");
				if (!string.IsNullOrEmpty(value))
				{
					if (!sawContentDelta & sawMessageContent)
					{
						content.Clear();
					}
					content.Append(value);
					sawContentDelta = true;
				}
				else if (!sawContentDelta && !sawMessageContent)
				{
					string text4 = ReadNodeString(node2, "content");
					if (text4 != null && text4.Length > 0)
					{
						content.Append(text4);
						sawMessageContent = true;
					}
				}
				string value2 = ReadNodeString(node, "reasoning_content", "reasoning", "thinking");
				if (!string.IsNullOrEmpty(value2))
				{
					if (!sawReasoningDelta & sawMessageReasoning)
					{
						reasoning.Clear();
					}
					reasoning.Append(value2);
					sawReasoningDelta = true;
				}
				else if (!sawReasoningDelta && !sawMessageReasoning)
				{
					string text5 = ReadNodeString(node2, "reasoning_content", "reasoning", "thinking");
					if (text5 != null && text5.Length > 0)
					{
						reasoning.Append(text5);
						sawMessageReasoning = true;
					}
				}
				finishReason = ReadNodeString(jsonNode, "finish_reason") ?? finishReason;
				List<ToolCallDelta> list = ReadToolCallDeltas(node);
				if (list.Count == 0 && toolCalls.Count == 0)
				{
					list = ReadToolCallDeltas(node2);
				}
				foreach (ToolCallDelta item in list)
				{
					if (!toolCalls.TryGetValue(item.Index, out ToolCallAccumulator value3))
					{
						value3 = new ToolCallAccumulator();
						toolCalls[item.Index] = value3;
					}
					value3.Apply(item);
				}
			}
			await ClearSessionDeadFailuresAsync(account, cancellationToken);
		}
		finally
		{
			response.Dispose();
		}
		if (finishReason == "length")
		{
			int[] array = (from pair in toolCalls
				where IsTruncatedToolArguments(pair.Value.Arguments.ToString())
				select pair.Key).ToArray();
			foreach (int key in array)
			{
				toolCalls.Remove(key);
			}
		}
		if (toolCalls.Count == 0 && finishReason == "tool_calls")
		{
			finishReason = "stop";
		}
		AdapterToolCall[] array2 = (from call in ((IEnumerable<KeyValuePair<int, ToolCallAccumulator>>)toolCalls).Select((Func<KeyValuePair<int, ToolCallAccumulator>, AdapterToolCall>)((KeyValuePair<int, ToolCallAccumulator> pair) =>
			{
				//IL_0030: Unknown result type (might be due to invalid IL or missing references)
				//IL_0036: Expected Obj, but got Unknown
				return new AdapterToolCall(pair.Key, pair.Value.Id, pair.Value.Name, pair.Value.Arguments.ToString());
			}))
			where !string.IsNullOrWhiteSpace(call.Name)
			select call).ToArray();
		bool flag = array2.Length == 0;
		if (flag)
		{
			string text6 = finishReason;
			bool flag2 = ((text6 == "tool_calls" || text6 == "function_call") ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			finishReason = "stop";
		}
		else
		{
			flag = array2.Length != 0;
			if (flag)
			{
				string text6 = finishReason;
				bool flag2 = ((text6 == "stop" || text6 == "function_call") ? true : false);
				flag = flag2;
			}
			if (flag)
			{
				finishReason = "tool_calls";
			}
		}
		return new AdapterCompletion(model, (content.Length == 0) ? null : content.ToString(), finishReason, usage, (IReadOnlyList<AdapterToolCall>)array2, (reasoning.Length == 0) ? null : reasoning.ToString(), (string)null);
	}

	/// <summary>
	/// 逐行读取上游 SSE `data:` 消息并转换为宿主 StreamChunk；
	/// 流结束时释放上游响应，遇到 `[DONE]` 时输出标准结束原因。
	/// </summary>
	private async IAsyncEnumerable<StreamChunk> ReadStreamAsync(HttpResponseMessage response, Account account, PluginAttemptContext context, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		bool sawTools = false;
		bool sawContent = false;
		bool sawContentDelta = false;
		bool sawReasoningDelta = false;
		string pendingMessageContent = null;
		string pendingMessageReasoning = null;
		List<ToolCallDelta> pendingMessageTools = null;
		string upstreamFinishReason = null;
		Usage lastUsage = null;
		bool clearedSessionStrikes = false;
		try
		{
			await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
			string eventName = null;
			while (true)
			{
				string text = await reader.ReadLineAsync(cancellationToken);
				bool flag;
				bool flag2;
				if (text != null)
				{
					if (text.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
					{
						eventName = text.Substring(6).Trim();
						continue;
					}
					string text2 = SseData(text);
					if (text2 == null)
					{
						continue;
					}
					string eventName2 = eventName;
					eventName = null;
					if (text2 == "[DONE]")
					{
						if (!sawContentDelta && !string.IsNullOrEmpty(pendingMessageContent))
						{
							yield return new StreamChunk(pendingMessageContent, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, (string)null, (string)null);
							sawContent = true;
						}
						if (!sawReasoningDelta && !string.IsNullOrEmpty(pendingMessageReasoning))
						{
							yield return new StreamChunk((string)null, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, pendingMessageReasoning, (string)null, (string)null, (string)null);
						}
						if (!sawTools && pendingMessageTools != null && pendingMessageTools.Count > 0)
						{
							yield return new StreamChunk((string)null, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)pendingMessageTools, (string)null, (string)null, (string)null, (string)null);
							sawTools = true;
						}
						if (!sawContent && !sawTools)
						{
							string value = ((lastUsage == null) ? "unknown" : lastUsage.CompletionTokens.ToString(CultureInfo.InvariantCulture));
							string error = $"WorkBuddy upstream returned no answer content or tool calls (finish_reason={upstreamFinishReason ?? "unknown"}, reasoning_present={sawReasoningDelta || !string.IsNullOrEmpty(pendingMessageReasoning)}, completion_tokens={value})";
							WorkBuddyTerminal workBuddyTerminal = this;
							string traceId = context.TraceId;
							string id = account.Id;
							string model = NormalizeModel(context.Request.Model);
							int? statusCode = 502;
							object details = new
							{
								downstreamRequest = new
								{
									body = context.Request.OriginalBody
								}
							};
							await workBuddyTerminal.TryLogAsync("upstream.empty-response", error, "Error", traceId, null, id, model, statusCode, null, details);
							yield return new StreamChunk((string)null, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, error, "upstream_empty_response");
							break;
						}
						flag = sawTools;
						if (flag)
						{
							if (upstreamFinishReason != null)
							{
								string text3 = upstreamFinishReason;
								if (!(text3 == "stop") && !(text3 == "function_call"))
								{
									flag2 = false;
									goto IL_062e;
								}
							}
							flag2 = true;
							goto IL_062e;
						}
						goto IL_0632;
					}
					JsonNode root = JsonNode.Parse(text2);
					string text4 = ReadSseError(root, eventName2);
					if (text4 != null)
					{
						yield return new StreamChunk((string)null, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, text4, "upstream_error");
						break;
					}
					Usage usage = ReadUsage(root);
					lastUsage = usage ?? lastUsage;
					JsonNode choice = FirstChoice(root);
					if (choice == null)
					{
						if (usage != null)
						{
							yield return new StreamChunk((string)null, (string)null, usage, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, (string)null, (string)null);
						}
						continue;
					}
					if (!clearedSessionStrikes)
					{
						await ClearSessionDeadFailuresAsync(account, cancellationToken);
						clearedSessionStrikes = true;
					}
					JsonNode node = choice["delta"];
					JsonNode node2 = choice["message"];
					string text5 = ReadNodeString(node, "content");
					if (!string.IsNullOrEmpty(text5))
					{
						sawContentDelta = true;
						sawContent = true;
						pendingMessageContent = null;
					}
					else if (!sawContentDelta)
					{
						string text6 = ReadNodeString(node2, "content");
						if (text6 != null && text6.Length > 0)
						{
							pendingMessageContent = text6;
						}
					}
					string text7 = ReadNodeString(node, "reasoning_content", "reasoning", "thinking");
					if (!string.IsNullOrEmpty(text7))
					{
						sawReasoningDelta = true;
						pendingMessageReasoning = null;
					}
					else if (!sawReasoningDelta)
					{
						string text8 = ReadNodeString(node2, "reasoning_content", "reasoning", "thinking");
						if (text8 != null && text8.Length > 0)
						{
							pendingMessageReasoning = text8;
						}
					}
					string text9 = ReadNodeString(choice["delta"], "role");
					string text10 = ReadNodeString(choice, "finish_reason");
					List<ToolCallDelta> list = ReadToolCallDeltas(node);
					if (list.Count > 0)
					{
						sawTools = true;
						pendingMessageTools = null;
					}
					else if (!sawTools)
					{
						List<ToolCallDelta> list2 = ReadToolCallDeltas(node2);
						if (list2.Count > 0)
						{
							pendingMessageTools = list2;
						}
					}
					if (text10 != null)
					{
						if (!sawContentDelta && !string.IsNullOrEmpty(pendingMessageContent))
						{
							text5 = pendingMessageContent;
							pendingMessageContent = null;
							sawContent = true;
						}
						if (!sawReasoningDelta && !string.IsNullOrEmpty(pendingMessageReasoning))
						{
							text7 = pendingMessageReasoning;
							pendingMessageReasoning = null;
						}
						if (!sawTools && pendingMessageTools != null && pendingMessageTools.Count > 0)
						{
							list = pendingMessageTools;
							pendingMessageTools = null;
							sawTools = true;
						}
					}
					if (text10 != null)
					{
						upstreamFinishReason = text10;
					}
					string text11 = ReadNodeString(choice["delta"], "reasoning_signature");
					if (text5 != null || text7 != null || text11 != null || text9 != null || usage != null || list.Count > 0)
					{
						yield return new StreamChunk(text5, (string)null, usage, text9, (IReadOnlyList<ToolCallDelta>)((list.Count == 0) ? null : list), text7, text11, (string)null, (string)null);
					}
					continue;
				}
				yield return new StreamChunk((string)null, (string)null, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, "WorkBuddy upstream stream ended before [DONE]", "upstream_incomplete_response");
				break;
				IL_0632:
				string text12 = (flag ? "tool_calls" : (upstreamFinishReason ?? "stop"));
				yield return new StreamChunk((string)null, text12, (Usage)null, (string)null, (IReadOnlyList<ToolCallDelta>)null, (string)null, (string)null, (string)null, (string)null);
				break;
				IL_062e:
				flag = flag2;
				goto IL_0632;
			}
		}
		finally
		{
			response.Dispose();
		}
	}

	/// <summary>把宿主标准消息、工具和生成参数构造成 WorkBuddy Chat Completions 请求体。</summary>
	private JsonObject BuildChatRequestBody(AdapterRequest request, string model)
	{
		List<JsonObject> list = BuildTools(request.Tools);
		JsonArray jsonArray = new JsonArray();
		foreach (AdapterMessage message in request.Messages)
		{
			JsonObject jsonObject = new JsonObject { ["role"] = (string.Equals(message.Role, "developer", StringComparison.OrdinalIgnoreCase) ? "system" : message.Role) };
			object value;
			IReadOnlyList<object> toolCalls;
			if (string.IsNullOrEmpty(message.Content))
			{
				IReadOnlyList<AdapterContentPart> contentParts = message.ContentParts;
				if (contentParts == null || contentParts.Count <= 0)
				{
					toolCalls = message.ToolCalls;
					if (toolCalls != null && toolCalls.Count > 0)
					{
						value = null;
						goto IL_00c9;
					}
				}
			}
			value = ToJsonNode(AdapterContentFormatter.ToChatContent(message));
			goto IL_00c9;
			IL_00c9:
			jsonObject["content"] = (JsonNode?)value;
			JsonObject jsonObject2 = jsonObject;
			if (!string.IsNullOrWhiteSpace(message.Name))
			{
				jsonObject2["name"] = message.Name;
			}
			if (!string.IsNullOrWhiteSpace(message.ToolCallId))
			{
				jsonObject2["tool_call_id"] = message.ToolCallId;
			}
			toolCalls = message.ToolCalls;
			if (toolCalls != null && toolCalls.Count > 0)
			{
				JsonArray jsonArray2 = new JsonArray();
				foreach (object toolCall in message.ToolCalls)
				{
					JsonNode jsonNode = ToJsonNode(toolCall);
					if (jsonNode != null)
					{
						jsonArray2.Add(jsonNode);
					}
				}
				jsonObject2["tool_calls"] = jsonArray2;
			}
			JsonElement? reasoningContent = message.ReasoningContent;
			if (reasoningContent.HasValue)
			{
				JsonElement valueOrDefault = reasoningContent.GetValueOrDefault();
				jsonObject2["reasoning_content"] = ToJsonNode(valueOrDefault);
			}
			reasoningContent = message.Reasoning;
			if (reasoningContent.HasValue)
			{
				JsonElement valueOrDefault2 = reasoningContent.GetValueOrDefault();
				jsonObject2["reasoning"] = ToJsonNode(valueOrDefault2);
			}
			jsonArray.Add(jsonObject2);
		}
		JsonObject jsonObject3 = new JsonObject
		{
			["model"] = model,
			["messages"] = jsonArray,
			["stream"] = true,
			["stream_options"] = new JsonObject { ["include_usage"] = true }
		};
		int? maxTokens = request.MaxTokens;
		if (maxTokens.HasValue)
		{
			int valueOrDefault3 = maxTokens.GetValueOrDefault();
			jsonObject3["max_tokens"] = valueOrDefault3;
		}
		double? temperature = request.Temperature;
		if (temperature.HasValue)
		{
			double valueOrDefault4 = temperature.GetValueOrDefault();
			jsonObject3["temperature"] = valueOrDefault4;
		}
		if (list.Count > 0)
		{
			jsonObject3["tools"] = new JsonArray(((IEnumerable<JsonObject>)list).Select((Func<JsonObject, JsonNode>)((JsonObject tool) => tool)).ToArray());
		}
		if (request.Extensions.TryGetValue("tool_choice", out var value2))
		{
			jsonObject3["tool_choice"] = ToJsonNode(value2);
		}
		else if (list.Count > 0)
		{
			jsonObject3["tool_choice"] = "auto";
		}
		string[] array = new string[12]
		{
			"reasoning_effort", "reasoningEffort", "reasoning", "thinking", "enable_thinking", "enableThinking", "include_reasoning", "reasoning_summary", "response_format", "parallel_tool_calls",
			"top_p", "stop"
		};
		foreach (string text in array)
		{
			if (request.Extensions.TryGetValue(text, out var value3))
			{
				jsonObject3[text] = ToJsonNode(value3);
			}
		}
		if (!jsonObject3.ContainsKey("stop") && request.Extensions.TryGetValue("stop_sequences", out var value4))
		{
			jsonObject3["stop"] = ToJsonNode(value4);
		}
		NormalizeToolChoice(jsonObject3);
		CleanupOrphanToolCalls(jsonArray);
		WorkBuddyModelReference workBuddyModelReference = ParseModelReference(request.Model);
		if (!_modelReasoningLevels.TryGetValue(workBuddyModelReference.CacheKey, out IReadOnlyList<string> value5))
		{
			_modelReasoningLevels.TryGetValue(model, out value5);
		}
		ApplyThinkingIntent(jsonObject3, value5);
		NormalizeReasoningEffort(jsonObject3, value5);
		BackfillReasoningContent(model, jsonArray);
		if (IsFingerprintSanitizationEnabled())
		{
			SanitizeMessages(jsonArray);
		}
		return jsonObject3;
	}

	private static void NormalizeToolChoice(JsonObject body)
	{
		if (!body.TryGetPropertyValue("tool_choice", out JsonNode jsonNode))
		{
			return;
		}
		if (TryReadJsonString(jsonNode, out string value))
		{
			if (string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase))
			{
				body.Remove("tool_choice");
				body.Remove("tools");
				body.Remove("functions");
			}
			return;
		}
		if (!(jsonNode is JsonObject jsonObject))
		{
			body.Remove("tool_choice");
			return;
		}
		string text = ReadNodeString(jsonObject, "type")?.Trim().ToLowerInvariant();
		bool flag;
		switch (text)
		{
		case "none":
			body.Remove("tool_choice");
			body.Remove("tools");
			body.Remove("functions");
			return;
		case "auto":
		case "required":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag)
		{
			body["tool_choice"] = text;
			return;
		}
		switch (text)
		{
		case "any":
			body["tool_choice"] = "required";
			break;
		case "function":
		{
			string text3 = ReadNodeString(jsonObject["function"], "name") ?? ReadNodeString(jsonObject, "name");
			body["tool_choice"] = (string.IsNullOrWhiteSpace(text3) ? "auto" : text3);
			break;
		}
		case "tool":
		{
			string text2 = ReadNodeString(jsonObject, "name");
			if (string.IsNullOrWhiteSpace(text2))
			{
				body.Remove("tool_choice");
			}
			else
			{
				body["tool_choice"] = text2;
			}
			break;
		}
		default:
			body.Remove("tool_choice");
			break;
		}
	}

	private static void CleanupOrphanToolCalls(JsonArray messages)
	{
		HashSet<string> callIds = new HashSet<string>(StringComparer.Ordinal);
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		foreach (JsonObject item in messages.OfType<JsonObject>())
		{
			string a = ReadNodeString(item, "role");
			if (string.Equals(a, "tool", StringComparison.Ordinal))
			{
				string text = ReadNodeString(item, "tool_call_id");
				if (!string.IsNullOrEmpty(text))
				{
					hashSet.Add(text);
				}
			}
			else
			{
				if (!string.Equals(a, "assistant", StringComparison.Ordinal) || !(item["tool_calls"] is JsonArray source))
				{
					continue;
				}
				foreach (JsonObject item2 in source.OfType<JsonObject>())
				{
					string text2 = ReadNodeString(item2, "id");
					if (!string.IsNullOrEmpty(text2))
					{
						callIds.Add(text2);
					}
				}
			}
		}
		if (callIds.Count == 0 && hashSet.Count == 0)
		{
			return;
		}
		callIds.IntersectWith(hashSet);
		foreach (JsonObject item3 in messages.OfType<JsonObject>())
		{
			if (string.Equals(ReadNodeString(item3, "role"), "assistant", StringComparison.Ordinal) && item3["tool_calls"] is JsonArray { Count: not 0 } jsonArray && !jsonArray.All((JsonNode call) => call is JsonObject node2 && callIds.Contains(ReadNodeString(node2, "id") ?? string.Empty)))
			{
				item3.Remove("tool_calls");
			}
		}
		HashSet<string> hashSet2 = (from call in (from message in messages.OfType<JsonObject>()
				where string.Equals(ReadNodeString(message, "role"), "assistant", StringComparison.Ordinal)
				select message).SelectMany((JsonObject message) => (!(message["tool_calls"] is JsonArray source2)) ? Array.Empty<JsonObject>() : source2.OfType<JsonObject>())
			select ReadNodeString(call, "id") into id
			where !string.IsNullOrEmpty(id)
			select id).ToHashSet(StringComparer.Ordinal);
		for (int num = messages.Count - 1; num >= 0; num--)
		{
			if (messages[num] is JsonObject node && string.Equals(ReadNodeString(node, "role"), "tool", StringComparison.Ordinal) && !hashSet2.Contains(ReadNodeString(node, "tool_call_id") ?? string.Empty))
			{
				messages.RemoveAt(num);
			}
		}
	}

	private static void ApplyThinkingIntent(JsonObject body, IReadOnlyList<string>? supportedEfforts)
	{
		bool? flag = ReadThinkingIntent(body);
		if (!flag.HasValue)
		{
			return;
		}
		if (flag == false)
		{
			string[] array = new string[6] { "reasoning_effort", "reasoningEffort", "reasoning", "include_reasoning", "enable_thinking", "enableThinking" };
			foreach (string propertyName in array)
			{
				body.Remove(propertyName);
			}
			body["thinking"] = new JsonObject { ["type"] = "disabled" };
			return;
		}
		List<string> list = NormalizeEffortNames(supportedEfforts);
		if (list.Count != 0)
		{
			string explicitEffort = ReadExplicitEffort(body);
			string text = list.FirstOrDefault((string effort) => string.Equals(effort, explicitEffort, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault((string effort) => string.Equals(effort, "high", StringComparison.OrdinalIgnoreCase)) ?? list.OrderBy(EffortRankOrUnknown).LastOrDefault();
			if (!string.IsNullOrWhiteSpace(text))
			{
				body["reasoning_effort"] = text.ToLowerInvariant();
			}
		}
	}

	private static bool? ReadThinkingIntent(JsonObject body)
	{
		string[] array = new string[2] { "reasoning_effort", "reasoningEffort" };
		foreach (string key in array)
		{
			if (TryGetNodeString(body, key, out string value))
			{
				return !IsThinkingOff(value);
			}
		}
		array = new string[3] { "thinking", "enable_thinking", "enableThinking" };
		foreach (string propertyName in array)
		{
			bool? flag = ReadIntentFromNode(body[propertyName], nestedEffortCounts: true);
			if (flag.HasValue)
			{
				return flag == true;
			}
		}
		array = new string[3] { "reasoning", "include_reasoning", "reasoning_summary" };
		foreach (string propertyName2 in array)
		{
			bool? flag = ReadIntentFromNode(body[propertyName2], nestedEffortCounts: false);
			if (flag.HasValue)
			{
				return flag == true;
			}
		}
		return null;
	}

	private static bool? ReadIntentFromNode(JsonNode? node, bool nestedEffortCounts)
	{
		if (node is JsonValue jsonValue)
		{
			if (jsonValue.TryGetValue<bool>(out var value))
			{
				return value;
			}
			if (jsonValue.TryGetValue<string>(out string value2) && !string.IsNullOrWhiteSpace(value2))
			{
				return !IsThinkingOff(value2);
			}
		}
		if (!(node is JsonObject jsonObject))
		{
			return null;
		}
		string value3 = ReadNodeString(jsonObject, "type")?.Trim();
		if (!string.IsNullOrWhiteSpace(value3))
		{
			return !IsThinkingOff(value3);
		}
		if (TryGetNodeString(jsonObject, "effort", out string value4) && !string.IsNullOrWhiteSpace(value4))
		{
			return !IsThinkingOff(value4);
		}
		if (nestedEffortCounts && (jsonObject["budget_tokens"] != null || jsonObject["effort"] != null))
		{
			return true;
		}
		return null;
	}

	private static string? ReadExplicitEffort(JsonObject body)
	{
		string[] array = new string[2] { "reasoning_effort", "reasoningEffort" };
		foreach (string key in array)
		{
			if (TryGetNodeString(body, key, out string value) && !string.IsNullOrWhiteSpace(value))
			{
				return value.Trim();
			}
		}
		array = new string[2] { "thinking", "reasoning" };
		foreach (string propertyName in array)
		{
			if (body[propertyName] is JsonObject obj && TryGetNodeString(obj, "effort", out string value2) && !string.IsNullOrWhiteSpace(value2))
			{
				return value2.Trim();
			}
		}
		return null;
	}

	private static void NormalizeReasoningEffort(JsonObject body, IReadOnlyList<string>? supportedEfforts)
	{
		(string, int?)[] array = (from effort in NormalizeEffortNames(supportedEfforts)
			select (Name: effort, Rank: EffortRank(effort)) into tuple
			where tuple.Rank.HasValue
			select tuple).ToArray();
		if (array.Length == 0)
		{
			return;
		}
		string text;
		if (body.ContainsKey("reasoning_effort"))
		{
			text = "reasoning_effort";
		}
		else
		{
			text = (body.ContainsKey("reasoningEffort") ? "reasoningEffort" : null);
		}
		if (text == null || !TryGetNodeString(body, text, out string requested))
		{
			return;
		}
		requested = requested.Trim();
		if (IsThinkingOff(requested))
		{
			return;
		}
		int? num = EffortRank(requested);
		if (!num.HasValue)
		{
			return;
		}
		int requestedRank = num.GetValueOrDefault();
		if (array.Any(((string Name, int? Rank) tuple) => string.Equals(tuple.Name, requested, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}
		string item = array.Where(((string Name, int? Rank) tuple) => tuple.Rank <= requestedRank).OrderBy(((string Name, int? Rank) tuple) => tuple.Rank).LastOrDefault()
			.Item1;
		if (item == null)
		{
			item = array.OrderBy(((string Name, int? Rank) tuple) => tuple.Rank).First().Item1;
		}
		body[text] = item.ToLowerInvariant();
	}

	private static List<string> NormalizeEffortNames(IReadOnlyList<string>? efforts)
	{
		return efforts?.Select((string effort) => effort.Trim()).Where((string effort) => effort.Length > 0).ToList() ?? new List<string>();
	}

	private static int? EffortRank(string value)
	{
		switch (value.Trim().ToLowerInvariant())
		{
		case "off":
			return 0;
		case "none":
		case "minimal":
			return 1;
		case "low":
			return 2;
		case "medium":
			return 3;
		case "high":
			return 4;
		case "xhigh":
			return 5;
		case "max":
			return 6;
		default:
			return null;
		}
	}

	private static int EffortRankOrUnknown(string value)
	{
		return EffortRank(value) ?? int.MaxValue;
	}

	private static bool IsThinkingOff(string value)
	{
		if (!value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase) && !value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) && !value.Trim().Equals("disabled", StringComparison.OrdinalIgnoreCase) && !value.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
		{
			return value.Trim().Equals("0", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private static void BackfillReasoningContent(string model, JsonArray messages)
	{
		if (!model.StartsWith("deepseek", StringComparison.OrdinalIgnoreCase) || !messages.OfType<JsonObject>().Any((JsonObject message) => message.ContainsKey("reasoning_content") || (TryGetNodeString(message, "reasoning", out string value2) && value2.Length > 0)))
		{
			return;
		}
		foreach (JsonObject item in messages.OfType<JsonObject>())
		{
			if (string.Equals(ReadNodeString(item, "role"), "assistant", StringComparison.OrdinalIgnoreCase) && !item.ContainsKey("reasoning_content"))
			{
				item["reasoning_content"] = (TryGetNodeString(item, "reasoning", out string value) ? value : string.Empty);
			}
		}
	}

	private static bool IsFingerprintSanitizationEnabled()
	{
		string text = Environment.GetEnvironmentVariable("ROUTER2API_WORKBUDDY_SANITIZE")?.Trim();
		if (text != null)
		{
			if (!text.Equals("0", StringComparison.OrdinalIgnoreCase) && !text.Equals("false", StringComparison.OrdinalIgnoreCase) && !text.Equals("no", StringComparison.OrdinalIgnoreCase))
			{
				return !text.Equals("off", StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		return true;
	}

	private static void SanitizeMessages(JsonArray messages)
	{
		foreach (JsonObject item in messages.OfType<JsonObject>())
		{
			if (TryGetNodeString(item, "content", out string value))
			{
				item["content"] = SanitizeText(value);
			}
			else if (item["content"] is JsonArray source)
			{
				foreach (JsonObject item2 in source.OfType<JsonObject>())
				{
					if (TryGetNodeString(item2, "text", out string value2))
					{
						item2["text"] = SanitizeText(value2);
					}
				}
			}
			if (TryGetNodeString(item, "reasoning_content", out string value3))
			{
				item["reasoning_content"] = SanitizeText(value3);
			}
			if (!(item["tool_calls"] is JsonArray source2))
			{
				continue;
			}
			foreach (JsonObject item3 in source2.OfType<JsonObject>())
			{
				if (item3["function"] is JsonObject jsonObject && TryGetNodeString(jsonObject, "arguments", out string value4))
				{
					jsonObject["arguments"] = SanitizeText(value4);
				}
			}
		}
	}

	private static string SanitizeText(string text)
	{
		if (!text.Contains("x-anthropic-billing-header", StringComparison.OrdinalIgnoreCase) && !text.Contains("cc_", StringComparison.Ordinal) && !text.Contains("You are Claude Code", StringComparison.Ordinal) && !text.Contains("Main branch (", StringComparison.Ordinal) && !text.Contains("You are a coding agent running in the Codex CLI", StringComparison.Ordinal) && !text.Contains("github.com/anthropics/", StringComparison.Ordinal) && !text.Contains("11128", StringComparison.Ordinal))
		{
			return text;
		}
		text = text.Replace("You are Claude Code, Anthropic's official CLI for Claude", "You are Claude Code, Anthropic's official CLI tool for Claude", StringComparison.Ordinal);
		text = text.Replace("Main branch (you will usually use this for PRs)", "Default branch (you will usually use this for PRs)", StringComparison.Ordinal);
		text = text.Replace("You are a coding agent running in the Codex CLI, a terminal-based coding assistant.", "You are a coding agent running in the Codex CLI tool, a terminal-based coding assistant.", StringComparison.Ordinal);
		text = text.Replace("To give feedback, users should report the issue at https://github.com/anthropics/claude-code/issues", "To provide feedback, users should report the issue at https://github.com/anthropics/claude-code/issues", StringComparison.Ordinal);
		text = text.Replace("11128", "11-128", StringComparison.Ordinal);
		text = AnthropicBillingHeaderRegex.Replace(text, string.Empty);
		if (text.Contains("cc_", StringComparison.Ordinal))
		{
			string a;
			do
			{
				a = text;
				text = ClientContextKeyValueRegex.Replace(text, string.Empty);
			}
			while (!string.Equals(a, text, StringComparison.Ordinal));
		}
		return BareAnthropicBillingHeaderRegex.Replace(text, "x-anthropic-billing-hdr").Trim();
	}

	private static bool IsTruncatedToolArguments(string arguments)
	{
		if (string.IsNullOrWhiteSpace(arguments))
		{
			return false;
		}
		try
		{
			using (JsonDocument.Parse(arguments))
			{
				return false;
			}
		}
		catch (JsonException)
		{
			return true;
		}
	}

	private static bool TryGetNodeString(JsonObject obj, string key, out string value)
	{
		if (obj.TryGetPropertyValue(key, out JsonNode jsonNode) && TryReadJsonString(jsonNode, out value))
		{
			return true;
		}
		KeyValuePair<string, JsonNode> keyValuePair = obj.FirstOrDefault((KeyValuePair<string, JsonNode> item) => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
		if (keyValuePair.Key != null && TryReadJsonString(keyValuePair.Value, out value))
		{
			return true;
		}
		value = string.Empty;
		return false;
	}

	private static bool TryReadJsonString(JsonNode? node, out string value)
	{
		if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string value2))
		{
			value = value2;
			return true;
		}
		value = string.Empty;
		return false;
	}

	private static int WorkBuddyAccountAffinityWeight(Account account, AdapterRequest request)
	{
		string text = ReadExplicitAffinity(request) ?? DerivePrefixAffinity(request);
		if (string.IsNullOrWhiteSpace(text))
		{
			return 0;
		}
		return BinaryPrimitives.ReadInt32BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes("workbuddy-affinity:" + text + ":" + account.Id)));
	}

	private static string? ReadExplicitAffinity(AdapterRequest request)
	{
		string[] array = new string[8] { "x-conversation-id", "conversation-id", "x-session-id", "session-id", "x-session-affinity", "x-opencode-session", "conversation_id", "session_id" };
		foreach (string text in array)
		{
			if (request.RequestHeaders.TryGetValue(text, out var value) && !string.IsNullOrWhiteSpace(value))
			{
				return text + ":" + value.Trim();
			}
		}
		if (request.Extensions.TryGetValue("metadata", out var value2) && ToJsonNode(value2) is JsonObject obj)
		{
			array = new string[3] { "conversation_id", "session_id", "thread_id" };
			foreach (string text2 in array)
			{
				if (TryGetNodeString(obj, text2, out string value3) && !string.IsNullOrWhiteSpace(value3))
				{
					return "metadata:" + text2 + ":" + value3.Trim();
				}
			}
		}
		return null;
	}

	private static string? DerivePrefixAffinity(AdapterRequest request)
	{
		if (request.Messages.Count == 0)
		{
			return null;
		}
		JsonArray jsonArray = new JsonArray();
		foreach (AdapterMessage item in request.Messages.Take(2))
		{
			JsonObject jsonObject = new JsonObject
			{
				["role"] = (string.Equals(item.Role, "developer", StringComparison.OrdinalIgnoreCase) ? "system" : item.Role),
				["content"] = item.Content
			};
			if (item.Name != null)
			{
				jsonObject["name"] = item.Name;
			}
			if (item.ToolCallId != null)
			{
				jsonObject["tool_call_id"] = item.ToolCallId;
			}
			IReadOnlyList<object> toolCalls = item.ToolCalls;
			if (toolCalls != null && toolCalls.Count > 0)
			{
				jsonObject["tool_calls"] = new JsonArray((from node in item.ToolCalls.Select(ToJsonNode)
					where node != null
					select node).ToArray());
			}
			JsonElement? reasoningContent = item.ReasoningContent;
			if (reasoningContent.HasValue)
			{
				JsonElement valueOrDefault = reasoningContent.GetValueOrDefault();
				jsonObject["reasoning_content"] = ToJsonNode(valueOrDefault);
			}
			reasoningContent = item.Reasoning;
			if (reasoningContent.HasValue)
			{
				JsonElement valueOrDefault2 = reasoningContent.GetValueOrDefault();
				jsonObject["reasoning"] = ToJsonNode(valueOrDefault2);
			}
			jsonArray.Add(jsonObject);
		}
		string s = jsonArray.ToJsonString();
		byte[] array = SHA256.HashData(Encoding.UTF8.GetBytes(s));
		return "pfx-" + Convert.ToHexString(array[..8]).ToLowerInvariant();
	}

	/// <summary>将宿主工具描述规整为上游支持的 function 工具结构并跳过无名称工具。</summary>
	private static List<JsonObject> BuildTools(IReadOnlyList<object> rawTools)
	{
		List<JsonObject> list = new List<JsonObject>();
		foreach (object rawTool in rawTools)
		{
			if (!(ToJsonNode(rawTool) is JsonObject jsonObject))
			{
				continue;
			}
			JsonObject jsonObject2 = jsonObject["function"] as JsonObject;
			string text = ReadNodeString(jsonObject2, "name") ?? ReadNodeString(jsonObject, "name");
			if (!string.IsNullOrWhiteSpace(text))
			{
				bool flag = string.Equals(ReadNodeString(jsonObject, "type"), "custom", StringComparison.OrdinalIgnoreCase);
				string text2 = ReadNodeString(jsonObject2, "description") ?? ReadNodeString(jsonObject, "description") ?? string.Empty;
				JsonNode value = jsonObject2?["parameters"]?.DeepClone() ?? jsonObject["parameters"]?.DeepClone() ?? jsonObject2?["input_schema"]?.DeepClone() ?? jsonObject["input_schema"]?.DeepClone() ?? new JsonObject
				{
					["type"] = "object",
					["properties"] = new JsonObject()
				};
				if (flag && string.Equals(text, "exec", StringComparison.Ordinal))
				{
					text2 += "\n\nProxy compatibility: encode the JavaScript source or shell command in the required JSON field `command`.";
					value = new JsonObject
					{
						["type"] = "object",
						["properties"] = new JsonObject { ["command"] = new JsonObject
						{
							["type"] = "string",
							["description"] = "PowerShell command or complete JavaScript source for the WorkBuddy exec tool."
						} },
						["required"] = new JsonArray((JsonNode)JsonValue.Create("command")),
						["additionalProperties"] = false
					};
				}
				list.Add(new JsonObject
				{
					["type"] = "function",
					["function"] = new JsonObject
					{
						["name"] = text,
						["description"] = text2,
						["parameters"] = value
					}
				});
			}
		}
		return list;
	}

	/// <summary>按国内/国际账号、请求用途和流模式构造 WorkBuddy 业务请求头。</summary>
	private static void ApplyHeaders(HttpRequestMessage request, OAuthCredential credential, bool includeAuthorization, bool streaming, bool webClaim = false, string? clientPlatform = null)
	{
		bool flag = IsGlobal(credential);
		string text = (flag ? "https://www.workbuddy.ai" : "https://www.codebuddy.cn");
		request.Headers.Accept.Clear();
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? "text/event-stream" : "application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
		request.Headers.TryAddWithoutValidation("Origin", webClaim ? "https://www.workbuddy.cn" : text);
		request.Headers.TryAddWithoutValidation("Referer", webClaim ? "https://www.workbuddy.cn/profile/growth-center" : (text + "/"));
		request.Headers.TryAddWithoutValidation("User-Agent", flag ? "WorkBuddy/5.5.4 WorkBuddy AI/5.5.4 CLI/2.137.1" : "WorkBuddy/5.5.4 WorkBuddy/5.5.4 CLI/2.137.1");
		request.Headers.TryAddWithoutValidation("X-CodeBuddy-Request", "1");
		request.Headers.TryAddWithoutValidation("Accept-Language", flag ? "en-US" : "zh-CN");
		if (includeAuthorization && !string.IsNullOrWhiteSpace(credential.AccessToken))
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
		}
		if (!string.IsNullOrWhiteSpace(credential.AccountId))
		{
			request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
			request.Headers.TryAddWithoutValidation("X-Machine-ID", StableAccountId(credential.AccountId, "machine"));
			request.Headers.TryAddWithoutValidation("X-Session-ID", StableAccountId(credential.AccountId, "session"));
		}
		if (!string.IsNullOrWhiteSpace(credential.EnterpriseId))
		{
			request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
			request.Headers.TryAddWithoutValidation("X-Tenant-Id", credential.EnterpriseId);
		}
		else if (flag)
		{
			request.Headers.TryAddWithoutValidation("X-No-Enterprise-Id", "1");
		}
		request.Headers.TryAddWithoutValidation("X-Domain", NormalizeDomain(credential.Domain) ?? (flag ? "www.workbuddy.ai" : "copilot.tencent.com"));
		if (!webClaim)
		{
			request.Headers.TryAddWithoutValidation("X-Agent-Purpose", "conversation");
			request.Headers.TryAddWithoutValidation("X-IDE-Name", "WorkBuddy");
			request.Headers.TryAddWithoutValidation("X-IDE-Type", "WorkBuddy");
			request.Headers.TryAddWithoutValidation("X-IDE-Version", "5.5.4");
			request.Headers.TryAddWithoutValidation("X-Product", "WorkBuddy");
		}
		if (webClaim)
		{
			request.Headers.TryAddWithoutValidation("x-client-platform", "web");
		}
		else if (!string.IsNullOrWhiteSpace(clientPlatform))
		{
			request.Headers.TryAddWithoutValidation("X-Client-Platform", clientPlatform);
		}
	}

	/// <summary>为账号和用途生成稳定、不可直接反推出账号 ID 的机器/会话标识。</summary>
	private static string StableAccountId(string accountId, string purpose)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("wb2a:" + purpose + ":" + accountId))[..18]).ToLowerInvariant();
	}

	/// <summary>构造 OAuth 接口所需的站点、User-Agent、Bearer/Refresh Token 和账号标识请求头。</summary>
	private static void ApplyOAuthHeaders(HttpRequestMessage request, string realm, string? bearer, string? refreshToken, OAuthCredential? credential = null)
	{
		string text = (string.Equals(realm, "global", StringComparison.OrdinalIgnoreCase) ? "https://www.workbuddy.ai" : "https://www.codebuddy.cn");
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
		request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
		request.Headers.TryAddWithoutValidation("Origin", text);
		request.Headers.TryAddWithoutValidation("Referer", text + "/");
		request.Headers.TryAddWithoutValidation("User-Agent", "CLI/2.63.2 CodeBuddy/2.63.2");
		if (!string.IsNullOrWhiteSpace(bearer))
		{
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
		}
		if (!string.IsNullOrWhiteSpace(refreshToken))
		{
			request.Headers.TryAddWithoutValidation("X-Refresh-Token", refreshToken);
		}
		if (!string.IsNullOrWhiteSpace((credential != null) ? credential.AccountId : null))
		{
			request.Headers.TryAddWithoutValidation("X-User-Id", credential.AccountId);
			request.Headers.TryAddWithoutValidation("X-Machine-ID", StableAccountId(credential.AccountId, "machine"));
			request.Headers.TryAddWithoutValidation("X-Session-ID", StableAccountId(credential.AccountId, "session"));
		}
		if (!string.IsNullOrWhiteSpace((credential != null) ? credential.EnterpriseId : null))
		{
			request.Headers.TryAddWithoutValidation("X-Enterprise-Id", credential.EnterpriseId);
			request.Headers.TryAddWithoutValidation("X-Tenant-Id", credential.EnterpriseId);
		}
		if (!string.IsNullOrWhiteSpace((credential != null) ? credential.Domain : null))
		{
			request.Headers.TryAddWithoutValidation("X-Domain", NormalizeDomain(credential.Domain));
		}
	}

	/// <summary>将宿主追踪 ID 和可选对话 ID 映射为 WorkBuddy 会话追踪请求头。</summary>
	private static void ApplyConversationHeaders(HttpRequestMessage request, PluginAttemptContext context)
	{
		string value = NormalizeTraceId(context.TraceId);
		string text = Guid.NewGuid().ToString("N");
		request.Headers.TryAddWithoutValidation("X-Conversation-Request-ID", value);
		request.Headers.TryAddWithoutValidation("X-Root-Request-ID", value);
		request.Headers.TryAddWithoutValidation("X-Trace-ID", context.TraceId);
		request.Headers.TryAddWithoutValidation("X-B3-TraceId", value);
		request.Headers.TryAddWithoutValidation("X-B3-SpanId", text.Substring(0, 16));
		request.Headers.TryAddWithoutValidation("X-B3-Sampled", "1");
		request.Headers.TryAddWithoutValidation("X-Conversation-Message-ID", text);
		request.Headers.TryAddWithoutValidation("X-Request-ID", text);
		if (context.Request.RequestHeaders.TryGetValue("x-conversation-id", out var value2) && !string.IsNullOrWhiteSpace(value2))
		{
			request.Headers.TryAddWithoutValidation("X-Conversation-ID", value2);
		}
	}

	/// <summary>通过宿主 HTTP 客户端工厂创建用于账号管理类直连调用的客户端。</summary>
	private HttpClient CreateDirectClient(CancellationToken cancellationToken)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Expected Obj, but got Unknown
		cancellationToken.ThrowIfCancellationRequested();
		IPluginHttpServices http = _host.Http;
		PluginHttpClientOptions val = new PluginHttpClientOptions();
		val.set_AllowAutoRedirect(true);
		return http.CreateDirectClient(val);
	}

	/// <summary>从 WorkBuddy V3 配置接口按指定客户端 UA 发现模型和模型能力。</summary>
	private async Task<ModelFetchResult> FetchV3ModelsAsync(HttpClient client, OAuthCredential credential, string userAgent, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseFor(credential) + "/v3/config");
		ApplyHeaders(request, credential, includeAuthorization: true, streaming: false);
		request.Headers.Remove("User-Agent");
		request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		return new ModelFetchResult(upstreamResult.Success, upstreamResult.StatusCode, upstreamResult.Success ? ParseModels(upstreamResult.Data, useCliAgent: false).ToArray() : Array.Empty<ModelDescriptor>());
	}

	/// <summary>按指定路径读取企业模型目录，可选择按 CLI agent 声明过滤模型。</summary>
	private async Task<ModelFetchResult> FetchCliModelsAsync(HttpClient client, OAuthCredential credential, string path, bool useCliAgent, CancellationToken cancellationToken)
	{
		using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, BaseFor(credential) + path);
		ApplyHeaders(request, credential, includeAuthorization: true, streaming: false);
		UpstreamResult upstreamResult = await ReadUpstreamAsync(client, request, cancellationToken);
		return new ModelFetchResult(upstreamResult.Success, upstreamResult.StatusCode, upstreamResult.Success ? ParseModels(upstreamResult.Data, useCliAgent).ToArray() : Array.Empty<ModelDescriptor>());
	}

	/// <summary>国际站企业模型目录优先读新版 /v2 路径；失败或空列表时回退旧 /console 路径。</summary>
	private async Task<ModelFetchResult> FetchGlobalCliModelsAsync(HttpClient client, OAuthCredential credential, CancellationToken cancellationToken)
	{
		ModelFetchResult modelFetchResult = await FetchCliModelsAsync(client, credential, "/v2/enterprises/personal/models", useCliAgent: false, cancellationToken);
		if (modelFetchResult.Success && modelFetchResult.Models.Length != 0)
		{
			return modelFetchResult;
		}
		ModelFetchResult modelFetchResult2 = await FetchCliModelsAsync(client, credential, "/console/enterprises/personal/models", useCliAgent: false, cancellationToken);
		return (modelFetchResult2.Success && modelFetchResult2.Models.Length != 0) ? modelFetchResult2 : new ModelFetchResult(Success: false, modelFetchResult2.StatusCode, Array.Empty<ModelDescriptor>());
	}

	/// <summary>
	/// 解析并过滤上游模型：排除禁用、补全类、图片生成或输出上限过低的模型，
	/// 并提取上下文、输入/输出上限和推理能力。
	/// </summary>
	private static IEnumerable<ModelDescriptor> ParseModels(JsonNode? data, bool useCliAgent)
	{
		if (!(data is JsonObject jsonObject) || !(jsonObject["models"] is JsonArray source))
		{
			yield break;
		}
		HashSet<string> cliIds = null;
		if (useCliAgent && jsonObject["agents"] is JsonArray source2)
		{
			cliIds = ((source2.OfType<JsonObject>().FirstOrDefault((JsonObject item) => string.Equals(ReadNodeString(item, "name"), "cli", StringComparison.OrdinalIgnoreCase))?["models"] is JsonArray source3) ? (from item in source3
				select item?.ToString() into item
				where !string.IsNullOrWhiteSpace(item)
				select (item)).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>());
		}
		foreach (JsonObject item in source.OfType<JsonObject>())
		{
			string text = ReadNodeString(item, "id", "modelId", "model");
			if (string.IsNullOrWhiteSpace(text) || ReadNodeBoolean(item, "disabled") == true || (useCliAgent && cliIds != null && cliIds.Count > 0 && !cliIds.Contains(text)))
			{
				continue;
			}
			long valueOrDefault = ReadNodeLong(item, "maxOutputTokens").GetValueOrDefault();
			string[] source4 = ((item["tags"] is JsonArray source5) ? source5.Select((JsonNode tag) => tag?.ToString() ?? string.Empty).ToArray() : Array.Empty<string>());
			if (!text.StartsWith("nes-", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("completion-", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("codewise-", StringComparison.OrdinalIgnoreCase) && (valueOrDefault <= 0 || valueOrDefault > 256) && !source4.Any((string tag) => string.Equals(tag, "text-to-image", StringComparison.OrdinalIgnoreCase)))
			{
				long valueOrDefault2 = ReadNodeLong(item, "maxInputTokens").GetValueOrDefault();
				string[] array = (((item["reasoning"] as JsonObject)?["supportedEfforts"] is JsonArray source6) ? (from effort in source6
					select effort?.ToString() ?? string.Empty into value
					where value.Length > 0
					select value).ToArray() : Array.Empty<string>());
				bool flag = ReadNodeBoolean(item, "supportsReasoning") == true || array.Length != 0;
				yield return new ModelDescriptor(text, ReadNodeString(item, "name") ?? text, ToInt(valueOrDefault2), true, ToInt(valueOrDefault2), ToInt(valueOrDefault), flag, (IReadOnlyList<string>)array, (valueOrDefault > 0) ? new int?(ToInt(valueOrDefault)) : ((int?)null), ReadNodeString(item, "credits", "creditMultiplier", "credits_multiplier"));
			}
		}
	}

	/// <summary>构造管理页面账号 DTO，只返回令牌元数据和已缓存积分，不暴露 OAuth Token。</summary>
	private object ToAccountView(Account account)
	{
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		Credential credential = account.Credential;
		OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
		_balanceCache.TryGetValue(account.Id, out BalanceSnapshot value);
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		value = value?.WithoutExpiredPackages(utcNow);
		return new
		{
			id = account.Id,
			accountId = ((val != null) ? val.AccountId : null),
			nickname = (((val != null) ? val.Nickname : null) ?? account.Label),
			realm = ((val == null) ? "cn" : RealmFor(val)),
			realmName = ((val == null || !IsGlobal(val)) ? "国内版" : "国际版"),
			domain = NormalizeDomain((val != null) ? val.Domain : null),
			expiresAt = (((val != null) ? val.ExpiresAt : ((DateTimeOffset?)null)) ?? account.ExpiresAt),
			state = ((object)account.Status.State/*cast due to constrained. prefix*/).ToString(),
			credits = value?.Remain,
			creditsUsed = value?.Used,
			creditsTotal = value?.Size,
			creditPackages = value?.Packages,
			creditPackageDetails = value?.PackageDetails,
			nextCreditExpiry = (((object)value == null) ? ((DateTimeOffset?)null) : PreferredCreditExpiry(value, utcNow)),
			creditsRefreshing = IsBalanceRefreshing(account.Id),
			creditsUpdatedAt = value?.UpdatedAt,
			creditsError = value?.Error
		};
	}

	/// <summary>汇总未过期积分包的余额与明细；周期剩余优先于账号级剩余。</summary>
	private static BalanceSnapshot ParseBalance(JsonNode? data)
	{
		JsonNode jsonNode = data?["Response"] ?? data?["response"];
		JsonObject jsonObject = (jsonNode?["Data"] as JsonObject) ?? (jsonNode?["data"] as JsonObject) ?? (data as JsonObject) ?? new JsonObject();
		decimal num = 0m;
		decimal num2 = 0m;
		decimal num3 = 0m;
		List<CreditPackageSnapshot> list = new List<CreditPackageSnapshot>();
		foreach (JsonObject item in (((jsonObject["Accounts"] ?? jsonObject["accounts"]) as JsonArray) ?? throw new JsonException("WorkBuddy balance response does not contain an Accounts array.")).OfType<JsonObject>())
		{
			string text = ReadNodeString(item, "CapacityUnit", "capacityUnit", "capacity_unit");
			if (string.IsNullOrWhiteSpace(text) || text.Equals("credits", StringComparison.OrdinalIgnoreCase))
			{
				decimal valueOrDefault = ReadNodeDecimal(item, "CycleCapacitySize", "cycleCapacitySize", "cycle_capacity_size").GetValueOrDefault();
				decimal? num4 = ReadNodeDecimal(item, "CycleCapacityRemain", "cycleCapacityRemain", "cycle_capacity_remain");
				decimal val = ((valueOrDefault > 0m) ? valueOrDefault : ReadNodeDecimal(item, "CapacitySize", "capacitySize", "capacity_size").GetValueOrDefault());
				val = Math.Max(0m, val);
				decimal num5 = num4 ?? ReadNodeDecimal(item, "CapacityRemain", "capacityRemain", "capacity_remain").GetValueOrDefault();
				num5 = ((val > 0m) ? Math.Clamp(num5, 0m, val) : Math.Max(0m, num5));
				decimal val2 = ((valueOrDefault > 0m || num4.HasValue) ? ReadNodeDecimal(item, "CycleCapacityUsed", "cycleCapacityUsed", "cycle_capacity_used").GetValueOrDefault() : ReadNodeDecimal(item, "CapacityUsed", "capacityUsed", "capacity_used").GetValueOrDefault());
				val2 = Math.Max(Math.Max(0m, val2), val - num5);
				num += num5;
				num2 += val2;
				num3 += val;
				list.Add(new CreditPackageSnapshot(ReadNodeString(item, "PackageName", "packageName", "package_name", "Name", "name") ?? "未命名积分包", num5, val2, val, ReadCreditPackageExpiry(item)));
			}
		}
		decimal valueOrDefault2 = ReadNodeDecimal(jsonObject, "TotalDosage", "totalDosage", "total_dosage").GetValueOrDefault();
		if (valueOrDefault2 > num3)
		{
			num3 = valueOrDefault2;
		}
		if (num3 > 0m && num3 - num > num2)
		{
			num2 = num3 - num;
		}
		DateTimeOffset utcNow = DateTimeOffset.UtcNow;
		return new BalanceSnapshot(num, num2, num3, list.Count, utcNow, null, list.ToArray()).WithoutExpiredPackages(utcNow);
	}

	/// <summary>积分包在周期或套餐有效期中较早的时间失效；无有效日期时不推测有效期。</summary>
	private static DateTimeOffset? ReadCreditPackageExpiry(JsonObject item)
	{
		DateTimeOffset? dateTimeOffset = null;
		string[] array = new string[4]
		{
			ReadNodeString(item, "PackageEndTime", "packageEndTime", "package_end_time"),
			ReadNodeString(item, "CycleEndTime", "cycleEndTime", "cycle_end_time"),
			ReadNodeString(item, "DeductionEndTime", "deductionEndTime", "deduction_end_time"),
			ReadNodeString(item, "ExpiredTime", "expiredTime", "expired_time")
		};
		foreach (string text in array)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				continue;
			}
			DateTimeOffset result2;
			DateTime result3;
			if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
			{
				if (result <= 0)
				{
					continue;
				}
				long num = ((result < 100000000000L) ? (result * 1000) : result);
				if (num > 253402300799999L)
				{
					continue;
				}
				result2 = DateTimeOffset.FromUnixTimeMilliseconds(num);
			}
			else if (DateTime.TryParseExact(text, new string[3] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out result3))
			{
				if (result3.Ticks < 288000000000L)
				{
					continue;
				}
				result2 = new DateTimeOffset(result3, TimeSpan.FromHours(8));
			}
			else if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result2))
			{
				continue;
			}
			if (dateTimeOffset.HasValue)
			{
				DateTimeOffset value = result2;
				DateTimeOffset? dateTimeOffset2 = dateTimeOffset;
				if (!(value < dateTimeOffset2))
				{
					continue;
				}
			}
			dateTimeOffset = result2;
		}
		return dateTimeOffset;
	}

	/// <summary>将模型目录项投影为管理页面需要的稳定 JSON 字段。</summary>
	private static object ToModelView(ModelDescriptor model)
	{
		string text = ParseModelReference(model.Id).Realm ?? "cn";
		return new
		{
			id = model.Id,
			displayName = model.DisplayName,
			realm = text,
			realmName = ((text == "global") ? "国际版" : "国内版"),
			contextWindow = model.ContextWindow,
			inputLimit = model.InputLimit,
			outputLimit = model.OutputLimit,
			creditMultiplier = model.CreditMultiplier,
			supportsReasoning = model.SupportsReasoning,
			reasoningLevels = model.ReasoningLevels
		};
	}

	/// <summary>构造统一的 JSON 错误响应，正文格式为 `{ error }`。</summary>
	private static PluginResult Error(PluginHttpContext context, int statusCode, string message)
	{
		return context.Json(statusCode, (object)new
		{
			error = message
		});
	}

	/// <summary>删除超过 OAuth 登录有效期的待完成 state，避免登录状态字典持续增长。</summary>
	private void CleanupPendingLogins()
	{
		DateTimeOffset dateTimeOffset = DateTimeOffset.UtcNow - LoginTtl;
		foreach (KeyValuePair<string, PendingLogin> pendingLogin in _pendingLogins)
		{
			if (pendingLogin.Value.CreatedAt < dateTimeOffset)
			{
				_pendingLogins.TryRemove(pendingLogin.Key, out PendingLogin _);
			}
		}
	}

	/// <summary>凭据至少包含访问令牌或刷新令牌时视为可供请求或续期使用。</summary>
	private static bool HasUsableToken(OAuthCredential credential)
	{
		if (string.IsNullOrWhiteSpace(credential.AccessToken))
		{
			return !string.IsNullOrWhiteSpace(credential.RefreshToken);
		}
		return true;
	}

	/// <summary>根据 OAuth 凭据域名返回对应 OAuth 站点标识。</summary>
	private static string RealmFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "cn";
		}
		return "global";
	}

	/// <summary>判断凭据域名是否属于 WorkBuddy 国际站。</summary>
	private static bool IsGlobal(OAuthCredential credential)
	{
		return NormalizeDomain(credential.Domain)?.Contains("workbuddy.ai", StringComparison.OrdinalIgnoreCase) ?? false;
	}

	/// <summary>根据账号所属站点选择聊天和模型 API 根地址。</summary>
	private static string BaseFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "https://copilot.tencent.com";
		}
		return "https://www.workbuddy.ai";
	}

	/// <summary>根据账号所属站点选择套餐/余额 API 根地址。</summary>
	private static string BillingBaseFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "https://www.codebuddy.cn";
		}
		return "https://www.workbuddy.ai";
	}

	/// <summary>返回国内或国际站的资源额度查询路径。</summary>
	private static string BillingResourcePathFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "/v2/billing/meter/get-user-resource";
		}
		return "/billing/meter/get-user-resource";
	}

	/// <summary>返回账号所属站点的每日签到接口路径。</summary>
	private static string DailyCheckinPathFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "/v2/billing/meter/daily-checkin";
		}
		return "/billing/meter/daily-checkin";
	}

	/// <summary>返回账号所属站点的活跃报告接口路径。</summary>
	private static string ReportPathFor(OAuthCredential credential)
	{
		if (!IsGlobal(credential))
		{
			return "/v2/report";
		}
		return "/report";
	}

	/// <summary>从 OAuth 流程显式选择的 `cn`/`global` realm 解析授权服务根地址。</summary>
	private static string BaseForRealm(string realm)
	{
		if (!string.Equals(realm, "global", StringComparison.OrdinalIgnoreCase))
		{
			return "https://copilot.tencent.com";
		}
		return "https://www.workbuddy.ai";
	}

	/// <summary>移除宿主模型标识中的可选平台名前缀，保留上游原始模型 ID。</summary>
	private static string NormalizeModel(string value)
	{
		return ParseModelReference(value).ModelId;
	}

	private static string QualifiedModelId(string realm, string modelId)
	{
		return realm + "/" + modelId;
	}

	private static bool MatchesRequestedRealm(Account account, AdapterRequest request)
	{
		string realm = ParseModelReference(request.Model).Realm;
		if (realm != null)
		{
			Credential credential = account.Credential;
			OAuthCredential val = (OAuthCredential)(object)((credential is OAuthCredential) ? credential : null);
			if (val != null)
			{
				return string.Equals(RealmFor(val), realm, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		return true;
	}

	private static WorkBuddyModelReference ParseModelReference(string value)
	{
		string text = (value.StartsWith("workbuddy/", StringComparison.OrdinalIgnoreCase) ? value.Substring("workbuddy".Length + 1) : value);
		int num = text.IndexOf('/');
		if (num > 0 && num < text.Length - 1)
		{
			string text2 = text.Substring(0, num).ToLowerInvariant();
			if ((text2 == "cn" || text2 == "global") ? true : false)
			{
				return new WorkBuddyModelReference(text2, text.Substring(num + 1));
			}
		}
		return new WorkBuddyModelReference(null, text);
	}

	/// <summary>将 OAuth 返回的 URL 或裸域名规范化为仅包含主机名的值。</summary>
	private static string? NormalizeDomain(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		string text = value.Trim().TrimEnd('/');
		if (Uri.TryCreate(text, UriKind.Absolute, out Uri result))
		{
			return result.Host;
		}
		return text.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("http://", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('/');
	}

	/// <summary>将追踪 ID 规整为 16/32 位小写十六进制；不符合格式时生成新的 ID。</summary>
	private static string NormalizeTraceId(string value)
	{
		string text = new string(value.Where((char character) => Uri.IsHexDigit(character)).ToArray()).ToLowerInvariant();
		int length = text.Length;
		if ((length != 16 && length != 32) || 1 == 0)
		{
			return Guid.NewGuid().ToString("N");
		}
		return text;
	}

	/// <summary>限制来自 OAuth 上游的账号 ID 长度和字符集，避免保存异常标识。</summary>
	private static bool IsSafeAccountId(string? value)
	{
		if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128)
		{
			return value.All((char character) =>
			{
				bool flag = char.IsLetterOrDigit(character);
				if (!flag)
				{
					bool flag2 = ((character == '-' || character == '.' || character == '_') ? true : false);
					flag = flag2;
				}
				return flag;
			});
		}
		return false;
	}

	/// <summary>从宿主 JSON 请求体读取指定字段，兼容字符串和可转成文本的简单 JSON 值。</summary>
	private static string? ReadBodyString(object? body, string name)
	{
		if (!(body is JsonElement { ValueKind: JsonValueKind.Object } jsonElement) || !jsonElement.TryGetProperty(name, out var value))
		{
			return null;
		}
		if (value.ValueKind != JsonValueKind.String)
		{
			return value.ToString();
		}
		return value.GetString();
	}

	/// <summary>将插件契约对象转换为可编辑 JSON 节点；已有 JSON 节点会先深复制。</summary>
	private static JsonNode? ToJsonNode(object? value)
	{
		if (value == null)
		{
			return null;
		}
		if (value is JsonNode jsonNode)
		{
			return jsonNode.DeepClone();
		}
		if (value is JsonElement jsonElement)
		{
			return JsonNode.Parse(jsonElement.GetRawText());
		}
		return JsonSerializer.SerializeToNode(value);
	}

	/// <summary>从 JSON 对象按候选字段顺序读取第一个存在的值文本。</summary>
	private static string? ReadNodeString(JsonNode? node, params string[] names)
	{
		if (!(node is JsonObject jsonObject))
		{
			return null;
		}
		foreach (string propertyName in names)
		{
			if (jsonObject.TryGetPropertyValue(propertyName, out JsonNode jsonNode) && jsonNode != null)
			{
				return jsonNode.ToString();
			}
		}
		return null;
	}

	private static bool IsSessionDeadError(string value)
	{
		if (!value.Contains("Offline user session not found", StringComparison.OrdinalIgnoreCase) && !value.Contains("12153", StringComparison.Ordinal))
		{
			return ContainsBusinessCode(value, 12153);
		}
		return true;
	}

	private static bool IsPermanentAccountFault(string value)
	{
		if (!ContainsBusinessCode(value, 11140))
		{
			if (value.Contains("request illegal", StringComparison.OrdinalIgnoreCase))
			{
				return !ContainsBusinessCode(value, 11128);
			}
			return false;
		}
		return true;
	}

	private static bool IsTrialNotActivated(string value)
	{
		if (!value.Contains("trial not activated", StringComparison.OrdinalIgnoreCase))
		{
			return ContainsBusinessCode(value, 14017);
		}
		return true;
	}

	private static bool IsHardCreditError(int statusCode, string value)
	{
		if (statusCode != 402 && !ContainsBusinessCode(value, 14018))
		{
			return HardCreditErrorMarkers.Any((string marker) => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
		}
		return true;
	}

	private static bool ContainsBusinessCode(string value, int code)
	{
		try
		{
			return ContainsBusinessCode(JsonNode.Parse(value), code);
		}
		catch (JsonException)
		{
			return false;
		}
	}

	private static bool ContainsBusinessCode(JsonNode? node, int code)
	{
		if (node is JsonObject jsonObject)
		{
			if (ReadNodeLong(jsonObject, "code", "error_code", "errorCode") != code)
			{
				return jsonObject.Any((KeyValuePair<string, JsonNode> property) => ContainsBusinessCode(property.Value, code));
			}
			return true;
		}
		if (node is JsonArray source)
		{
			return source.Any((JsonNode item) => ContainsBusinessCode(item, code));
		}
		return false;
	}

	/// <summary>将候选 JSON 字段文本解析为 64 位整数。</summary>
	private static long? ReadNodeLong(JsonNode? node, params string[] names)
	{
		if (!long.TryParse(ReadNodeString(node, names), out var result))
		{
			return null;
		}
		return result;
	}

	/// <summary>积分允许小数及数字字符串，避免将小数余额误读为零。</summary>
	private static decimal? ReadNodeDecimal(JsonNode? node, params string[] names)
	{
		if (!decimal.TryParse(ReadNodeString(node, names), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return null;
		}
		return result;
	}

	/// <summary>将候选 JSON 字段解析为布尔值；无法识别时返回空值。</summary>
	private static bool? ReadNodeBoolean(JsonNode? node, params string[] names)
	{
		if (!bool.TryParse(ReadNodeString(node, names), out var result))
		{
			return null;
		}
		return result;
	}

	/// <summary>尝试从 JSON 对象的指定属性读取 32 位整数。</summary>
	private static bool TryReadInt(JsonObject node, string name, out int value)
	{
		return int.TryParse(ReadNodeString(node, name), out value);
	}

	/// <summary>优先将响应正文解析为 JSON；正文不是 JSON 时将原文本包装为 JSON 字符串。</summary>
	private static JsonNode? ParseJsonOrText(string value)
	{
		try
		{
			return JsonNode.Parse(value);
		}
		catch (JsonException)
		{
			return JsonValue.Create(value);
		}
	}

	/// <summary>提取 SSE `data:` 行的有效负载；其他字段行和注释行返回空值。</summary>
	private static string? SseData(string line)
	{
		if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return line.Substring(5).Trim();
	}

	private static string? ReadSseError(JsonNode? root, string? eventName)
	{
		if (!(root is JsonObject jsonObject))
		{
			return null;
		}
		JsonNode jsonNode = jsonObject["error"];
		if (jsonNode == null && !string.Equals(eventName, "error", StringComparison.OrdinalIgnoreCase) && !string.Equals(ReadNodeString(jsonObject, "type"), "error", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		return ReadNodeString(jsonNode, "message", "msg") ?? ((jsonNode is JsonValue) ? jsonNode.ToString() : null) ?? ReadNodeString(jsonObject, "message", "msg") ?? "WorkBuddy upstream stream failed";
	}

	private static bool IsContentBlockedResponse(int statusCode, string body)
	{
		if (statusCode >= 400)
		{
			if (!body.Contains("blocked by security policy", StringComparison.OrdinalIgnoreCase) && !body.Contains("unapproved channel", StringComparison.OrdinalIgnoreCase))
			{
				return body.Contains("illegal api invocation", StringComparison.OrdinalIgnoreCase);
			}
			return true;
		}
		return false;
	}

	private static JsonObject CreateDegradedChatRequestBody(JsonObject requestBody)
	{
		JsonObject jsonObject = (JsonObject)requestBody.DeepClone();
		if (!(jsonObject["messages"] is JsonArray jsonArray))
		{
			return jsonObject;
		}
		JsonArray jsonArray2 = new JsonArray
		{
			new JsonObject
			{
				["role"] = "system",
				["content"] = "You are a helpful assistant. Respond in the user's language, follow the user's instructions, and be direct and concise."
			}
		};
		foreach (JsonNode item in jsonArray)
		{
			string a = ((item is JsonObject node) ? ReadNodeString(node, "role") : null);
			if (!string.Equals(a, "system", StringComparison.OrdinalIgnoreCase) && !string.Equals(a, "developer", StringComparison.OrdinalIgnoreCase))
			{
				jsonArray2.Add(item?.DeepClone());
			}
		}
		jsonObject["messages"] = jsonArray2;
		return jsonObject;
	}

	/// <summary>读取聊天完成响应的第一个 choice；空数组或格式不符时返回空值。</summary>
	private static JsonNode? FirstChoice(JsonNode? root)
	{
		if (!(root is JsonObject jsonObject) || !(jsonObject["choices"] is JsonArray { Count: >0 } jsonArray))
		{
			return null;
		}
		return jsonArray[0];
	}

	/// <summary>读取 OpenAI 兼容 usage 的 prompt、completion 和 total token 数。</summary>
	private static Usage? ReadUsage(JsonNode? root)
	{
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Expected Obj, but got Unknown
		if (!(root?["usage"] is JsonObject node))
		{
			return null;
		}
		return new Usage(ToInt(ReadNodeLong(node, "prompt_tokens").GetValueOrDefault()), ToInt(ReadNodeLong(node, "completion_tokens").GetValueOrDefault()), ToInt(ReadNodeLong(node, "total_tokens").GetValueOrDefault()));
	}

	/// <summary>读取标准工具调用增量或旧式 function_call，并统一转换为宿主工具增量类型。</summary>
	private static List<ToolCallDelta> ReadToolCallDeltas(JsonNode? node)
	{
		//IL_0140: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Expected Obj, but got Unknown
		if (node is JsonObject jsonObject && jsonObject["tool_calls"] is JsonArray source)
		{
			return (from call in source.Select((JsonNode call, int position) => ReadToolCallDelta(call, position))
				where call != null && (!string.IsNullOrWhiteSpace(call.Id) || !string.IsNullOrWhiteSpace(call.Name) || !string.IsNullOrWhiteSpace(call.Arguments))
				select (call)).ToList();
		}
		if (node is JsonObject jsonObject2 && jsonObject2["function_call"] is JsonObject node2)
		{
			string text = ReadNodeString(node2, "name");
			string text2 = ReadNodeString(node2, "arguments");
			if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(text2))
			{
				return new List<ToolCallDelta>();
			}
			int num = 1;
			List<ToolCallDelta> list = new List<ToolCallDelta>(num);
			CollectionsMarshal.SetCount(list, num);
			CollectionsMarshal.AsSpan(list)[0] = new ToolCallDelta(0, ReadNodeString(node2, "id"), "function", text, text2);
			return list;
		}
		return new List<ToolCallDelta>();
	}

	/// <summary>解析单个工具调用片段，缺少显式 index 时使用其在数组中的位置。</summary>
	private static ToolCallDelta? ReadToolCallDelta(JsonNode? node, int position)
	{
		//IL_00a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a6: Expected Obj, but got Unknown
		if (!(node is JsonObject jsonObject))
		{
			return null;
		}
		JsonObject node2 = jsonObject["function"] as JsonObject;
		return new ToolCallDelta((int)(ReadNodeLong(jsonObject, "index") ?? position), ReadNodeString(jsonObject, "id"), ReadNodeString(jsonObject, "type") ?? "function", ReadNodeString(node2, "name"), ReadNodeString(node2, "arguments"));
	}

	/// <summary>读取成长任务平铺或 progress 对象中的次数字段。</summary>
	private static long ReadTaskCount(JsonObject task, string property)
	{
		return ReadNodeLong(task["progress"] as JsonObject, property) ?? ReadNodeLong(task, property).GetValueOrDefault();
	}

	/// <summary>筛选尚未锁定、接受或领取的成长任务，并返回去重后的任务代码。</summary>
	private static List<string> ReadTaskCodes(JsonNode? data)
	{
		if (!(data?["tasks"] is JsonArray source))
		{
			return new List<string>();
		}
		return (from item in (from item in source.OfType<JsonObject>()
				where ReadNodeBoolean(item, "locked") != true
				select item).Where((JsonObject item) =>
			{
				string text = ReadNodeString(item, "accept_status");
				return !(text == "accepted") && !(text == "claimed");
			})
			select ReadNodeString(item, "task_code") into item
			where !string.IsNullOrWhiteSpace(item)
			select (item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>根据 7d、14d 或 28d 档位读取对应的连登奖励状态。</summary>
	private static string? TierStatus(JsonObject? redemption, string? tier)
	{
		return tier?.ToLowerInvariant() switch
		{
			"7d" => ReadNodeString(redemption, "tier_7d_status"), 
			"14d" => ReadNodeString(redemption, "tier_14d_status"), 
			"28d" => ReadNodeString(redemption, "tier_28d_status"), 
			_ => null, 
		};
	}

	/// <summary>筛选已完成但尚未领取的任务，并返回可尝试领取的去重任务代码。</summary>
	private static List<string> ReadClaimableTaskCodes(JsonNode? data)
	{
		if (!(data?["tasks"] is JsonArray source))
		{
			return new List<string>();
		}
		return (from item in source.OfType<JsonObject>()
			where ReadNodeBoolean(item, "locked") != true
			where !string.Equals(ReadNodeString(item, "accept_status"), "claimed", StringComparison.OrdinalIgnoreCase)
			where string.Equals(ReadNodeString(item, "status"), "completed", StringComparison.OrdinalIgnoreCase) || (ReadTaskCount(item, "target") > 0 && ReadTaskCount(item, "current") >= ReadTaskCount(item, "target"))
			select ReadNodeString(item, "task_code") into item
			where !string.IsNullOrWhiteSpace(item)
			select (item)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	/// <summary>将 64 位计数安全收窄为 32 位值，并在越界时饱和到边界。</summary>
	private static int ToInt(long value)
	{
		if (value <= int.MaxValue)
		{
			if (value >= int.MinValue)
			{
				return (int)value;
			}
			return int.MinValue;
		}
		return int.MaxValue;
	}
}
