# QQ 机器人插件（htaci.qqbot）所需的宿主接口

> 来源：`D:\Project\qqplugin`（插件侧已在跑，QQ 官方 v2 API 接入、斜杠命令、插件会话已通）。
> 这份文档只列**宿主需要补的接口**，不含插件侧实现。
> 编写依据：读过的宿主源码（路径见各条「落点」）。凡是「我推测」的地方都写了「待确认」。
> 最后更新：见文件末尾。

---

## 零、速览

| # | 需求 | 建议接口 | 优先级 |
| --- | --- | --- | --- |
| 1 | 会话用量（输入/输出/缓存/次数/上下文占用）+ 模型窗口 | `ISessionFacade.GetUsageAsync` + `SessionSummary` 加两列 | **P0** |
| 2 | 模型能力（是否支持视觉，决定图片直传还是转路径） | `ISessionFacade.GetModelInfoAsync`（可与 1 合并） | **P0** |
| 3 | 读写会话的工具集（插件会话默认零工具，等于干不了活） | `ListAvailableToolsAsync` / `GetSessionToolsAsync` / `SetSessionToolsAsync` | **P0** |
| 4 | 交互桥：把工具审批 / 提问 / 等用户操作转发到聊天软件 | `SetInteractionAsync(sessionId, IPluginInteractionHost)` | **P0** |
| 5 | 派发归属：`RoundCompleted` 认不出是哪次派发 | `ChatEvent.TurnId`（+ `IsPluginInitiated`） | **P0** |
| 6 | 工作区：列表、列会话、建会话 | `ListWorkspacesAsync` / `ListWorkspaceSessionsAsync` / `CreateWorkspaceSessionAsync` | P1 |
| 7 | 压缩时保留最近 N 轮 | `CompressAsync(sessionId, keepRecentTurns)` | P1 → ✅ 已实现 |
| 8 | 插件工具自定义超时（等用户回 `/true` 会超 5 分钟） | `PluginToolDefinition.TimeoutSeconds`（等人另走交互桥 `WaitForUserAsync`） | P1 → ✅ 已实现 |
| 9 | 消息快照带 usage / turnId / model | `SessionMessageSnapshot` 加字段 | P2（可被 1 覆盖） |

前 5 条是「插件现在真的跑不动」的硬缺口，6–8 是功能完整性，9 是便利性。

---

## 一、实现前先看：已确认的现状

以下都是读过代码确认的，不是推测。行号可能随改动漂移，用名字找。

