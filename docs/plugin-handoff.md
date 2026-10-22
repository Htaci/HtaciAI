# 开发交接快照

> 用途：上下文压缩 / 换人接手时，只读这一份就能接着干。
> 最后更新：2026-10（对应提交 `M7`）

---

## 零、当前状态（读这一段就够开工）

**已完成**：M0（插件加载/卸载/崩溃归因）、M1（`ConversationRuntime` + `ConversationRuntimeRegistry`，
A/B/C 三步全完）、M2（`PromptComposer` + `IChatEventBus`）、M3（`ISessionFacade`）、
M4（窗口宿主 + 界面插槽 + 安全壳）、M7（插件工具 + 清单依赖字段 + 内置开发文档）、
M6（插件管理页）、工具超时与「继续」那一轮。

**提交线**（从旧到新）：

```
b0325ac  M6 插件管理页
03f989b  M1-A  会话状态搬进 ConversationRuntime
2c7a96c  M1-B  一轮对话的执行搬进 runtime，视图改成事件驱动渲染
0bdda5f  工具超时与停止（默认 5 分钟，停止时如实写回结果）
f625c5d  M1-C  重新生成/重试/继续/压缩搬进 runtime
cd7c9c4  M1 收尾：ConversationRuntimeRegistry，一个会话一份运行时
85f91d8  交接文档更新到 M2 开工点
2209bab  M3：插件能主动发起对话（ISessionFacade）
4b3409a  M2：上下文贡献 + 会话事件总线
cb47997  M4：窗口宿主 + 界面插槽 + 插件控件安全壳
（M7）    插件工具 + 清单依赖字段 + 内置开发文档
```

**下一步：M5（桌宠插件本体）**。底层能力已全部齐备，见「三、待办」。
工作区干净，`dotnet build HtaciAI/HtaciAI.csproj -t:Rebuild` 0 错误、6 个既有告警。

**关键能力现在到哪了**：

| 能力 | 状态 |
| --- | --- |
| 插件能不能加载/禁用/崩溃归因 | ✅ M0 |
| 插件能不能在界面上被看见（列表/启停/崩溃记录） | ✅ M6 |
| 插件能不能在会话没打开时发起对话 | ✅ M1（runtime）+ M3（`ISessionFacade` 门面） |
| 插件能不能停止一轮 / 改权限档位 / 压缩 / 读历史 | ✅ M3 |
| 插件能不能观察会话流（流式增量、轮次完成） | ✅ M2（`IChatEventBus`） |
| 插件能不能往上下文里加东西 | ✅ M2（`PromptComposer` + `IPromptRegistry`） |
| 插件能不能建窗口 / 插控件 | ✅ M4（`IPluginWindowHost` + `IUiExtensionRegistry`） |
| 插件能不能提供工具给模型 | ✅ M7（`IToolRegistryApi` + `ToolSource.Plugin`） |
| 清单能不能声明平台/前置/互斥 | ✅ M7（含拓扑加载、循环依赖、连带停用） |
| 用户能不能在界面里看到开发文档 | ✅ M7（插件页小字入口 → `<数据根>/doc/`） |
| 桌宠插件本体 | ⬜ M5（M2/M3/M4 已全部就绪） |


---

## 一、已完成并验证

### 1. 请求自动重试（提交 `9cc5fad`）

- `HtaciAI/Services/Clients/RetryingChatClient.cs`：`IChatClient` 装饰器
- 一次 API 往返最多 5 次尝试，工具循环里每次请求各算一份（**不是**整轮任务共享）
- 只重试：连不上 / 连接被掐 / 读流中断 / 超时 / 408 / 425 / 429 / 5xx
- 不重试：400 / 401 / 403 / 404 / 422，以及文案含 `insufficient` `quota` `余额` `额度` 的 402/429
- **已经向界面吐过分片就不再重试**（正文与工具卡边收边追加，无法回滚）
- 退避 600ms / 1.4s / 3s / 5s，等待受 `ct` 管辖；输入区显示「正在重试 n/4」

### 2. 失败后可手动重试且保留思考链

- `ChatStreamException`：把失败前已收到的正文与思考交还网关
- 网关把部分内容写进那条 `failed` 的 assistant 消息；错误记录保存**原始异常**（标题才有「HTTP 502」）
- `ErrorCard` 增加「重试」按钮（实时与历史错误卡都有）
- `ConversationView.RetryTurnAsync`：不删任何消息，取失败轮之前的历史重发

### 3. 会话窗口唯一性（修「任务未结束却显示已完成」）

- `SessionViewCache` 改为全进程共用一份（`Shared`），一个会话只有一个窗口
- 运行中 / 等待确认的会话窗口**钉住**不被淘汰（`CollectExpired` 的 `isPinned`）
- 任务结束时 `TouchSession` 让保留期从「跑完」重新计时
- 正在显示的窗口每轮刷新时间戳
- 跨标签页打开同一会话时从原宿主摘下（`DetachFromOtherHost`），原页面切回时按会话 id 重新挂上

### 4. AI 自举（创建工具 / 技能）

- 内置工具 `create_tool` / `create_skill`，**危险等级由程序写死**，不接受模型传参
- 工具名 `^[a-z][a-z0-9_]{1,63}$`，与已有工具重名直接拒绝
- 脚本路径由代码推导 `<数据根>/tools/<name>.py|js`，不接受绝对路径
- 上限：脚本 64 KB、schema 16 KB、技能正文 24 KB；schema 校验根为 `type=object`
- 内置技能 `tool-authoring` / `skill-authoring` 作为自举指引
- 设置项 `AllowAgentSelfExtension`（默认开）；关掉时从请求的 `tools` 数组过滤
- 迁移 V21：`tool_tools.created_by`；AI 建的显示「AI 创建」标签
- 工具页等级徽标可点改等级；审批卡片把 `script` / `body` 按真实换行单独展示
- **建好不会自动启用**，需要用户到工具页勾选

