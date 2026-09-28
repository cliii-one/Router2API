# JoyCode 插件（Router2API）

将 JoyCode（京东 AI 编程助手）的多模型账号接入 Router2API。

## 支持的模型

| 模型 | 上下文 | 最大输出 | 推理 | 视觉 |
|------|--------|----------|------|------|
| JoyAI-Code | 200K | 64K | - | - |
| Claude-Opus-4.7 | 200K | 32K | - | - |
| MiniMax-M2.7 | 200K | 16K | ✓ | - |
| Kimi-K2.6 | 200K | 16K | ✓ | ✓ |
| Kimi-K2.5 | 200K | 16K | - | ✓ |
| GLM-5.1 | 200K | 16K | ✓ | - |
| GLM-5 / GLM-4.7 | 200K | 8K | - | - |
| Doubao-Seed-2.0-pro | 200K | 16K | - | - |

## 安装步骤

### 1. 从 JoyCode IDE 提取凭证

JoyCode 的凭据存在本地 SQLite 数据库里：

| 系统 | 路径 |
|------|------|
| macOS | `~/Library/Application Support/JoyCode/User/globalStorage/state.vscdb` |
| Windows | `%APPDATA%\JoyCode\User\globalStorage\state.vscdb` |
| Linux | `~/.config/JoyCode/User/globalStorage/state.vscdb` |

用任意 SQLite 工具打开，执行：

```sql
SELECT value FROM ItemTable WHERE key='JoyCoder.IDE';
```

在返回的 JSON 里找到 `joyCoderUser` 节点，记录 `ptKey` 和 `userId`。

### 2. 在 Router2API 添加账号

管理后台 → 账号管理 → 平台选 `joycode` → 凭证类型 `Custom`，填入：

```json
{
  "ptKey": "你的 ptKey",
  "userId": "你的 userId"
}
```

可选字段：`tenant`（默认 JOYCODE）、`colorBaseURL`（默认 https://api-ai.jd.com）、`loginType`、`orgFullName`。

### 3. 调用

```bash
curl http://<router>:5242/v1/chat/completions \
  -H "Authorization: Bearer <apikey>" \
  -H "Content-Type: application/json" \
  -d '{"model":"joycode/GLM-5.1","messages":[{"role":"user","content":"你好"}]}'
```

## 上游协议说明

上游协议参考 JoyCode2Api（https://github.com/vibe-coding-labs/JoyCode2Api）逆向实现：

- **端点**：`https://api-ai.jd.com/api?appid=joycode_ide&functionId=<id>&t=<ms>&sign=<hmac>`
- **签名**：HMAC-SHA256，密钥 `0691a3f0b37b4a85aeb63ad0fc7db3ed`，规范串 `joycode_ide&<functionId>&<timestamp>`
- **认证**：请求头 `ptKey` + `loginType`
- **SSE**：上游返回 OpenAI 兼容格式，插件 raw 透传
- **Claude 模型**：走 `/api/saas/anthropic/v1/messages` 专用通道

## 构建

```bash
cd src/JoyCode
dotnet build -c Release
# 产物在 bin/Release/net10.0/，把 JoyCode.dll + plugin.json 复制到宿主 plugins/joycode/
```

打包发行 ZIP 时排除 `Router.Contracts.*`（宿主自带）。

## 免责声明

仅供个人学习和技术研究使用。禁止用于商业转售、API 中转服务、大规模薅号或任何违法违规活动。本项目不是 JoyCode 官方产品。