| 事实 | 位置 |
| --- | --- |
| `ISessionFacade` 现有 9 个方法（Send / Cancel / SetPermissionMode / Compress / GetRecentMessages / ListSessions / CreateSession / ListMySessions / DeleteSession） | `HtaciAI.Plugin.Abstractions/ISessionFacade.cs` |
| `SessionMessageSnapshot(Role, Content, Thinking, Status, SequenceNumber, CreatedAt)` —— **没有 usage、没有 turnId** | 同上 |
| `SessionSummary(SessionId, Title, UpdatedAt)` —— 只有三项 | 同上 |
| `PluginSession` 表里**已经有** `Model / Thinking / EnabledToolIds / EnabledSkills / McpServers / ToolPermissionMode / SystemPrompt` | `Models/PluginSession.cs` |
| 插件会话的工具与 MCP 默认为空，且**没有任何写入路径** | `Services/Plugins/PluginSessionFacade.cs`（`CreateSessionCoreAsync`） |
| 上下文用量的口径**已经写好了** | `ConversationRuntime.TryGetContextUsage(out used, out total)`；`MaybeAutoCompressAsync` 用它 |
| `ContextWindow` 每轮按请求模型刷新，来源 `ModelDetails.ContextWindow` | `ConversationRuntime.cs`（`ContextWindow = request.Settings.Model.ContextWindow`） |
| 模型能力：`ModelDetails.ContextWindow / MaxOutputTokens / Capabilities / SupportsArrayContent`；视觉判定 = `Capabilities.Contains("vision")` | `Services/Clients/ChatGateway.cs`、`ConversationRuntime.SupportsVision` |
| 用量已落库：`chat_message.usage_json` = `{input_tokens, cache_hit_tokens, output_tokens, raw}` | `ChatGateway.BuildUsageJson`、`Services/Clients/TokenUsage.cs` |
| 交互出口是**单个属性**，为空就按「用户不在」 | `ConversationRuntime.Interaction`（`IInteractionHost?`），装配处 `_toolExecutor.ApprovalGate = Interaction is { } host ? ... : Task.FromResult(false)` |
| `ChatEvent` 有 `SessionId / SequenceNumber / Text / Status / Success`，**没有 TurnId** | `HtaciAI.Plugin.Abstractions/IChatEventBus.cs` |
| 工作区仓储只在宿主内部 | `Data/WorkspaceRepository.GetAllAsync/GetAsync`、`Data/WorkspaceSessionRepository.GetByWorkspaceAsync/CreateAsync` |
| 压缩范围固定「最后一条压缩记录 → 末尾」 | ~~`ContextCompression.SliceForSummary` 没有保留轮数参数~~ → 现已支持 `keepRecentTurns`（第 8 节落地）；`BuildWireHistory` 的 `keepRecentTurns` 只影响**发送**，两者不要混 |
| 插件工具的超时字段 | ~~没有，固定 5 分钟~~ → 已加 `PluginToolDefinition.TimeoutSeconds`（`null` = 宿主默认 5 分钟、`0` = 一直等）；用户在工具页单独配的时长优先 | `HtaciAI.Plugin.Abstractions/IPluginTools.cs`、`Services/Plugins/PluginToolFacade.cs` |

---

## 二、逐条接口需求

### 1. `GetUsageAsync` —— 会话用量（P0）

**动机**：QQ 侧 `/context` 要显示「总输入 / 总输出 / 调用次数 / 缓存命中 / 命中率 / 上下文占用%」；
`/task list` 每条会话要显示「上下文用量百分比」。插件侧**完全拿不到**这些数：
`GetRecentMessagesAsync` 的快照不含 usage，`SessionSummary` 不含窗口与用量。

**建议签名**（`ISessionFacade` 追加）：

```csharp
Task<SessionUsage?> GetUsageAsync(string sessionId, CancellationToken ct = default);

public sealed record SessionUsage(
    string ModelId,             // 会话存档里的模型 id（可能为空 = 用宿主默认）
    long ContextWindow,         // <= 0 表示未知（模型没声明）
    long ContextUsed,           // 当前占用；口径复用 ConversationRuntime.TryGetContextUsage
    long InputTokens,           // 累计（所有有 usage 的消息求和）
    long CachedInputTokens,     // 累计缓存命中
    long OutputTokens,          // 累计
    int RequestCount,           // 有 usage 的消息条数 ≈ API 往返次数
    long UpdatedAt);            // 最后一条有 usage 的消息时间
```

- 命中率、cache_miss 由插件自己算（`cache_miss = input - cached`，与 `TokenUsage` 一致），宿主不必给。
- 「上下文占用」照抄 `TryGetContextUsage` 的两条口径（普通 assistant 取 `TotalTokens`、压缩记录取 `OutputTokens`），
  **不要新写一套**，否则界面与 QQ 报出的百分比会对不上。
- `SessionUsage` 命名/字段可改，但请保留「窗口 + 占用」两个数，否则百分比算不出来。

**实现落点**：`Services/Plugins/PluginSessionFacade.cs`。

**实现要点（重要）**：
- **不要**为了查用量去 `Acquire` 运行时。现在 `GetRecentCoreAsync` 会 `Acquire + EnsureReadyAsync`，
  等于把整个会话读进内存；`/task list` 一次要问 10 个会话，会白白拉起 10 份运行时。
  用量直接走库：`ChatRepository.GetBySessionAsync(sessionId)` 遍历 `UsageJson` 求和即可。