### 5. 插件系统 M0（提交 `6e7ca37` + 修复 `29bb33b`）

契约 `HtaciAI.Plugin.Abstractions`（不引用 Avalonia）：

| 成员 | 说明 |
| --- | --- |
| `IPlugin` | `InitializeAsync(IPluginContext, ct)` / `ShutdownAsync(ct)` |
| `IPluginContext` | `PluginId` `PluginDirectory` `Manifest` `HostVersion` `Log` `Storage` `Resources` |
| `IPluginStorage` | `LoadText` / `SaveText`（**推荐**）+ `Load<T>` / `Save<T>`（会导致需重启） |
| `PluginManifest` | `plugin.json` 显式字段绑定；`capabilities` 点分标识 |
| `PluginCapabilities` | `prompt.contribute` `chat.observe` `chat.control` `ui.window` `ui.slot` `tools.provide` |

宿主侧 `HtaciAI/Services/Plugins/`：

| 文件 | 职责 |
| --- | --- |
| `PluginManager.cs` | 扫描、校验、加载、卸载、启用/禁用、崩溃策略 |
| `PluginLoadContext.cs` | 可回收 ALC；共享宿主与 Avalonia 程序集；**流加载** |
| `PluginContext.cs` | 上下文实现（日志 / 存储 / 资源） |
| `PluginJson.cs` | 泛型存储 API 用的 JSON（注明其卸载代价） |
| `PluginLog.cs` | `logs/plugin.log`，2 MB 轮转 |
| `PluginBreadcrumb.cs` | 崩溃前写「谁/哪个阶段」，返回后清 |
| `PluginCrashReporter.cs` | 运行标记、崩溃日志、下次启动归因 |
| `PluginStateStore.cs` | `plugins/state.json`（启用、崩溃计数、autoLoadAfterCrash） |
| `PluginInfo.cs` | 给界面的不可变快照 + 状态文案 |

其他接线：

- `AppPaths`：新增 `Plugins` / `PluginData` / `Logs`
- `AppSettingsData.EnablePlugins`（**默认关闭**）
- 常规设置页新增「插件」卡片：总开关（切换即 `ReloadAsync`）、打开目录、运行状态
- `App.OnFrameworkInitializationCompleted`：`ShutdownMode = OnMainWindowClose`；
  `ShutdownRequested` 改成「取消一次 → 收插件 → 收 MCP → 删运行标记 → 再 Shutdown」
- 示例插件 `samples/HtaciAI.Plugin.Sample`（含 `plugin.json` 与 README）

### 6. 插件管理页（M6）

- `Views/PluginsPage.axaml(.cs)`：新标签页 →「插件」卡片进入；左侧插件列表（状态点 + 短状态），
  右侧总开关 + 详情（能力清单、错误 / 需重启提示、崩溃记录与「查看崩溃日志」、目录入口）
- 总开关切走时调 `PluginManager.ReloadAsync()`；页面订阅 `PluginManager.Changed`（attach/detach 成对，
  切换标签页回来时重新刷新），刷新经 `Dispatcher.Post` 延后一拍，避免在管理器自己的卸载流程中间重建控件
- 启用疑似崩溃的插件会弹 `Views/PluginCrashWarningDialog.cs`：说明后果 + 「崩溃后仍自动加载」勾选（**默认不勾**）
- 常规设置页的插件卡片与插件页总开关双向同步（`Changed` → 静默回写 `IsChecked`，用 `_suppressSave` 挡住重入）
- 新标签页新增「插件」卡片（`puzzle_piece`，U+E9EA），主窗口按会话已有模式把它替换成插件页标签

### 7. `ConversationRuntime`（M1-A/B/C）

当时的现状：`ConversationView.axaml.cs`（约 3200 行）把「会话状态 / 请求执行 / 落库 / UI 渲染 / 交互等待」混在一起，
`ChatGateway.beginRound` 回调直接建控件。**「跑一轮对话」离不开一个打开的视图**，
所以插件无法在会话未打开时发起对话。

分三步，每步单独构建 + 单独回归：

- **A** ✅ 已完成：`Services/Conversations/ConversationRuntime.cs` 收拢了
  `Messages` / `Profile` / `Gateway` / `IsBusy` / `SendCts`；视图改用三个只读访问器
  （`Profile` / `Messages` / `Gateway`）加 `_runtime.IsBusy`、`_runtime.SendCts`，行为不变。
  已实测：启动、打开长会话、历史分段渲染、思考卡与 Tokens 汇总均正常。
- **B** ✅ 已完成：请求执行整体搬进 `ConversationRuntime`（`SendAsync` / `RunTurnAsync` /
  落库 / 自动话题命名 / 会话提醒），视图只剩订阅渲染：

  | 事件 | 视图侧的用途 |
  | --- | --- |
  | `RoundStarted` | 建本段回复的根容器与正文块（`RoundRender`） |
  | `ContentDelta` / `ThinkingDelta` / `ToolCallDelta` | 追加正文 / 建思考卡 / 建并更新工具卡 |
  | `MessageInserted` / `MessageUpdated` | 用户气泡、工具卡结果回填、错误卡 |
  | `BusyChanged` | 切换输入区发送/停止形态、停计时器、收重试提示 |
  | `TurnEnded`（**可 await**） | 收聚合卡与操作栏；重新生成时整体重排历史 |
  | `RetryScheduled` / `ActivityChanged` / `ToolCallsRepaired` | 重试提示、左侧列表状态、入参索引重建 |

  交互出口抽成 `IInteractionHost`（审批 / 提问 / 技能变更），视图实现它；
  没有宿主时按「用户不在」处理：审批拒绝、提问返回 null，工具链如实回给模型。
  （发请求前的自动压缩当时临时挂在 `BeforeRequest` 钩子上，C 步已经搬成正式成员。）

  已实测（本地假 OpenAI 兼容服务，不花钱）：普通流式、思考卡、工具循环 + 聚合卡、
  点停止（保留已收正文、无错误卡）、重试（旧轮保留、新轮追加）、
  重新生成（旧轮替换、位置正确）、自动话题命名。

