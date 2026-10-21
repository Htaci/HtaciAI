# 开发交接快照

> 用途：上下文压缩 / 换人接手时，只读这一份就能接着干。
> 最后更新：2026-10（对应提交 `cd7c9c4`）

---

## 零、当前状态（读这一段就够开工）

**已完成**：M0（插件加载/卸载/崩溃归因）、M1（`ConversationRuntime` + `ConversationRuntimeRegistry`，
A/B/C 三步全完）、M6（插件管理页）、工具超时与「继续」那一轮。

**提交线**（从旧到新）：

```
b0325ac  M6 插件管理页
03f989b  M1-A  会话状态搬进 ConversationRuntime
2c7a96c  M1-B  一轮对话的执行搬进 runtime，视图改成事件驱动渲染
0bdda5f  工具超时与停止（默认 5 分钟，停止时如实写回结果）
f625c5d  M1-C  重新生成/重试/继续/压缩搬进 runtime
cd7c9c4  M1 收尾：ConversationRuntimeRegistry，一个会话一份运行时
```

**下一步：M2（`PromptComposer` + `IChatEventBus`）**。开工要点见「三、待办」第一条。
工作区干净，`dotnet build HtaciAI/HtaciAI.csproj -t:Rebuild` 0 错误、6 个既有告警。

**关键能力现在到哪了**：

| 能力 | 状态 |
| --- | --- |
| 插件能不能加载/禁用/崩溃归因 | ✅ M0 |
| 插件能不能在界面上被看见（列表/启停/崩溃记录） | ✅ M6 |
| 插件能不能在会话没打开时发起对话 | ✅ M1（`ConversationRuntimeRegistry.Acquire` + `runtime.SendAsync`） |
| 插件能不能观察会话流（流式增量、轮次完成） | ⬜ M2 |
| 插件能不能往上下文里加东西 | ⬜ M2 |
| 插件能不能建窗口 / 插控件 | ⬜ M4 |
| 桌宠插件本体 | ⬜ M5（依赖 M2/M3/M4） |

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

M0 / M1 / M6 已完成，剩下 M2 / M3 / M4 / M5。推荐顺序：M2（本节第一条，插件能看能写上下文）
→ M4（插件能建窗口/插控件）→ M5（桌宠本体）；M3 是桌宠「主动发起对话」的前置，
但它依赖的 runtime 能力现在已经在 `ConversationRuntimeRegistry.Acquire` 里了，可以随时插队。

### M2：`PromptComposer` + `IChatEventBus`（**下一步**）

目标：插件能往上下文里加东西（`prompt.contribute`）、能看会话流（`chat.observe`）。
两者都接在 M1 已经做好的 runtime 上，**不需要碰视图**。

#### M2-a `PromptComposer`（上下文贡献）

落点只有一处：`ConversationRuntime.RunTurnAsync` 里的

```csharp
var system = Profile.BuildSystemPrompt(Profile.State.Model);   // 现在
```

- **不要把 `IConversationProfile.BuildSystemPrompt` 改成异步**（原计划是这么写的）。
  它只是拼静态文本，两处实现都是纯字符串拼接；改成 `Task<string>` 会把接口、两个实现、
  以及「工作空间那套三层拼装不能与普通对话合流」的注释一起搅动，收益却为零。
  改由 `PromptComposer.ComposeAsync(profile, modelId, ct)` 包一层：
  先拿 `profile.BuildSystemPrompt` 的结果，再按 `Order` 依次追加插件贡献，最后统一截断。
- 配额：**每插件 4 KB、单次合计 8 KB**（常量放 `PromptComposer`，先不做成设置项）。
  超限截断并 `PluginLog.Warn`；单个贡献者抛异常就跳过它，绝不因此让本轮发送失败。
- 每个插件实际贡献了多少字符要能被界面读到 —— M6 里那条「贡献配额要等 M2」就是等这个。
  最小做法：`PluginManager` 在每次组合后记一份「最近一次贡献长度」到内存，
  插件页直接显示；不要为此加持久化字段。
- 分隔与标注：追加内容前写一行来源（`<!-- plugin: htaci.pet -->` 之类），
  出问题时能一眼看出是谁写的。

#### M2-b `IChatEventBus`（会话事件）

**别重新发明事件**：`ConversationRuntime` 已经有全套——