- 但 `TryGetContextUsage` 在 `ConversationRuntime` 上。两条路可选：
  (a) 把这段口径抽成 `static` 帮助类（推荐，`ContextCompression` 那类的位置），宿主的运行时与门面共用；
  (b) 门面里复制一份 —— 不推荐，两处口径迟早分叉。

### 2. `SessionSummary` 加两列（P0，可与 1 合并）

**动机**：`/task list -n` 每条要「名称 / id / 最近使用时间 / 上下文用量%」。逐个调 `GetUsageAsync`
是 N+1 次查询，且会话多的时候明显卡。

**建议**：`SessionSummary` 增可选参数（追加在末尾，保持位置构造函数兼容）：

```csharp
public sealed record SessionSummary(
    string SessionId, string Title, long UpdatedAt,
    long ContextUsed = 0, long ContextWindow = 0);
```

`ListSessionsAsync` / `ListMySessionsAsync` 顺手填上（同一批 SQL 里算，或按 session 分组聚合
`usage_json`）。取不到就给 0，插件按「未知」显示。

### 3. `GetModelInfoAsync` —— 模型能力（P0）

**动机**：图片分流。模型支持视觉 → 直接把图当图像输入；不支持 → 落成文件路径交给 `view_image`。
插件现在无从判断 `Capabilities` 里有没有 `vision`，也无法知道上下文窗口。

**建议签名**：

```csharp
Task<SessionModelInfo?> GetModelInfoAsync(string sessionId, CancellationToken ct = default);

public sealed record SessionModelInfo(
    string ModelId, string DisplayName,
    long ContextWindow, long MaxOutputTokens,   // -1 = 未知
    bool SupportsVision,
    bool SupportsThinking,
    bool IsFallbackToHostDefault);              // true = 会话没选模型，走的是宿主默认
```

会话没选模型时按 `AppDefaults.ResolveDefaultModelAsync()` 回退，与 `PluginSessionFacade.ResolveModelAsync` 现有逻辑一致。

> 如果嫌接口多：把 `ContextWindow` / `SupportsVision` 揉进 `SessionUsage` 也能用，
> 但 `/login` 后还没发过言的会话拿不到 usage，仍然需要一条「只问模型」的路。**建议保留独立方法。**

### 4. 工具集读写（P0）

**动机**：插件会话默认 `EnabledToolIds` 为空 —— 绑上去的 QQ 用户面对一个**没有任何工具**的 Agent，
什么都干不了。QQ 侧要 `/tools ls`、`/tools enable|disable <id|all>`，
还要按权限档位算白名单（严格=无工具、1=安全、2=+风险、3=+危险）。

**建议签名**：

```csharp
// 全部可登记的工具（不含 IsInternal 的哨兵工具）
Task<IReadOnlyList<PluginToolInfo>> ListAvailableToolsAsync(CancellationToken ct = default);
// 某个会话当前勾选的工具 id；会话不存在返回 null
Task<IReadOnlyList<string>?> GetSessionToolsAsync(string sessionId, CancellationToken ct = default);
// 覆盖式写入；只接受本插件自己的会话（plugin- 前缀 + 归属校验），其他会话返回 false
Task<bool> SetSessionToolsAsync(string sessionId, IReadOnlyList<string> toolIds, CancellationToken ct = default);

public sealed record PluginToolInfo(
    string Id, string Name, string? Description,
    PluginToolDangerLevel DangerLevel,   // 复用契约里已有的枚举，别新造
    string Source);                      // builtin / plugin / mcp
```

**实现落点**：`PluginSessionFacade` + `Services/Tools` 的注册表（`ToolSource.Plugin` / 内置 / MCP 那套）。