- **C** ✅ 已完成：剩下的入口也搬进 runtime ——
  `RegenerateAsync` / `RetryAsync` / `ContinueAsync` / `CompressAsync`（含发请求前的自动压缩）
  与 `ReloadMessagesAsync`、`IsTurnResumable`、上下文用量口径。视图只剩「解析模型 → 调 runtime → 提示人话」。

  几个要点：

  | 东西 | 位置 | 为什么 |
  | --- | --- | --- |
  | `TurnSettings(Model, Client, Thinking, ToolIds)` | runtime | 模型解析是调用方的判断（视图读选择器，插件读自己的配置） |
  | `HistoryRebuildRequested`（事件） | runtime 发、宿主重绘 | runtime 只管数据，重绘归视图；压缩 / 重新生成 / 删轮次共用一条路 |
  | `TakePendingInsertSequence()` | runtime | 插入位置在「发起时」定下、「建控件时」才取用 —— 中间可能已经重绘过 |
  | `CompressingChanged`（事件） | runtime 发、宿主提示 | 「正在压缩上下文…」只有界面能给 |
  | `ContextWindow` | runtime 的可写属性 | 自动压缩的阈值判定要用它；每轮按请求里的模型刷新，换模型时宿主也可直接赋 |

  已用**无头回归**驱动 `ConversationRuntime`（不建任何界面，本地假 OpenAI 服务）跑完 26 项：
  工具循环、停止（保留已收正文、interrupted 落库、运行标记复位）、继续、重新生成
  （旧轮替换而非叠加、请求过重绘、新回复紧跟用户消息）、失败轮 + 重试（消息只增不减）、
  手动压缩（摘要落库）与「刚压过再压」的收敛。
  这一轮同时证明了 M1 的目的：**跑一轮对话不再需要打开的视图**。

### 8. `ConversationRuntimeRegistry`（M1 收尾）

`Services/Conversations/ConversationRuntimeRegistry.cs`：一个会话一份运行时，视图关掉也不销毁。

| 东西 | 说明 |
| --- | --- |
| `Acquire(sessionId, profileFactory)` | 借出运行时，返回 `ConversationRuntimeLease`（`IDisposable`）。有就复用，没有才用工厂新建——工厂只在新建时调用一次 |
| 引用计数 | 视图借一次、插件订阅时借一次；租约 `Dispose` 减一，重复 `Dispose` 安全 |
| 回收 | 归零**且没有正在跑的轮次**时立即丢掉。不设空闲计时器：数据比控件树便宜，但「什么时候会消失」必须可预测 |
| 跑着不丢 | 归零但 `IsBusy` 时先留着，等 `BusyChanged(false)` 再收。半路丢掉等于让调用方攥着一个之后再也找不到的会话 |

配套改动：

1. **读库交给 runtime**：`EnsureReadyAsync()`（载入档案 + 读一次全量历史 + 修一次残缺工具消息，幂等）
   搬进 `ConversationRuntime`；`SendAsync` / `RegenerateAsync` / `RetryAsync` / `ContinueAsync` /
   `CompressAsync` 进门先 `await` 它。视图的 `LoadMessagesAsync` 与 `_historyLoaded` / `_historyLoadTcs`
   全部删掉，改成 `RenderLoadedHistory()`（只画，不读）。插件在会话没打开时发起对话也不会拿到空上下文。
2. **视图可被丢弃**：`ConversationView` 实现 `IDisposable`，`Dispose` 里退订 runtime、清掉
   runtime 上指向本视图的两处（`Interaction` / `SummaryModelIdProvider`）、还租约。
   **订阅活到丢弃为止，而不是「离开可视树就退订」** —— 切到别的会话时窗口只是被摘下来，
   这一轮如果还在跑，后续增量必须继续画进它。
3. **`SessionViewCache.Evict` 顺带丢弃窗口**：只从字典删键而不丢窗口，那个窗口会被 runtime 的
   事件一直吊着 —— 缓存看着是空的，内存其实还在涨。
4. **跨标签页的「正在显示」登记**：缓存是**全进程**共用的，但「正在显示」是每个页面各自知道的。
   两个「智能对话」标签页各显示一个会话时，B 页的淘汰器会把 A 页正看着的那个当「很久没显示」清掉，
   清掉的后果不是「下次重新加载」，是那个窗口当场退订、界面从此不再更新。
   所以 `CollectExpired` 的 `keepKey` 参数换成了 `Claim(key, holder)` / `ReleaseHolder(holder)` 登记。
   页面离开可视树时把窗口交回缓存（`RightPanel.Content = null`），切回来按会话 id 重新挂
   —— 命中就原样挂上（滚动位置都在），没命中就重建。
5. **重新生成后的重排由 runtime 自己做**：`InsertMessagesAtAsync` 之后立即 `ReloadMessagesAsync()`。
   内存里的 `Messages` 原本还是「追加在末尾」的顺序，与库不一致；等宿主重绘的话，
   插件在后台重新生成时没有宿主，顺序错了就再没人纠正。
6. **一轮没有控件时补渲染**：`OnTurnEnded` 发现 `_activeTurnRoot` 为空（视图是这一轮跑到一半
   才挂上来的，或者它由插件在后台发起）就整体重绘 —— 否则流式增量没有落点，内容只在
   `Messages` 里，永远看不见。

已验证（无头，不建界面）：