| runtime 事件 | 映射到插件侧 |
| --- | --- |
| `RoundStarted` | `RoundStarted` |
| `ContentDelta` / `ThinkingDelta` | `ContentDelta` / `ThinkingDelta`（**合并后再投递**） |
| `ToolCallDelta` | `ToolCallStarted`（首次见到某个 index 时） |
| `MessageInserted` / `MessageUpdated` | `MessagePersisted` |
| `TurnEnded`（可 await） | `RoundCompleted` / `RoundFailed` |
| `RetryScheduled` | 可选，暂不暴露 |
| `ActivityChanged` | `ActivityChanged` |

要补的是三件宿主自己的事：

1. **挂到每个 runtime 上**。runtime 是由 `ConversationRuntimeRegistry` 按需创建的，
   总线必须在「新建 Entry」时挂上去，否则插件只能看见自己借过的那些会话。
   做法：给注册表加一个 `RuntimeCreated` 事件（`Action<ConversationRuntime>`），
   总线订阅它，在每个 runtime 上转订阅并把 `IDisposable` 收进自己的表。
2. **节流与不阻塞**。runtime 的事件是同步 `Action`，现在的订阅方是视图，开销可接受；
   插件不行。每条投递走「有界队列 + 50 ms 合并」：`ContentDelta` 攒够一段时间再发，
   队列满了丢最旧的增量（合并后的文本不丢语义）。插件回调一律 `try/catch` 吞掉。
3. **撤销**。`PluginManager.PluginEntry.Subscriptions` 这个字段 M0 就留好了，
   全是空的 —— 插件登记的每个 `IDisposable`（事件订阅、prompt 贡献者）都塞进去，
   禁用时统一 Dispose。这是「插件卸载能不能干净」的一半。

`ChatEventFilter`：按 sessionId（null = 全部）+ 按事件类型。默认给「全部会话」，
桌宠那种只关心当前会话的由插件自己过滤。

#### 契约要动的地方

- `IPluginContext` 加 `Prompts` / `Chat` 两个属性（M0 的注释里已经写明「随各自里程碑实装」）。
- 只在插件清单声明了 `prompt.contribute` / `chat.observe` 时注入；没声明就给 `null`
  或空实现（**定成 null** —— 让插件作者在编译期/第一行就发现「我忘了声明」）。
- 契约程序集是跨项目公开协议，改了要顺手更新 `samples/HtaciAI.Plugin.Sample` 的演示。

#### 验收

- 插件贡献出现在 system 末尾；超配额被截断且写了日志；贡献者抛异常时本轮照常发送。
- 每轮 `ContentDelta` 合并后，插件收到的累积文本与最终回复**一字不差**（不丢字、不重复）。
- 禁用插件后不再收到任何事件（`Subscriptions` 生效），且插件页显示它贡献了多少。
- 插件在会话没打开时（只有 runtime、没有视图）也能收到事件 —— 这是 M1 换来的能力，顺手验证。

#### 已验证可复用的测试手法（M1 用过两次）

无头 harness：临时建 csproj（引 `HtaciAI.csproj`），`Main` 里 `AppPaths.Initialize` +
`DatabaseService.Initialize` + 写测试会话/模型行，然后**直接调服务层**，不建界面。
模型用本地假 OpenAI 兼容服务（Python `http.server`，几十行）：

- 请求体是 **chunked** 发的（`HttpClient` 默认），服务端要按 chunked 读，不能只看 `Content-Length`
- 分支按最后一条 user 文本里的关键词走（`tool` / `slow` / `think` / `fail`），一次写好反复用
- fixture 要连 `data.db-wal` / `-shm` 一起备份，否则还原后库是半截的
- 临时工程与脚本不要提交（`git status` 确认干净再 commit）

> **M2 做完记得回来改本文件**：开头「零、当前状态」那张能力表里两行 ⬜ 改成 ✅，
> 提交线追加一条，并把本条待办挪进「一、已完成」。

### M4（只依赖 M0，可先做）

- `IPluginWindowHost`（`CreateStandalone` + 记忆位置）+ `IUiExtensionRegistry` + `PluginControlHost`
- 插槽 id：`newtab.cards` `conversation.header` `conversation.input.side` `sidebar.below` `mainwindow.titlebar.right`

> M6 已完成（`Views/PluginsPage`）：插件列表、启停、能力清单、崩溃记录与日志入口、
> 需重启提示、「崩溃后仍自动加载」勾选（默认不勾）。「贡献配额」要等 M2 有贡献模型后再加。

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