**为什么限制「只能改自己的会话」**：`PluginSession.EnabledToolIds` 是插件会话自己的字段；
普通会话 / 工作区会话的 `EnabledToolIds` 是**用户在界面上的选择**，插件改它等于悄悄替用户做决定。
（顺带一条既有隐患：现在 `SetPermissionModeAsync` 对**任意**会话都生效，QQ 插件为跑工具把它设成
`Loose` 会改掉桌面用户手选的档位。建议同样收紧为「只能改自己的会话」，或至少给一个
`SetPermissionModeAsync(..., onlyOwnSessions: true)` 的显式说法 —— 待你定。）

### 5. 交互桥（P0，最关键的一条）

**动机**：宿主的规则是「没有窗口时审批一律拒绝、提问返回 null」。QQ 用户**永远没有窗口**，
于是：

- 任何 `Risk` / `Danger` 工具在 QQ 侧都会被自动拒绝；
- `ask_user_question` 永远得到「用户不在」；
- `ask_user_continue`（等用户去扫码/输验证码）永远得到 null。

结果是 QQ 机器人只能跑只读工具。「让 AI 在手机上问一句、用户回一句」这件事目前做不到。

**建议签名**（新契约接口 + 门面方法）：

```csharp
// IPluginContext 不变；新增能力 conversation.interaction（或并入 chat.control）
Task<bool> SetInteractionAsync(string sessionId, IPluginInteractionHost? host);

public interface IPluginInteractionHost
{
    Task<bool> ApproveAsync(PluginToolApprovalRequest request, CancellationToken ct);
    Task<IReadOnlyList<PluginAskAnswer>?> AskAsync(
        IReadOnlyList<PluginAskQuestion> questions, CancellationToken ct);
    Task<bool?> WaitForUserAsync(string title, string instructions, CancellationToken ct);
}

public sealed record PluginToolApprovalRequest(
    string SessionId, string ToolId, string ToolName,
    string ArgumentsJson, PluginToolDangerLevel DangerLevel);
```

**语义（建议写进 XML 注释）**：

| 场景 | 谁接 |
| --- | --- |
| 会话窗口开着（有人看着界面） | **优先给界面**（现状不变，避免两处同时弹） |
| 没有窗口，但插件登记了交互桥 | 给插件（QQ 侧发一条系统消息等回复） |
| 都没有 | 现状：审批拒绝、提问 null |

- 插件侧一定会有超时（用户可能永不回复），所以 `ct` 必须能把请求取消掉；
  超时由插件自己管（`WaitForUserAsync` 等 5 分钟就回 false）。
- 桥的调用必须是「不阻塞 UI 线程的 await」——`ConversationRuntime.Interaction` 本来就是异步的，
  直接把门面套上去即可。
- 归属：只接受本插件会话；对别人的会话登记一律返回 false。

**为什么不做成「插件自己注册两个工具代替 ask/wait」**：那是可行的降级方案（见第四节），
但它**替不掉审批**——`bash`、`write` 这些工具要不要跑，是宿主的闸门在问，插件插不进去。

### 6. `ChatEvent.TurnId`（P0）

**动机**：QQ 侧要「派发任务到某会话 → 等它跑完 → 把结果发回 QQ」。现在 `RoundCompleted` 只带
`SessionId`，如果那条会话同时在桌面上跑着一轮（或者插件连续派了两个任务），
**无法判断这个完成事件属于谁**，结果会串到别的任务上。

**建议**：`ChatEvent` 增加两个字段（`init` 属性，追加不影响既有代码）：

```csharp
public int? TurnId { get; init; }            // chat_message.turn_id，轮次标识
public bool IsPluginInitiated { get; init; } // 这一轮是否由插件经 ISessionFacade 发起
```

- `TurnId` 与 `chat_message.turn_id` 一致，插件用 `GetRecentMessagesAsync` 也能对上（前提是第 9 条给了 turnId）。
- `IsPluginInitiated` 可以被插件用来「只认自己派的那一轮」，比 turnId 更直接。
  两者都给最好；只给一个的话，`TurnId` 更有用。