- 同一会话两次借到同一份运行时；读库只读一次
- 没有视图也能跑完一轮并落库
- 还有租约时保留、归零即回收、重新借是新实例且重读历史
- 运行中归还时不回收，跑完才消失
- 两个会话互不影响；重复 `Dispose` 不会误减别人的引用

界面侧另验证：打开会话 → 切到另一个会话 → 切回来（历史与窗口都在）；
慢速流式跑到一半切走再切回（流式继续画进同一个窗口，跑完后操作栏与 Tokens 正常）；
切到「设置」标签页再切回来（窗口交回缓存后按 id 重新挂上，会话完整恢复）。

> 另外记下了插件清单的字段设想（平台独占、前置依赖、循环依赖与连带停用规则），
> 见 [`plugin-manifest.md`](./plugin-manifest.md)。清单继续用 JSON，不上 ini。

### 9. `ISessionFacade`（M3）

**目标**：插件能主动发起一轮（桌宠「点一下就开口」），并且能在会话没打开时跑完。

契约 `HtaciAI.Plugin.Abstractions/ISessionFacade.cs`：

| 成员 | 说明 |
| --- | --- |
| `SendAsync(sessionId, text, ct)` | 用会话当前配置跑一轮，返回 `SessionSendResult`（`Sent` / `Busy` / `NotFound` / `NoModel` / `Failed`） |
| `CancelAsync(sessionId)` | 等价于用户按「停止」，返回是否真的停了一轮 |
| `SetPermissionModeAsync(sessionId, PluginPermissionMode)` | 改挡位并落库 |
| `CompressAsync(sessionId)` | 等价于点「压缩」，返回是否真的压了 |
| `GetRecentMessagesAsync(sessionId, take, ct)` | 最近 N 条快照（`SessionMessageSnapshot`），最多 500 |

宿主实现 `HtaciAI/Services/Plugins/PluginSessionFacade.cs`：

1. **按 id 建档案**：先查 `chat_sessions`，再查 `workspace_sessions` + `workspaces`（两种会话共用一套 id 空间）。
2. **走注册表的租约**：`ConversationRuntimeRegistry.Shared.Acquire(...)` → `EnsureReadyAsync()` → `runtime.SendAsync(...)`。
   会话窗口开着时用的是同一份运行时，因此插件的这一轮会实时出现在窗口里。
3. **不改用户的选择**：一轮发送会把模型/思考/工具写回会话存档，所以只沿用 `Profile.State` 里的配置，
   不提供「临时指定模型」。会话没选过模型、或原模型被删了，才回退到宿主默认模型（`AppDefaults`）。
4. **禁用它自己的那一轮**：`Dispose` 时对「本插件发起、还在跑」的会话调 `Cancel()`；
   `_disposed` 之后一切调用返回 `Failed`。门面登记在 `PluginEntry.Subscriptions`，禁用插件时由宿主统一 Dispose。
5. **线程**：任意线程可调，内部 `CheckAccess()` 为假时 `Dispatcher.UIThread.Post`。

注入方式：`PluginManager.LoadAsync` 里只有清单声明了 `chat.control` 才 `new PluginSessionFacade`，
否则 `IPluginContext.Sessions` 是 **null**（不是空实现 —— 让作者第一行就发现「我忘了声明」）。

**与 `plugin-plan.md` 草案的差异**（都是主动收缩，理由记在这里）：

| 草案 | 实际 | 为什么 |
| --- | --- | --- |
| `SendOptions`（模型/思考/工具） | 不做 | 一轮发送会把 settings 写回会话存档，插件指定等于悄悄改掉用户在界面上的选择 |
| `GetRecentMessages` 同步 | 改成 `GetRecentMessagesAsync` | 会话可能没读过库，同步签名只能拿到空上下文 |
| `AppendNoticeAsync`（role=agent 旁注） | 不做 | `role=agent` 已经是上下文压缩摘要（`ContextCompression.CompressRole`），旁注要独立角色 + 视图渲染，等真有需求再做 |
| `MessageSnapshot.Origin = "plugin:<id>"` | 写 `PluginLog` | 加列要迁移数据库；审计入口是 `logs/plugin.log`（含插件 id 与会话 id） |

**已验证**（无头，不建界面，本地假 OpenAI 服务，15 项全过）：

- 未打开会话也能发起一轮并落库（user + assistant）
- 历史快照、第二轮追加
- 同会话并发被拒（`Busy`），竞态情况下也退回 `Busy`
- `CancelAsync` 停掉慢速流式：这一轮结束、不算成功，库里留下 `interrupted`
- **无视图时的工具审批**：模型请求 `bash`，审批按「用户不在」拒绝，工具结果如实回给模型，这一轮照常成功结束
- 权限档位改动落库、压缩生成摘要记录
- 工作空间会话也能发起一轮（双表解析生效）
- 会话不存在 → `NotFound`；插件停用后 → `Failed`
- 插件被禁用时，它发起、还没跑完的那一轮被停掉

**没验证**：插件从**非 UI 线程**调用（`Post` 分支）—— 无头进程没有消息循环，测不了；
真实应用的 UI 线程有消息循环，路径本身与 `SessionNotifier` 用的是同一套写法。
示例插件只确认「注入生效」并打日志，不主动发消息（每次启动都往会话塞一句会很打扰）。

### 10. `PromptComposer` + `IChatEventBus`（M2）

**目标**：插件能往上下文里加东西（`prompt.contribute`）、能看会话流（`chat.observe`）。
两者都接在 M1 的 runtime 上，**没有碰视图**。

契约（`HtaciAI.Plugin.Abstractions`）：

