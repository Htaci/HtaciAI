# API 请求统计（`api_requests` 与「统计」页）

新标签页 → **统计**：每一次出站 API 往返的用量、金额、耗时与失败原因。

## 一句话架构

一行 = 一次 HTTP 往返。写入在客户端装饰器里（`Services/Clients/ApiRecordingChatClient`），
读取在 `Data/ApiRequestRepository`，展示在 `Views/ApiStatsPage`。中间没有预汇总表。

```
ChatGateway ─┐
压缩/总结    ├─ IChatClient ─ ApiRecordingChatClient ─ 真实客户端（OpenAI / Anthropic / …）
话题命名     │                     │
连接测试    ─┘                     └─→ api_requests（一次往返一行）
```

## 为什么另开一张表，不拿 `chat_message.usage_json` 算

| 原因 | 具体后果 |
|---|---|
| 不是每次请求都有消息 | 话题命名、上下文压缩、连接测试都发请求，但没有 `chat_message` 行 |
| 消息会被复制与删除 | 编辑重发、重新生成、软删会让同一份用量重复或消失，直接加总会算错 |
| 缺关键维度 | 服务商、HTTP 状态码、首字延迟、错误类型都不在消息里 |
| 拿不到重试 | 自动重试的每次尝试都真实发出去了，消息上只有最后一次的结果 |

## 表结构

```sql
CREATE TABLE api_requests (
    id               TEXT PRIMARY KEY,
    kind             TEXT NOT NULL,          -- chat / title / compress / probe / other
    status           TEXT NOT NULL,          -- ok / failed / cancelled
    attempt          INTEGER NOT NULL DEFAULT 1,
    session_id       TEXT, turn_id TEXT, message_id TEXT,
    provider_name    TEXT, model_id TEXT, model_name TEXT, protocol TEXT, endpoint TEXT,
    http_status      INTEGER,                -- 成功固定 200；连不上/超时为 NULL
    error_kind       TEXT,                   -- network / timeout / http / quota / auth / other
    error_message    TEXT,
    input_tokens     INTEGER,                -- NULL = 这次没拿到用量，不是 0
    cache_hit_tokens INTEGER,
    output_tokens    INTEGER,
    cost             REAL,                   -- 写入时的价格快照
    currency         TEXT,
    ttft_ms          INTEGER,                -- 首字延迟；NULL = 一个字都没收到
    duration_ms      INTEGER,
    message_count    INTEGER, tool_count INTEGER,
    started_at       INTEGER NOT NULL
);
CREATE INDEX idx_api_req_started ON api_requests (started_at DESC);
CREATE INDEX idx_api_req_session ON api_requests (session_id, started_at);
CREATE INDEX idx_api_req_model   ON api_requests (model_name, started_at);
```

迁移号：**V30**（`DatabaseService.RunMigrationV30`）。

### 刻意不存的东西

- **请求体 / 响应体**：体积，且里面是用户的全部对话内容。
- **api_key**。
- **endpoint 的 query 串**：有的中转站把密钥塞在 URL 里，落库前统一剥到 path。

### 两处容易算错的口径

1. **token 用 NULL 不用 0。** 0 会混进 `AVG()` 与「平均用量」，把失败请求算成"这次没花钱"。
2. **cost 是快照，不是现算。** 模型价格在「模型服务」里可以随时改，历史金额必须留在写入那一刻的
   `AiModel.PriceInput / PriceCacheHit / PriceOutput` 上。缓存命中价没填时退回输入价
   （宁可高估，也不凭空给出一个好看的便宜数）。

## 写入点

`ModelCatalog.BuildClient` 统一返回包好装饰器的客户端 —— 只有这一处可能漏，
增删调用点时不用去找。装饰器位置在 **`RetryingChatClient` 之内**：

```
RetryingChatClient（每次尝试）→ ApiRecordingChatClient（记一行）→ 真实客户端
```

于是自动重试的每一次尝试各记一行，`attempt` 由重试装饰器写在 `options.ApiCall.Attempt` 上。
统计页上「用途」后面带 `#2` 的就是第二次尝试。

### 上下文怎么传

`ChatRequestOptions.ApiCall`（`ApiCallContext`）挂一份可变上下文：种类、会话、轮次、消息 id、尝试序号。

| 调用点 | kind |
|---|---|
| `ChatGateway.ChatAsync` | `chat`（每轮补 `MessageId`） |
| `ConversationRuntime.CompressAsync` / 回复总结 | `compress` |
| `ConversationRuntime.GenerateTitleAsync` | `title` |
| `ConnectionProbe.CheckCompletionAsync` | `probe` |

**写库失败、统计关闭、格式异常一律不影响对话**：`ApiRequestRepository.InsertAsync` 自己吞异常，
装饰器在关闭时连计时都不做，直接透传。落库走 `Task.Run`，不往 UI 线程排 SQLite 写。

## 设置

「设置 → 常规设置 → API 请求统计」：

- **记录开关，默认开**（`AppSettingsData.ApiRequestLogEnabled`）。关掉只是停止写入，已有历史不删。
- **保留策略，默认 365 天**（`ApiRequestLogRetentionDays`）：30 / 90 / 180 / 365 / 永久（0）。
  超期记录在**启动时清一次**（`App.OnFrameworkInitializationCompleted`），改设置时也立刻清一次。
- 统计页右上「清空记录」调 `ClearAsync`，有确认框。

## 统计页

`Views/ApiStatsPage.axaml(.cs)`，入口是新标签页的「统计」卡片（`NewTabPage.StatsSelected` →
`MainWindow.OnStatsSelected` → `ReplaceTabContent`）。

- 时间范围：今天 / 7 天 / 30 天 / 90 天 / 全部。「今天」与「N 天」都按**本地零点**切，
  与用户看到的日期一致（SQL 里用 `date(started_at/1000,'unixepoch','localtime')`）。
- 五条聚合（总览、按天、按模型、按用途、最近 100 条）并发跑，每次切范围重算一次。
- 趋势图是**手绘柱状图**：一根柱子一个定高 `Border`，要回答的只有「哪几天忙」。
  项目没有图表库，也不要为此引一个。
- 失败率的分母是全部请求；「已取消」单独列一格 —— 用户点停止不算失败。
- 表格是手绘的（项目没有 DataGrid）：`ApiStatsPage.Table` 里列宽只声明一次，所有行共用。

## 已知取舍

- **不做日汇总表**：一年重度使用约几十万行、几十 MB，带索引的 SQLite 现算得起这五条 GROUP BY。
  真到了算不动的量级，再加 `api_requests_daily` 并保留本表作为原始流水。
- **不记录「列模型」请求**（`ModelDiscovery` 自己发 HTTP，不走 `IChatClient`）。它不花 token，
  属于配置操作；要统计的话得单独接一条路径。
- **只记流式对话请求**。将来若新增非流式接口（如 embeddings），需要在 `IChatClient` 上补一条记录路径。