- 轮次结束事件（`RoundCompleted` / `RoundFailed`）必须带。

**落点**：`Services/Plugins/ChatEventBus.cs` 里构造事件的地方；`TurnId` 从 runtime 当前轮取。

### 7. 工作区（P1）

**动机**：QQ 侧 `/task work -w <id>` 要在某个工作区里新建会话跑任务（工作区才有工具、MCP、模型）。

**建议签名**（可放在 `ISessionFacade` 或新接口 `IWorkspaceFacade`，建议后者，语义清楚）：

```csharp
Task<IReadOnlyList<PluginWorkspaceInfo>> ListWorkspacesAsync(CancellationToken ct = default);
Task<IReadOnlyList<SessionSummary>> ListWorkspaceSessionsAsync(
    string workspaceId, int take = 10, CancellationToken ct = default);
Task<string?> CreateWorkspaceSessionAsync(
    string workspaceId, string? title = null, CancellationToken ct = default);

public sealed record PluginWorkspaceInfo(
    string Id, string Name, string? Path,
    int SessionCount, long UpdatedAt);
```

**实现落点**：`Data/WorkspaceRepository.GetAllAsync`、`Data/WorkspaceSessionRepository.GetByWorkspaceAsync/CreateAsync`
（都有现成的）。新建会话继承工作区的 `Model / EnabledToolIds / EnabledSkills / McpServers / ToolPermissionMode`，
与用户在界面上「在工作区里新建会话」的行为一致（可参照 `Views/ConversationPage.axaml.cs` 那段）。

### 8. `CompressAsync` 重载：保留最近 N 轮（P1）—— ✅ 已实现

**动机**：QQ 侧 `/compact -n` 要「压缩倒数 N 轮之前的记录，保留最后 N 轮」。

**建议签名**：

```csharp
Task<PluginCompressResult> CompressAsync(string sessionId, int keepRecentTurns,
    CancellationToken ct = default);

public sealed record PluginCompressResult(bool Compressed, int SummarizedMessages, string? Error);
```

**实现要点**：
- 复用 `ContextCompression.SliceForSummary(Messages)` 得到「最后一条压缩记录 → 末尾」，
  再按 **user 消息数** 从尾部砍掉 `keepRecentTurns` 轮（与 `BuildWireHistory` 里
  `roundCount - keepRecentTurns` 同一套数法）。`keepRecentTurns <= 0` 等同于现有行为。
- 现有 `CompressAsync(sessionId)` 保持不动，内部转调 `CompressAsync(sessionId, 0)`。
- `CompressResult` 是宿主内部类型，别直接暴露给契约（契约不能引宿主程序集）。

**实际落地（2026-11）**：

- 签名按契约惯例给默认值，没有另开重载：`Task<bool> CompressAsync(string sessionId, int keepRecentTurns = 0)`。
  旧的 `CompressAsync(sessionId)` 调用点不用改（源码兼容），内部就是 `keepRecentTurns = 0`。
- 返回值仍是 `bool`，没有引入 `PluginCompressResult`：`false` 覆盖「压了没成功」「没有可压内容」两种情况，
  需要细节就再查一次 `GetRecentMessagesAsync` / `GetUsageAsync`，不为一个统计值再加一个契约类型。
- 切点在**第 N 条 user 消息**（从末尾往回数），与 `BuildWireHistory` 数轮次的口径一致：
  一条 user 消息 = 一轮的开头，所以不会切出「有 tool 结果没有它的调用」那种半轮。
- 可压内容不足 N 轮时返回 `false`，**什么都不压**：宁可不动，也不把半轮历史塞进摘要再从原文里删掉。
- 落点：`ContextCompression.SliceForSummary(messages, keepRecentTurns)` /
  `HasCompressibleContent(messages, keepRecentTurns)` → `ConversationRuntime.CompressAsync(manual, keepRecentTurns, ct)`
  → `PluginSessionFacade.CompressCoreAsync`。界面上的「压缩」仍传 0（行为不变）。