| 接口 | 说明 |
| --- | --- |
| `IPromptRegistry` / `IPromptContributor` | `Add(contributor)` 登记；`Order` 决定追加顺序，`BuildAsync(PromptContext, ct)` 返回要追加的文本 |
| `IChatEventBus` / `IChatEventSink` | `Subscribe(sink, filter)`；`OnEventAsync(ChatEvent, ct)` |
| `ChatEvent` / `ChatEventKind` / `ChatEventFilter` | 一条事件带全部字段（按 `Kind` 分流）；过滤按 sessionId + 事件种类 |

宿主侧：

| 文件 | 职责 |
| --- | --- |
| `Services/Plugins/PromptComposer.cs` | `ComposeAsync(profile, modelId, sessionId, ct)`；另有 `PluginPromptRegistry`（名单）、`PluginPromptUsage`（每插件最近一次字数）、`PluginPromptFacade` |
| `Services/Plugins/ChatEventBus.cs` | 挂载到每个 runtime、50 ms 合并、有界队列、后台投递；另有 `PluginChatFacade` |
| `PluginContext` 的 `PluginFacades` | 三个门面打包传进来，按声明注入，没声明就是 **null** |

几个要点：

| 东西 | 做法 | 为什么 |
| --- | --- | --- |
| 落点 | `ConversationRuntime.RunTurnAsync` 里原来那行 `Profile.BuildSystemPrompt` 换成 `PromptComposer.ComposeAsync` | 不把 profile 接口改成异步：它只是字符串拼接，改了要动接口、两个实现和「工作空间不能合流」的注释 |
| 配额 | 每插件 4096 字、单轮合计 8192 字；超了截断 + `PluginLog.Warn`；单贡献者 500 ms 超时；抛异常只跳过它 | 这个入口能悄悄改变模型行为，必须既有上限又有可见性 |
| 来源标记 | 每段贡献前写 `<!-- plugin: id -->` | 出问题时一眼看出是谁写的 |
| 挂载 | 注册表加 `RuntimeCreated` / `RuntimeRemoved`，总线订阅这两个事件转订阅 | runtime 是按需创建的，不挂就只能看见插件自己借过的会话 |
| 合并 | 每 runtime 一份累积缓冲，50 ms `Timer` 冲一次；`Text` = 累积正文，`Delta` = 本次新增 | 空闲时一个 tick 都不该有；插件拿到累积文本才不用自己拼 |
| 队列 | 256 条上限，满了优先丢最旧的 `ContentDelta` / `ThinkingDelta` | 下一个事件里会重新给出累积文本，丢增量不丢语义 |
| 投递 | 后台 Task + `SemaphoreSlim`，逐个 `try/catch` | 插件回调慢不能拖住模型输出，也不能把别人带崩 |
| 撤销 | 门面登记进 `PluginEntry.Subscriptions`；总线在订阅归零时整体卸载 | 「禁用插件能不能干净」的一半 |

**两个踩到的坑**（已修，别再犯）：

1. **`TurnEnded` 不只表示「一轮结束」**：自动话题命名完成后也会发一次
   （`TurnOutcome.TitleChanged == true`）。照发 `RoundCompleted` 的话插件会看到两次「完成」。
2. **`Task.WaitAsync(timeout)` 抛的是 `TimeoutException`**，不是 `OperationCanceledException`；
   只接后者会让「超时」在日志里显示成「贡献者代码抛异常」，误导排查方向。

**已验证**（两套无头 harness，不建界面，本地假 OpenAI 服务）：

- 上下文贡献：落在 system 末尾、带来源标记、字数被记到 `PluginInfo.ContributionChars`；
  单插件超配额截断到 4096；合计超配额时后来者被跳过 / 截断；抛异常与 500 ms 超时都只是少一段内容，
  这一轮照常发送成功
- 会话事件：合并后的增量拼接与最终回复**一字不差**；40 片突发只合并成 1 条事件；
  按 sessionId 过滤生效；退订后不再收到；runtime 被回收后重建仍能自动挂上；
  撤销（= 禁用插件）后贡献与事件同时停止
- 端到端（真 `PluginManager` + 真 `samples/HtaciAI.Plugin.Sample`）：
  插件贡献进入 system、插件日志里出现「一轮开始 / 这一轮完成」、禁用后既不再贡献也不再收事件、
  字数清零，并且**禁用后仍是「已禁用」而不是「需重启」**（说明新门面没有吊住插件程序集）

**没验证**：真实界面里插件页那一行字数文案（逻辑层已通）、插件长时间跑的事件量。


---

### 11. 窗口宿主 + 界面插槽（M4）

**目标**：插件能建自己的窗口、能往宿主界面里插控件、能在「新标签页」加一张入口卡片。

契约（`HtaciAI.Plugin.Abstractions/IPluginUi.cs`）。**从这里开始契约程序集引用 Avalonia**
（`Window` / `Control` 是跨进程内公开协议的一部分），插件不必自带 Avalonia 的 dll ——
加载器把 `Avalonia*` 一律解析到默认上下文（`PluginLoadContext`）：

| 类型 | 说明 |
| --- | --- |
| `IPluginWindowHost.CreateStandalone(WindowOptions)` | 建一个配好的窗口：透明 / 无边框 / 置顶 / 不进任务栏 / 可拖动 / 按「插件 id + 标题」记忆位置；宿主在禁用插件与退出时统一关闭 |
| `IUiExtensionRegistry.AddControl(slotId, factory, order)` | 往插槽里插控件；`Order` 小的排前面，同序按注册顺序 |
| `IUiExtensionRegistry.AddLauncherCard(LauncherCard)` | 「新标签页」入口卡片（外观由宿主决定，与内置卡片一套风格） |
| `PluginSlots` | 插槽 id 常量：`newtab.cards` / `conversation.header` / `conversation.input.side` / `sidebar.below` / `mainwindow.titlebar.right` |

宿主侧：

