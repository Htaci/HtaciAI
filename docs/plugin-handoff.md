# 开发交接快照

> 用途：上下文压缩 / 换人接手时，只读这一份就能接着干。
> 最后更新：2026-10（对应提交 `29bb33b`）

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

### M1：`ConversationRuntime` 拆分（M2/M3 的前置）

现状：`ConversationView.axaml.cs`（约 3200 行）把「会话状态 / 请求执行 / 落库 / UI 渲染 / 交互等待」混在一起，
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
  发请求前的自动压缩临时挂在 `BeforeRequest` 钩子上，C 步再搬走。

  已实测（本地假 OpenAI 兼容服务，不花钱）：普通流式、思考卡、工具循环 + 聚合卡、
  点停止（保留已收正文、无错误卡）、重试（旧轮保留、新轮追加）、
  重新生成（旧轮替换、位置正确）、自动话题命名。

- **C** ⬜ 未开始：迁 `Regenerate` / `Retry` / `Compress` / 异步审批与提问。

配套：`ConversationRuntimeRegistry`（按 sessionId 复用，视图关闭不销毁——桌宠后台工作的前提）。
**A 步刻意没接 registry**：一旦 runtime 跨视图复用，`LoadMessagesAsync` 就会在「视图重建」时
把库里的历史再灌一遍（重复消息），或者干脆不读（历史为空）。这件事必须与 B 步的
「事件驱动 + 是否已初始化」判断一起做，单独提前接只会引入难查的状态错乱。

> 另外记下了插件清单的字段设想（平台独占、前置依赖、循环依赖与连带停用规则），
> 见 [`plugin-manifest.md`](./plugin-manifest.md)。清单继续用 JSON，不上 ini。

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