### 9. `PluginToolDefinition.TimeoutSeconds`（P1）—— ✅ 已实现

**动机**：插件工具原来固定 5 分钟封顶。跑得久的工具（起服务、装包、全量测试）会被腰斩，
而失败时模型只会看到一条「执行超时」，没有别的办法。

**落地（2026-11）**：`PluginToolDefinition` 增加

```csharp
public int? TimeoutSeconds { get; init; }
```

- `null`（默认）= 跟内置工具一样 5 分钟；
- `0` = 一直等，不设超时；
- `>0` = 秒数，上限 24 小时（超了按上限算并记日志），负数按默认处理（也记一条日志）。
- 宿主 `PluginToolRegistry.Register` 把它落到 `ToolDefinition.TimeoutSeconds` 上，
  由已有的 `ToolExecutorDispatcher.ResolveTimeoutSeconds` 统一生效 ——
  用户在「工具」页给这个工具单独配的时长**优先于**插件声明的值（那是用户对「允许多久」的明确意思）。
- 插件工具的 schema 里若声明了 `timeout` 参数，模型仍可为单次调用抬高，取「声明的」与「入参的」较大者。

**仍然保留的结论：等人别用工具**。工具是被模型调用的，超时、取消、失败都是宿主的语义，
拿它当「等用户按按钮」的通道，模型会看到一条「超时失败」，而人可能一分钟后才回。第 5 条做好的交互桥
（`SetInteractionAsync` + `IPluginInteractionHost.WaitForUserAsync`）才是这件事的正主：

- 插件自己决定等多久、怎么提示、超时后回什么，宿主不掺和；
- 不占一轮工具调用，也不会因为「超时失败」把模型带进重试循环；
- 桥是「窗口优先」的，用户在桌面上操作时不会被机器人抢走交互。

两条一起用才对：`/true` `/false` 走桥，跑得久的真工具在定义里声明 `TimeoutSeconds`。

### 10. `SessionMessageSnapshot` 扩展（P2）

```csharp
public sealed record SessionMessageSnapshot(
    string Role, string? Content, string? Thinking,
    string Status, int SequenceNumber, long CreatedAt,
    int TurnId = 0, long InputTokens = 0, long CachedInputTokens = 0,
    long OutputTokens = 0, string? ModelName = null);
```

有了它，插件可以「自己算用量」而不依赖第 1 条；也能把「哪条消息属于第几轮」对上。
**位置构造函数加参数会破坏二进制兼容**，但插件都是随宿主重编译的，风险可控；
如果不想动，就只做第 1、2 条。

---

## 三、明确**不**建议开放的能力

| 能力 | 为什么不开 |
| --- | --- |
| 让插件创建「普通会话」（`chat_sessions` 一行） | QQ 用户建的会话会出现在用户的会话列表里，用完还得由插件清理；插件会话（`plugin_sessions`）已经是隔离的，够用。**若 QQ 侧确实要「普通会话」语义，请复用它，别开新口子。** |
| 让插件改**非自有**会话的工具集 / 系统提示词 / 模型 | 那是用户在界面上的选择，插件改它等于悄悄替人做主（现有 `SetPermissionModeAsync` 建议一并收紧，见第 4 条） |
| 插件读「某个会话的完整历史」（超过 500 条） | 没有真实需求；真要做「上下文报告」用第 1 条的聚合数就够了 |
| 插件直接写库 / 直接拿 `ChatRepository` | 绕过权限与租约（会话可能正在跑），插件侧以前用反射干过，已弃用 |

---

## 四、如果只想改最小面积（替代方案）

按「宿主改动量」从大到小：