| 文件 | 职责 |
| --- | --- |
| `Services/Plugins/PluginWindowHost.cs` | 窗口参数、拖动、关闭、位置记忆（`plugin-data/<id>/window-state.json`，宿主自己的类型，不会吊住插件） |
| `Services/Plugins/PluginUiRegistry.cs` | 登记表（`SlotEntry` / `CardEntry`）+ `Changed` 事件 + `PluginUiFacade`（一个插件一个） |
| `Controls/PluginControlHost.cs` | 插槽安全壳：工厂抛异常 → 换成一行「插件 X 显示失败」；另有 `PluginSlotPanel.Fill/Subscribe` 收口「清空 → 逐个包壳 → 空则隐藏」 |

插槽落在哪：

| 插槽 | 宿主位置 | 空时 |
| --- | --- | --- |
| `newtab.cards` | `NewTabPage.CardsPanel`（内置卡片之后） | 无卡片自然不占位 |
| `conversation.header` | `ConversationView` 新增的第 0 行 | 整行 `IsVisible=false` |
| `conversation.input.side` | 输入框左侧一列（`Grid ColumnDefinitions="Auto,*"`） | 整列隐藏 |
| `sidebar.below` | `ConversationPage` 左侧导航第 2 行 | 整行隐藏 |
| `mainwindow.titlebar.right` | 标题栏右侧，「检查更新」左边 | 整块隐藏 |

**几条设计上的取舍**：

| 取舍 | 为什么 |
| --- | --- |
| 登记的是**工厂**不是控件实例 | 视图会被重建（会话窗口缓存淘汰后重进、标签页关掉重开），存控件只会拿到已经脱离可视树的旧对象。代价是工厂会被反复调用 —— 契约里写明了「工厂别存瞬时状态」 |
| 登记表变化 → `Changed` → 视图重建插槽 | 与 M1 的「数据归 runtime、重绘归视图」同一条原则：登记表只存数据，长什么样由视图决定 |
| 未知插槽 id 只记警告、返回 no-op | 抛出去会让整个插件加载失败，而「一个插槽名拼错」不该有这么大代价；日志里带着可用 id 列表 |
| 插件控件外面必须包一层安全壳 | 插件构造函数抛异常如果穿透到布局测量，坏的是整个页面（用户只看到「程序坏了」） |
| 关窗口放在「撤订阅」那一步 | 保证它一定发生在 `AssemblyLoadContext.Unload()` 之前；也在插件 `ShutdownAsync` 之后，插件不必操心「窗口已经没了」 |
| 位置记忆用宿主自己的类型 | 复用 `PluginJson`，但序列化的是宿主类型 —— 踩不到坑 2（反射序列化插件类型导致回收不掉） |

**已验证**（无头，`Avalonia.Headless` 真起了一个平台实例，所以窗口是真的建出来了）：

- 登记：五个插槽都能登记、`Order` 升序、单条撤销、整插件撤销后清空、已撤销的门面不再登记新控件
- 未知插槽不抛、只记警告
- 安全壳：正常工厂的控件被放进去；抛异常的工厂换成「插件 X 显示失败」，异常没穿透
- 无头界面冒烟：`CreateStandalone` 的属性（Topmost / 不进任务栏 / 无边框 / 尺寸）正确、
  `Show()` 后可见、插槽空时容器隐藏 / 有内容时显示并包壳、能走完测量与排布、
  登记表变化时自动重建、撤销后自动清空、禁用时窗口被关掉、位置文件写出
- 端到端（真 `PluginManager` + 真示例插件）：插槽与卡片登记成功、工厂能建出控件、
  禁用后控件与卡片都撤掉，且状态是「已禁用」而不是「需重启」（说明 UI 这一路没有吊住插件程序集）

**没验证**：人眼看界面（卡片外观、插槽边距与视觉）；拖动的真实手感；
窗口位置在**换显示器 / 改分辨率**后的还原（`IsOnScreen` 只写了逻辑，没有实测）；
`conversation.input.side` 与 `sidebar.below` 在真实布局里的观感。

> 排查提示：临时工程里如果**在本方法局部变量里攥着插槽条目或插件建的控件**，
> 禁用插件时一定显示「需重启」—— 那不是 bug，是那份引用还在。
> 把「取条目 + 建控件 + 读文本」放进一个单独的 `[MethodImpl(NoInlining)]` 方法就干净了。

---

### 12. 插件工具 + 清单依赖字段 + 内置开发文档（M7）

#### a) `tools.provide`（插件提供工具）

契约 `IPluginTools.cs`：`IToolRegistryApi.Register(PluginToolDefinition, PluginToolHandler)`。
宿主侧 `Services/Plugins/PluginToolFacade.cs` + `ToolSource.Plugin`。

| 东西 | 做法 | 为什么 |
| --- | --- | --- |
| 执行体存哪 | 单独一张按工具名的表（`PluginToolRegistry`），**不挂在 `ToolDefinition` 上** | 那份定义会在选择器、请求页、工具页到处被复制；混进插件代码就等于到处留着插件程序集 |
| 工具 id | `plugin:<插件id>:<工具名>`，`Target` 存插件 id | 会话存档里选中的 id 能反查是谁的工具 |
| 校验 | 名字 `^[a-z][a-z0-9_]{1,63}$`、schema 根 `type=object`、不与任何既有工具重名 | 名字是模型唯一的调用入口，重名等于随机命中一个 |
| 拒绝方式 | 记警告 + 返回 no-op 凭据，**不抛** | 一个工具名写错不该让整个插件加载失败 |
| 失败 | 返回 `PluginToolResult.Fail` 或抛异常 → 都变成一条失败结果回给模型 | 插件 bug 不能掀掉整轮对话 |
| 撤销 | 单个 `IDisposable` 或整个门面 `Dispose`（禁用插件时自动） | 与会话里选着的 id 自动失联 |
| 审批 | 走 `ToolExecutorDispatcher` 原有那条路（危险等级 + 权限档位） | 与内置工具完全一致，没有第二套规则 |
| 超时 | 沿用默认 5 分钟；schema 里声明 `timeout` 则由模型给 | 复用已有守卫，插件不必自己写超时 |

