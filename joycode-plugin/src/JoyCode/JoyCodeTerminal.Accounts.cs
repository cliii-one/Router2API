using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace JoyCode;

/// <summary>JoyCodeTerminal 的管理端点：模型发现、账号校验和连接测试。</summary>
public sealed partial class JoyCodeTerminal
{
    /// <summary>测试 JoyCode 连接是否可用（调上游模型列表，验证 ptKey 有效性）。</summary>
    [PluginEndpoint("GET", "models/discover")]
    public async Task<PluginResult> DiscoverModelsAsync(PluginHttpContext context)
    {
        var credentials = await host.Accounts.ListAsync(Platform);
        if (credentials.Count == 0)
            return context.Json(200, new { ok = false, error = "没有已保存的 JoyCode 账号" });

        foreach (var account in credentials)
        {
            if (account.Credential is not CustomCredential custom || !HasUsableCredentials(custom))
                continue;
            try
            {
                var url = RequestURL(custom, "/api/saas/models/v1/modelList");
                var body = PrepareBody(custom);
                using var request = BuildRequest(custom, HttpMethod.Post, url, body);
                request.Headers.Add("Accept-Encoding", "gzip, deflate");
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var response = await client.SendAsync(request);
                var content = await response.Content.ReadAsStringAsync();
                if (response.IsSuccessStatusCode)
                {
                    var data = JsonNode.Parse(content)?["data"];
                    if (data is JsonArray models)
                    {
                        var list = new JsonArray();
                        foreach (var m in models.OfType<JsonObject>())
                            list.Add(new JsonObject
                            {
                                ["label"] = m["label"]?.DeepClone(),
                                ["chatApiModel"] = m["chatApiModel"]?.DeepClone(),
                                ["maxTotalTokens"] = m["maxTotalTokens"]?.DeepClone(),
                            });
                        return context.Json(200, new { ok = true, models = list });
                    }
                }
                return context.Json(200, new { ok = false, error = $"上游返回 {response.StatusCode}", body = content[..Math.Min(content.Length, 300)] });
            }
            catch (Exception ex)
            {
                return context.Json(200, new { ok = false, error = ex.Message });
            }
        }
        return context.Json(200, new { ok = false, error = "所有账号的凭证都不可用" });
    }

    /// <summary>验证 JoyCode Custom 凭证的连接性（管理页"测试连接"按钮）。</summary>
    [PluginEndpoint("POST", "connection/test")]
    public async Task<PluginResult> TestConnectionAsync(PluginHttpContext context)
    {
        try
        {
            var body = context.Body switch
            {
                string s => JsonNode.Parse(s),
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                _ => null,
            };
            var ptKey = body?["ptKey"]?.GetValue<string>();
            var userId = body?["userId"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(ptKey) || string.IsNullOrWhiteSpace(userId))
                return context.Json(400, new { ok = false, error = "缺少 ptKey 或 userId" });

            var credential = new CustomCredential(new Dictionary<string, string?>
            {
                ["ptKey"] = ptKey,
                ["userId"] = userId,
            });
            var url = RequestURL(credential, "/api/saas/user/v1/userInfo");
            var request = BuildRequest(credential, HttpMethod.Post, url, PrepareBody(credential));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var response = await client.SendAsync(request);
            var content = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return context.Json(200, new { ok = true, message = "连接成功" });
            return context.Json(200, new { ok = false, error = $"上游返回 {(int)response.StatusCode}", body = content[..Math.Min(content.Length, 200)] });
        }
        catch (Exception ex)
        {
            return context.Json(200, new { ok = false, error = ex.Message });
        }
    }
}