1. **全量（推荐）**：第 1–6 条。QQ 侧能完整工作：看用量、配工具、审批走 QQ、派发可归属。
2. **只做 5 + 6**：插件会话**不配工具**、一切任务都派发到工作区会话执行，QQ 会话只当指挥台。
   这样能跑，但 `/tools`、`/context` 这两个命令做不出来。
3. **只做 1 + 2**：只能做「看」，看不到执行 —— `/context`、`/tools ls` 能出数据，但工具仍然跑不了。
4. **一条都不改**：QQ 侧只能聊天（纯文本），所有工具自动拒绝。现状就是这个状态。

第 5 条（交互桥）是分水岭：有它，QQ 才是一个能干活的前端；没有它，那只是个聊天框。

---

## 五、验收建议

宿主仓库里已有成熟的无头 harness 写法（见 `plugin-handoff.md` 第三节末尾），这几条都能这么测：

| 接口 | 验收 |
| --- | --- |
| 1 / 2 | 造一条有 `usage_json` 的会话，`GetUsageAsync` 的累计与 `TryGetContextUsage` 的占用和界面显示一致（不一致就是又抄了一份口径）；`ContextWindow <= 0` 时插件能拿到「未知」而不是 0 |
| 2 | 未发过言的插件会话也能问到模型信息（走默认模型回退） |
| 3 | 只列非 internal 工具；`SetSessionToolsAsync` 对别人的会话返回 false 且不写库；写入后下一轮请求的 tools 数组里真的出现（用假 OpenAI 服务把请求体 dump 出来断言） |
| 4 | 无窗口 + 登记了桥：`bash` 的审批走到插件、插件返回 true 时工具真的执行；插件返回 false / 抛异常 / 超时 → 工具得到「被拒绝」，这一轮照常结束；窗口打开时**界面优先**（插件不应收到） |
| 5 | 同一会话先由插件派发一轮、再由「界面」发起一轮：`RoundCompleted` 的 `TurnId` / `IsPluginInitiated` 能区分开 |
| 6 | 工作区建会话后模型与工具集继承正确；`ListWorkspaceSessionsAsync` 按 `LastMessageAt` 倒序 |
| 7 | `keepRecentTurns = 2` 时，最后 2 轮的原文不出现在摘要输入里（断言摘要请求体）；`0` 与旧行为一致 |
| 8 | 插件登记时声明 `TimeoutSeconds = 90`：让执行体睡 120 秒，断言第 90 秒就收到「执行超时（>90 秒）」；`TimeoutSeconds = 0` 时工具能睡过 5 分钟不被切断；负数与 >24 小时的声明按默认 / 上限处理且日志有记录；工具页给同一工具配 600 时以 600 为准。交互桥另测：`WaitForUserAsync` 等超过 5 分钟仍能拿到用户回复 |

---

## 六、坑与兼容性

1. **别为了查询拉起运行时**：`Acquire + EnsureReadyAsync` 会读全量历史。用量/工具/工作区这类只读查询走库。
2. **UI 线程**：`PluginSessionFacade` 里读库的方法（`ListSessionsCoreAsync`）是**直接在线程池上跑的**，
   只有要碰 `ConversationRuntime` 的才 `OnUiThreadAsync`。新方法照这个分。
3. **口径只有一份**：上下文占用、cache_miss 的定义都别复制。`TryGetContextUsage` / `TokenUsage` 已经写好了。
4. **契约版本**：新增 record 参数一律追加在末尾并给默认值；`ChatEvent` 用 `init` 属性而不是位置参数，
   这样旧的构造点不用全改。
5. **能力声明**：新增接口挂在新能力（`conversation.interaction` / `workspace.delegate`）下，
   与现有 `chat.control` 的注入方式一致 —— 没声明就是 `null`，让插件作者第一行就发现漏了。