#### b) 清单的 `platforms` / `requires` / `conflicts`

`PluginManifest` 新增这四个字段（另有 `tags` / `author` / `homepage` 纯展示）。
判定分成两段：

- **加载前（`ApplyManifestRules`）**：平台不符、前置没装、`requires` 写了自己、`requires` 成环 → `Blocked`。
  成环时**整组都标**并写出环路 `a → b → a`（只标一个的话，用户改完那个会发现另一个又报同样的错）。
- **加载时（`CheckDependencies`）**：前置**没运行起来**、或与已运行插件互斥 → `DependencyBlocked`。

新增状态 `PluginRunState.DependencyBlocked`（「因依赖未运行」）。与 `Blocked` 分开的理由：
Blocked 是「这个插件的清单有问题，作者要改」，DependencyBlocked 是「插件没问题、环境不满足」，
用户能做的事完全不同。

配套规则：

| 场景 | 行为 |
| --- | --- |
| 加载顺序 | 对 `requires` 拓扑排序，前置先加载。不排序的话「A 依赖 B 但 B 目录名排在后面」会误判 |
| 互斥 | **双向检查**：不只查自己的 `conflicts`，还查「已运行插件有没有把我列为 conflicts」。
否则「谁先加载谁赢」，而目录名字母序不该有这种权力 |
| 前置被禁用 | 依赖方一起停（`StopDependentsAsync`，递归），状态写「前置 X 已停止，本插件也跟着停了」 |
| 前置重新启用 | `LoadWaitingDependentsAsync` 把因它被挡住的依赖方重新试一遍 |

#### c) 内置开发文档

`Services/Plugins/PluginDocs.cs`：**点一次写一次**，写进 `<数据根>/doc/`，然后浏览器打开首页。

- 内置九份 HTML（首页 + 八章）：搭建环境（VS 2026 建类库、改 csproj、部署、调试）、
  清单字段、日志/存储/资源、上下文贡献与会话事件、主动发起对话、窗口与插槽、注册工具、调试打包排错。
- 样式内联在各页里（共用一份 `<style>` 模板），**没有外部资源**：拷走 doc 目录也能直接看。
- 不点就不落盘 —— 只用对话功能的用户不该凭空多出九个文件。
- 入口：插件页标题下的那行小字「插件开发文档 · 从零写一个插件」（`PluginsPage.DevDocLink`）。

**已验证**（无头，两套 harness）：

- 工具：登记后注册表能解析、分派器能执行、名字不合规 / 坏 schema / 重名都被拒绝、
  执行体抛异常变成失败结果、单条撤销与门面撤销都干净
- 真插件（示例）声明 `tools.provide` 后 `sample_count_words` 出现在注册表、执行返回「3 个词」、
  禁用后工具消失且状态仍是「已禁用」（没吊住程序集）
- 清单：前置在运行 → 依赖方运行；前置不存在 → 无法加载；平台不符 → 无法加载；
  成环 → 两个都标；互斥 → 两者不会同时运行；前置禁用 → 依赖方跟着停；
  前置重新启用 → 依赖方自动回来
- 文档：九份文件都写出来、安装章含「Visual Studio 2026」与「类库」、清单章含能力与依赖字段、
  工具章含 `PluginToolDefinition` 与 `DangerLevel`、无未替换占位符

**没验证**：人眼看文档排版与插件页那行小字；插件工具在真实界面里的工具页展示与等级调整按钮。

---

## 二、M0 踩过的坑（别再踩）

### 坑 1：`LoadFromAssemblyPath` 会锁死文件

它把 dll 映射成映像节，之后文件以写方式永远打不开，卸载后也不释放。
**用 `LoadFromStream`**（读字节再加载）。`PluginLoadContext.ReadIntoMemory` 已封装。

顺带一个探测陷阱：只读打开在只读映像下**能成功**，所以早期误判「未锁定」。
要判断「文件能否被替换」必须用 `FileAccess.ReadWrite`。

### 坑 2：反射式 JSON 序列化插件类型会让 ALC 回收不掉

`System.Text.Json` 把类型元数据缓存进运行时静态结构，而类型定义在插件程序集里。
已排除且**全部无效**：每次新建 Options、每次新建 `DefaultJsonTypeInfoResolver`、
`MakeReadOnly()` 冻结、换非泛型 `Serialize(object, Type, options)`。
→ 契约用 `LoadText` / `SaveText` 规避。

### 坑 3：`async` 方法的状态机会持有引用

async 方法的 Task 就是状态机对象；写在里面的 `Assembly` / `Type` 局部变量会被抓到 Task 被回收。
→ 加载程序集、找类型、建实例放在**同步方法** `CreatePluginInstance` 里。

### 坑 4：`await` 插件返回的 Task 会把它挂进宿主的状态机

→ 用 `ExecuteDetached`：插件调用包在一个「跑完就没人引用」的 Task 里，
交回来的只有宿主自己的 TCS。

### 坑 5：GC 判定要在独立帧、且多轮收集

在同一方法里 `Unload()` 完就地 `GC.Collect` 一定失败（局部变量还活着）。
→ `DetachAndUnload`（发起卸载后立刻返回）与 `Collect`（独立帧）分开。
判据：ALC 对象被回收 **或** `alc.Assemblies` 已空，都算已卸载。

### 坑 6：`ShutdownRequested` 里再调 `Shutdown()` 会自己取消自己