6. **`plugin_sessions` 的 id 前缀**：插件会话是 `plugin-`；`SessionProfileResolver` 靠前缀分表。
   第 3/4/6 条的「只能改自己的会话」建议用「行里的 `PluginId` 等于调用方」判断，**不要**靠前缀（前缀不分插件）。

---

## 七、变更记录

| 日期 | 改动 |
| --- | --- |
| 2026-11 | 初版：QQ 机器人插件（`htaci.qqbot`）提交的接口需求清单 |
| 2026-11 | 宿主已实现 P0 全套（第 1–6 条），落地情况与三处偏离见第八节 |
| 2026-11 | 第 7 条实现（`CompressAsync(sessionId, keepRecentTurns)`）；第 8 条改为不走插件工具、用交互桥 |
| 2026-11 | 第 8 条改为实现（`PluginToolDefinition.TimeoutSeconds`）：按插件作者要求补上，其他插件也可能需要跑得久的工具；「等人」仍走交互桥 |

---

## 八、宿主实现回执（P0 已落地）

| 需求 | 落地方式 |
| --- | --- |
| 1 用量 | `ISessionFacade.GetUsageAsync` + `SessionUsage`。只读库（新加 `ChatRepository.GetUsageMessagesAsync`，只捞带 usage 的行），不 `Acquire` 运行时 |
| 2 列表用量 | `SessionSummary` 追加 `ContextUsed` / `ContextWindow`（末尾默认值，源码兼容）；`ListSessionsAsync` / `ListMySessionsAsync` / `ListWorkspaceSessionsAsync` 都已填 |
| 3 模型能力 | `GetModelInfoAsync` + `SessionModelInfo`（含 `IsFallbackToHostDefault`） |
| 4 工具集 | `ListAvailableToolsAsync` / `GetSessionToolsAsync` / `SetSessionToolsAsync` + `PluginToolInfo`。写入只认「行里的 `PluginId` 等于调用方」，运行时有就先改运行时档案 |
| 5 交互桥 | `SetInteractionAsync` + `IPluginInteractionHost`（审批 / 提问 / 等人三件）。**窗口优先**：桥挂在 `ConversationRuntime.PluginInteraction` 上，视图的 `Interaction` 有值时桥收不到 |
| 6 派发归属 | `ChatEvent.TurnId` + `IsPluginInitiated`，只挂在 `RoundCompleted` / `RoundFailed` 上 |
| 6' 工作区 | 新接口 `IWorkspaceFacade`（`Workspaces`，能力 `workspace.delegate`）：列工作空间 / 列会话 / 新建会话 |

**三处与本文档不同的决定**

1. `ChatEvent.TurnId` 是 **`string?`**，不是 `int?`：`chat_message.turn_id` 在宿主里是字符串（历史数据里还有 GUID 形态的旧值），强转整数会丢对不上的轮次。
2. 交互桥**没有**新增 `conversation.interaction` 能力，而是挂在 `chat.control` 下（`SetInteractionAsync` 就是 `ISessionFacade` 的成员）。少一个要插件声明的常量，也不会出现「声明了 chat.control 却没有交互桥」的半残状态。
3. `SetInteractionAsync` 接受**任何真实存在的会话**，不只插件自己的会话：第 6' 条允许插件在工作空间里建会话派任务，那些会话同样需要一个「问用户」的出口，按归属收紧会把这个用法直接堵死。桥本身不改配置、不影响有窗口时的行为，风险落在「插件知道会话 id」上 —— 而插件在本进程里本来就有完整信任。

**仍未做**：第 9/10 条（快照扩展，P2）。
第 7 条（`CompressAsync` 保留最近 N 轮）已实现，见第 8 节「实际落地」；
第 8 条（`PluginToolDefinition.TimeoutSeconds`）已实现，见第 9 节 —— 插件工具可声明自己的时长，
但**等人仍走交互桥的 `WaitForUserAsync`**，两条各管一件事。
`SetPermissionModeAsync` 按你的选择**不收紧**，仍对任意会话生效。