→ 用 `_shutdownStarted` 闸门：第一次进入时 `e.Cancel = true` 并异步收尾，
收完再调 `desktop.Shutdown()`（第二次进入直接放行）。

---

## 三、待办

M0 / M1 / M2 / M3 / M4 / M6 / M7 已完成，只剩 **M5（桌宠插件本体）**。
它需要的底层能力现在全部齐备：

| 桌宠要做的事 | 用哪个出口 |
| --- | --- |
| 透明置顶窗口、可拖动、记住位置 | `context.Windows.CreateStandalone(...)` |
| 显示「思考中 / 等你确认 / 完成」 | `context.Chat.Subscribe(...)` 的 `ActivityChanged` |
| 气泡显示 AI 输出尾部 | 同上，`ContentDelta`（已经按 50 ms 合并，`Text` 是累积正文） |
| 点一下就开口 | `context.Sessions.SendAsync(sessionId, text)` |
| 在会话窗口里加自己的按钮 | `context.Slots.AddControl(PluginSlots.ConversationInputSide, ...)` |
| 给模型提供「喂食 / 摸摸头」这类动作 | `context.Tools.Register(...)`（要声明 `tools.provide`） |

写之前先让用户看一眼 **插件页那行小字「插件开发文档」**：它会把完整的九章 HTML 教程
生成到 `<数据根>/doc/` 并打开浏览器，里面从「VS 2026 建一个类库」讲到排错清单。

开工要点（M5）：

- **插件 id 用 `htaci.pet`**，目录 `plugins/htaci.pet/`，清单声明
  `ui.window` + `chat.observe` + `chat.control`（需要注上下文再加 `prompt.contribute`）。
- **桌宠窗口不要放进任务栏**：`ShowInTaskbar = false`（默认值就是 false，别改）。
- **气泡要跟着流式刷新，但别自己再做一次节流**：宿主已经 50 ms 合并过，插件里按事件直接更新即可；
  真正要小心的是「往 UI 上写」——事件投递**不在** UI 线程上，更新控件必须 `Dispatcher.UIThread.Post`。
- **关窗口别调 `Shutdown()`**，只关自己的窗口；插件被禁用时宿主管窗口。
- **`ShutdownAsync` 里要停掉自己的定时器 / 动画**，否则禁用后只能「需重启」。
- 位置记忆按「插件 id + 窗口标题」存，标题变了等于换一个记忆槽。

验收（`plugin-plan.md` 第 7 节的 M5 那两条）：
桌宠能显示流式内容尾部；点击能发起一轮；重启后位置被记住；禁用后窗口消失且不再响应事件。

### 已验证可复用的测试手法（M1 – M7 用过五次）

无头 harness：临时建 csproj（引 `HtaciAI.csproj`），`Main` 里 `AppPaths.Initialize` +
`DatabaseService.Initialize` + 写测试会话/模型行，然后**直接调服务层**，不建界面。
模型用本地假 OpenAI 兼容服务（Python `http.server`，几十行）：

- 请求体是 **chunked** 发的（`HttpClient` 默认），服务端要按 chunked 读，不能只看 `Content-Length`；
  响应用「不给长度 + `Connection: close`」最省事，手写 chunked 帧很容易写错
- 分支按最后一条 user 文本里的关键词走（`SLOW` / `BURST` / `TOOL` / `以下是需要你压缩的对话内容`），
  一次写好反复用；需要断言 system 时让它把 system 追加写进 `requests.log`
- **无头进程没有 Dispatcher 消息循环**：`Dispatcher.UIThread.Post` 排进去的回调永远不会跑。
  凡是「只在 UI 线程上访问」的服务（注册表、运行时），要在 harness 里装一个单线程
  `SynchronizationContext` 并在主线程泵它，否则续体会落到线程池上
- fixture 要连 `data.db-wal` / `-shm` 一起备份，否则还原后库是半截的
- **要测界面就用 `Avalonia.Headless`**：`AppBuilder.Configure<Application>()
  .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true }).SetupWithoutStarting();`
  之后窗口、控件、测量排布都真的能跑（窗口会真的 `Show()`），只是不画到屏幕上。
  临时工程记得写 `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`，
  否则中央包管理会因为「没在 Directory.Packages.props 里声明版本」直接拒绝还原
- 临时工程与脚本不要提交（`git status` 确认干净再 commit）；测完把 `data/` 与 `requests.log` 删掉

---

## 四、快速自检

```powershell
# 构建
dotnet build HtaciAI/HtaciAI.csproj -t:Rebuild
dotnet build HtaciAI.Desktop/HtaciAI.Desktop.csproj
dotnet build samples/HtaciAI.Plugin.Sample/HtaciAI.Plugin.Sample.csproj -c Release

# 装示例插件（<数据根> 见「设置 → 数据管理」）
#   plugins/htaci.sample/  →  HtaciAI.Plugin.Sample.dll + plugin.json
# 新标签页 →「插件」→ 勾选「启用插件」（或 设置 → 常规设置 → 插件）
# 看 logs/plugin.log、plugin-data/htaci.sample/stats
# 取消勾选 → 再勾上：应能立即重新加载，不提示需重启
```

预期结果：宿主 0 错误（6 个既有告警与本工作无关）。

---

## 五、约定与注意

- **不要**在插件目录放 `Avalonia*.dll` / `HtaciAI*.dll`：会被忽略并记警告，带上只会造成两套类型
- 插件不是沙箱：能读写任意文件、能建窗口；只提供「声明 + 展示 + 故障隔离 + 崩溃归因」
- 崩溃归因措辞用「**疑似**」：宿主无法证明因果；面包屑为空时不归咎任何插件
- `plugins/state.json` 归宿主独占，插件不许碰（崩溃后要读它决定这次不加载谁）
- 文件换行：仓库里部分文件是 CRLF、部分是 LF，改动前先看一眼，别整篇转换
