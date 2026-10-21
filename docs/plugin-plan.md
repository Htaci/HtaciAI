# 插件系统实施计划

> 目标：让第三方扩展能参与 UI、上下文构建与会话生命周期，首个验证案例是「AI 对话桌宠」。
> 所有路径相对仓库根目录。
>
> **进度交接请看 [`plugin-handoff.md`](./plugin-handoff.md)** —— 那一份记录已完成项、
> 踩过的坑与下一步，本文只保留设计与验收标准。

## 0. 范围

**做**
- 插件扩展 UI（独立窗口 + 页面内插槽）
- 插件参与上下文构建（system 提示词贡献）
- 插件观察会话流（用户发出、流式增量、工具、轮次完成、审批/提问）
- 插件注入与控制（主动发起一轮、取消、改权限档位、注入消息）
- 插件注册工具
- 从本地目录加载，设置页管理，总开关默认关闭

**不做（明确排除）**
- 安全沙箱。插件在宿主进程 UI 线程上跑，能读写任意文件、执行任意代码。能提供的只有「声明 + 启用前展示 + 故障隔离」，不承诺隔离。
- 在线分发 / 插件商店。只从 `<数据根>/plugins/` 加载。
- 跨进程 / 跨语言插件。

**前置结论**：第一步必须先做一次内部重构（第 1 节），否则「插件在会话未打开时也能发起对话」无法成立。

---

## 1. 前置重构：把「一轮对话」从视图里搬出来

### 1.1 现状（已核对代码）

`HtaciAI/Views/ConversationView.axaml.cs` ≈ 3200 行，把四件事混在一起：

| 职责 | 代表成员 |
| --- | --- |
| 会话状态 | `_messages`、`_profile`、`_busy`、`_historyLoadTcs`、`_sendCts` |
| 请求执行 | `SendMessageAsync`、`RunTurnAsync`、`RegenerateAsync`、`RetryTurnAsync`、`CompressContextAsync`、`MaybeAutoCompressAsync` |
| 落库 | `PersistMessage`、`UpdatePersistedMessageAsync`、`PersistSessionAsync` |
| UI 渲染 | `RenderHistory`、`BuildTurnBlock`、`BeginAssistantRound`、`UpsertToolCard`、`AddErrorCard`、`ScrollToEnd`、向上分批加载 |
| 交互等待 | `ApproveInlineAsync`、`AskInlineAsync`（建卡片、等用户点） |

`ChatGateway.ChatAsync` 的 `beginRound` 回调由视图实现并直接建控件，`ChatRoundSink` 的 `OnContent/OnThinking/OnToolCall` 直接写 `TurnBodyView`。**请求执行与 UI 是交叉的，不是分层的。**

### 1.2 目标结构

```
ConversationRuntime            （每会话一个，无 UI 依赖）
├─ 状态：_messages、_profile、_busy、_sendCts
├─ 入口：Initialize / SendAsync / RegenerateAsync / RetryAsync / CompressAsync / Cancel
├─ 事件：见 2.3 的 ChatEvent
└─ 交互出口：IInteractionHost（审批 / 提问）

ConversationRuntimeRegistry    （进程内，按 sessionId）
├─ GetOrCreate(profileFactory)
├─ 引用计数：视图 + 插件订阅 == 0 且无活跃轮次时，空闲 N 分钟后回收
└─ 视图关闭不销毁 runtime —— 这是桌宠能在后台工作的前提

ConversationView               （纯渲染 + 输入）
├─ 订阅 runtime 事件更新控件
├─ 输入框 → runtime.SendAsync
└─ 页面状态（滚动、分批渲染、插入下标）留在视图
```

### 1.3 拆分映射（逐条）

| 现状 | 去向 | 说明 |
| --- | --- | --- |
| `_messages`、`_busy`、`_sendCts` | Runtime | `_busy` 上移顺带解决「同一会话两处并发发送」 |
| `SendMessageAsync` 前半段（模型校验、标题初始化、维护状态） | Runtime | 提示类反馈改走事件，由当前显示的视图渲染 |
| `RunTurnAsync` | Runtime | 生成 system / options / 调网关 / 收尾全在 runtime |
| `BeginAssistantRound` | Runtime 建轮次对象 + 发 `RoundStarted` | 视图订阅后建 root/host/body |
| `ChatRoundSink` 三个回调 | Runtime 累积文本 + 发 `ContentDelta/ThinkingDelta/ToolCallDelta` | 视图只做追加 |
| `PersistMessage` / `UpdatePersistedMessageAsync` | Runtime 落库 + 发 `MessagePersisted` | 视图订阅后增量渲染 |
| `UpsertToolCard` / `AddErrorCard` | 视图渲染，数据来自事件 | |
| `RenderHistory` / `AppendGroups` / `LoadEarlierHistory` | 留在视图 | 纯渲染优化，与插件无关 |
| `_streamInsertIndex`、`_autoScroll`、`_firstRenderedGroup` | 留在视图 | 页面级状态 |
| `ApplyAutoTitleAsync` / `GenerateTitleAsync` | Runtime | 依赖 turn 结果 |
| `ApproveInlineAsync` / `AskInlineAsync` | `IInteractionHost` | 视图实现 = 插卡片；无视图 = 系统提醒 + 可选插件窗口；都没有 = 自动拒绝并回注原因 |
| `SessionNotifier.Notify` | Runtime | 不再依赖视图存在 |
| `RegenerateAsync` / `RetryTurnAsync` / `CompressContextAsync` | Runtime | `RetryTurnAsync` 里「保留上一轮内容」的语义不变 |

### 1.4 迁移分三步，每步都能单独构建、单独回归

- **A**（✅ 已完成）：建 `ConversationRuntime` 壳，把 `_messages`/`_busy`/`_gateway`/`_profile`/`_sendCts` 迁进去，
  视图改为通过 runtime 访问。行为完全不变。**未接 registry**，理由见 `plugin-handoff.md` 第三节。
- **B**：把 `SendMessageAsync` + `RunTurnAsync` + 流式渲染改成事件驱动。这是风险最高的一步，回归清单最重。
- **C**：迁 `Regenerate`/`Retry`/`Compress`/异步入审批与提问。

每步验收见第 7 节。

---

## 2. 插件契约：`HtaciAI.Plugin.Abstractions`

新建类库，`net10.0`，引用 `Avalonia 12.0.4`（与宿主同版本，走共享 ALC）。只放接口与 DTO，不含实现。

### 2.1 清单 `plugin.json`

```json
{
  "id": "htaci.pet",
  "name": "AI 桌宠",
  "version": "1.0.0",
  "minHostVersion": "1.0.0",
  "entry": "HtaciAI.Pet.dll",
  "entryType": "HtaciAI.Pet.PetPlugin",
  "capabilities": ["ui.window", "ui.slot", "prompt.contribute", "chat.observe", "chat.control"]
}
```

`capabilities` 决定宿主注入哪些服务；插件未声明的能力，对应服务为 null 或空实现。启用前在设置页逐项展示给用户看。

### 2.2 入口

```csharp
public interface IPlugin
{
    Task InitializeAsync(IPluginContext context, CancellationToken ct);
    Task ShutdownAsync();
}

public interface IPluginContext
{
    IPluginLogger Log { get; }
    IPluginStorage Storage { get; }          // 插件私有 JSON 配置 + 数据目录
    IResourceLocator Resources { get; }      // 插件目录下的图标/资源
    IPromptRegistry Prompts { get; }
    IChatEventBus Chat { get; }
    ISessionFacade Sessions { get; }
    IPluginWindowHost Windows { get; }
    IUiSlotRegistry Slots { get; }
    IToolRegistryApi Tools { get; }
}
```

### 2.3 扩展点

**a) 上下文构建**

```csharp
public interface IPromptContributor
{
    string Id { get; }
    int Order { get; }
    PromptLayer Layer { get; }               // SystemAppend / BeforeUserPrompt
    ValueTask<string?> BuildAsync(PromptContext ctx, CancellationToken ct);
}
```

- 接入点：`StandaloneProfile.BuildSystemPrompt` 与 `WorkspaceProfile.BuildSystemPrompt` 的末尾统一走 `PromptComposer`。
- **签名要改成异步**：现在两者都是 `string BuildSystemPrompt(string modelId)`，调用点在 `RunTurnAsync`（已是 async）。改成 `Task<string> BuildSystemPromptAsync(string modelId, CancellationToken ct)`，插件贡献在发送前一次性取好。不要试图在同步方法里 `GetAwaiter().GetResult()`。
- 硬约束：每插件配额（默认 4 KB），单次总配额（默认 8 KB）；超限截断并记日志；异常吞掉并跳过该贡献者，不影响本轮发送。
- 每个插件的实际贡献长度要能在设置页看到——这是唯一能悄悄改变模型行为的入口。

**b) 会话事件**

```csharp
public interface IChatEventBus { IDisposable Subscribe(IChatEventSink sink, ChatEventFilter? filter = null); }
public interface IChatEventSink { ValueTask OnEventAsync(ChatEvent e, CancellationToken ct); }
```

事件种类：`RoundStarted`、`ContentDelta`、`ThinkingDelta`、`ToolCallStarted`、`ToolCallFinished`、`MessagePersisted`、`RoundCompleted`、`RoundFailed`、`ApprovalRequested`、`QuestionAsked`、`ActivityChanged`。

- `ChatEventFilter`：按 sessionId（null = 全部，可选「仅当前活跃会话」）、按事件类型。
- **节流由宿主做**，不指望插件自律：`ContentDelta` 在投递前按 50 ms 合并成一段再发，插件拿到的是累积文本 + 本次增量。
- 投递顺序保证按会话串行；插件回调不阻塞主流程——用有界队列 + 丢弃最旧增量（文本合并后不会丢语义）。

**c) 注入与控制**

```csharp
public interface ISessionFacade
{
    Task<SendResult> SendAsync(string sessionId, string text, SendOptions? options = null, CancellationToken ct = default);
    Task CancelAsync(string sessionId);
    Task<bool> SetPermissionModeAsync(string sessionId, PermissionMode mode);
    Task<bool> CompressAsync(string sessionId);
    IReadOnlyList<MessageSnapshot> GetRecentMessages(string sessionId, int take);
}
```

两类「注入」必须分开，不要混：
- 给**模型**看的：走 `SendAsync`（作为 user 消息）或 prompt contributor。
- 给**用户**看的旁注：走 `AppendNoticeAsync`，落成 `role=agent` 消息（`chat_message` 已允许该值），**不进模型上下文**。

约束：`SendAsync` 对同一会话复用 runtime 的 `_busy` 互斥；插件发起的一轮必须有来源标记（`MessageSnapshot.Origin = "plugin:<id>"`），便于日后审计。

**d) 工具注册**

```csharp
public interface IToolRegistryApi
{
    IDisposable Register(ToolDefinition definition, Func<ChatToolCall, CancellationToken, Task<ToolExecution>> execute);
}
```

- 新增 `ToolSource.Plugin`，在 `ToolExecutorDispatcher` 的分派 switch 里加一支，走插件注册表。
- 插件工具同样过权限网关；`DangerLevel` 由插件声明，用户在工具页可见并可调（复用上一轮做的可点等级徽标）。
- 禁用插件时自动注销其工具，并从会话 `EnabledToolIds` 的解析结果里消失。

**e) UI**

插件**可以自由使用 Avalonia**：自己写 `UserControl`、自己画、自己动画、自己 `new Window`。
宿主只负责三件事——在哪里插、给什么容器、退出时谁来收。

```csharp
public interface IUiExtensionRegistry
{
    /// 往指定插槽注册一个控件。返回的 IDisposable 用来撤销。
    IDisposable AddControl(string slotId, Func<Control> factory, int order = 0);

    /// 新标签页入口卡片。
    IDisposable AddLauncherCard(LauncherCard card);
}

public sealed class LauncherCard
{
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string? Glyph { get; init; }
    public string? IconPath { get; init; }     // 相对插件目录
    public Action OnClick { get; init; } = () => { };
}
```

插槽 id 先定这几个：`newtab.cards`、`conversation.header`、`conversation.input.side`、
`sidebar.below`、`mainwindow.titlebar.right`。

**每个插槽位置都包一层 `PluginControlHost`**（宿主提供的"自定义控件容器"）：
- 外面套 `Border` + 插件名 tooltip，出错时整块换成一行灰色「插件 X 显示失败」，
  而不是让异常穿透到布局测量阶段；
- 记录 `(pluginId, slotId, control)`，禁用插件时按这张表逐个 `Parent.Children.Remove`；
- 视��重建（会话缓存淘汰后重进）时 factory 会被重新调用，因此 **factory 不能持有瞬时状态**，
  状态放插件自己的 `IPluginStorage` 或插件自建的对象里。

**窗口**：插件可以自己 `new Window`，也可以用宿主给的辅助方法拿到一个配好的：

```csharp
public interface IPluginWindowHost
{
    /// 建一个已配好的窗口（透明/无边框/置顶/可拖/记忆位置），并把 owner 设成主窗口。
    Window CreateStandalone(WindowOptions options);
    IReadOnlyList<Window> OpenWindows { get; }   // 只读，宿主用来在退出时统一收口
}

public sealed class WindowOptions
{
    public string Title { get; init; } = "";
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Topmost { get; init; } = true;
    public bool Transparent { get; init; }
    public bool ShowInTaskbar { get; init; }
    public bool AllowDrag { get; init; } = true;
    public bool RememberPosition { get; init; } = true;   // 按插件 id 记住位置
}
```

为什么要有这个辅助方法而不是纯手工：`ShowInTaskbar = false` + `Transparent` + `Topmost` +
`WindowStartupLocation` 这四个属性配错一个，桌宠就变成任务栏里多一个条目、或者点不到、或者跑出屏幕。
记忆位置也要按插件 id 存，否则插件作者得自己造一套。

⚠️ **`ShutdownMode` 仍然要显式设一次**，但理由和"关不掉程序"无关。

退出路径本身没问题：关闭按钮直接 `Close()`，托盘"退出"也会显式结束进程，两条路都收得干净。
问题在 Avalonia 的默认值是 `OnLastWindowClose` —— **插件窗口一出现，这个默认值的行为就变了**：
主窗口关掉后桌宠窗口还在，进程不会退出，用户看着以为关掉了，任务管理器里还留着。
现在的实现之所以没问题，只是因为还没有第二个窗口。

改法：`App` 里一行 `desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;`。
它和"最小化到托盘"不冲突——托盘模式下主窗口是 `Hide()` 而不是 `Close()`，
不触发关闭，进程照常活着；托盘"退出"调 `desktop.Shutdown()`，`ShutdownRequested` 里收插件，
与现在 `McpConnectionManager.ShutdownAll` 同一处。

---

## 3. 加载与生命周期

- **目录**：`<数据根>/plugins/<id>/`。`AppPaths` 增加 `Plugins` 属性（与 `Skills`/`Tools` 同级）。
- **加载器**：每个插件一个 `AssemblyLoadContext(name, isCollectible: true)`。
  - **Avalonia 与宿主程序集必须解析到 default context**，不能各插件一份。这不是为了省内存，
    是类型同一性问题：插件自己加载的 `Avalonia.dll` 里 `Control` 与宿主那座可视树认的 `Control`
    是两个不同 `Type`，把前者塞进后者会直接失败；而且第二份 Avalonia 的静态状态是独立的，
    `Application.Current` 为 null、字体与主题资源全取不到。
  - 判断规则：目标程序集名以 `Avalonia` 开头、或等于 `HtaciAI`/`HtaciAI.Plugin.Abstractions`、
    或已在 `AppDomain.CurrentDomain.GetAssemblies()` 里 → 交给 default context。
    剩下的才从插件目录加载（用 `AssemblyDependencyResolver`）。
  - 插件目录里若出现 `Avalonia*.dll`，加载时直接跳过并记一条警告，不要让它被装上。
- **卸载（`isCollectible: true` 的意义）**：禁用插件时执行
  `ShutdownAsync` → 关窗口 → 摘控件 → 撤销全部 `IDisposable` → `alc.Unload()`。
  **"能不能真的回收"是 best-effort，必须实测，不能假设。** 挡住回收的常见来源：
  | 来源 | 处理 |
  | --- | --- |
  | 宿主事件订阅 | 由 `IPluginContext` 统一登记，禁用时全部 `Dispose` |
  | 插件注册的工具 / prompt 贡献者 | 同上，登记后注销 |
  | 插件自己的 `DispatcherTimer` / 动画 | 插件责任（契约里要写明），宿主在 `ShutdownAsync` 后校验一次存活 |
  | 插件起的线程 / 未完成的 Task | 同上；`ShutdownAsync` 的 ct 必须传下去 |
  | `Dispatcher.UIThread.Post` 里待执行的回调 | 无法撤销，只能等它跑完；`Unload()` 前 `await` 一次 `Dispatcher.UIThread.InvokeAsync(() => {}, Background)` 冲队列 |
  | Avalonia 内部以 `Type` 为 key 的静态缓存 | **最不确定的一条**，只能靠实测 |

  验证方式：`var weak = new WeakReference(alc); alc.Unload(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();`
  然后看 `weak.IsAlive`。**回收失败不影响功能禁用** —— 禁用是"停止投递 + 收窗口 + 撤销注册"，
  这一套必须 100% 生效；内存没释放只记一条日志。
- **`AssemblyLoadContext.Unload()` 之后不能再有代码在这个 ALC 里跑**：所以 `ShutdownAsync` 必须在
  `Unload` 之前完成并 `await`，不能 fire-and-forget。
- **"需重启才能生效"是探测出来的，不是预先知道的。** 没法在禁用之前判断这个插件能不能卸载，
  只能走一遍完整流程再判定。所以顺序是固定的：
  1. **静默期**：先把该插件的投递出口全部关掉（事件、prompt 贡献、工具、控件工厂），
     新调用一律拒绝；再 `await` 在途回调完成，超时（默认 2 s）后不等了；
  2. `ShutdownAsync()`（带 ct），超时（默认 3 s）后视为卡死，直接继续；
  3. 关窗口、摘控件、撤销全部 `IDisposable`；
  4. `await Dispatcher.UIThread.InvokeAsync(() => { }, Background)` 冲掉 Dispatcher 队列；
  5. `alc.Unload()` + 多轮 `GC.Collect`；
  6. 判定：ALC 对象被回收 → 已卸载；对象还活着但 `alc.Assemblies` 已空 → 也算已卸载
     （说明程序集卸干净了，重新加载不会出现两套静态状态）；否则 → 「需重启」。
  - 判定为「需重启」后**不再尝试第二次 `Unload`**（重复卸载只会让状态更混乱）。

**M0 实测结论（2026-10，Windows / .NET 10）**

排查过程与最终结论（每条都用独立探针跑过）：

| 变量 | 结果 |
| --- | --- |
| `LoadFromAssemblyPath` 加载 | 文件被映射成映像节，禁用后**始终不可写**，10 轮 GC 也不释放 |
| 改用 `LoadFromStream`（读字节再加载） | 加载期间文件就可写；禁用后立刻可替换 ✅ |
| 仅加载 / 仅实例化 / 实例化并调用 | 三种都能回收 |
| 用真实的 `PluginContext` | **回收不掉** |
| `PluginContext` 只构造、不交给插件 | 能回收 |
| 把 `PluginContext` 拆成 Log / Storage / Resources 逐个真实化 | 只有 **Storage 真实实现**导致回收失败 |
| Storage 只做文本读写（不序列化） | 能回收 |
| Storage 用 `JsonSerializer.Serialize<T>` | **回收不掉** |
| 改用非泛型 `Serialize(object, Type, options)` | **回收不掉** |
| 每次新建 Options + 新建 `DefaultJsonTypeInfoResolver` | **回收不掉** |
| 再调 `options.MakeReadOnly()` 冻结 | **回收不掉** |

→ 根因有两条，都已修：
1. **`LoadFromAssemblyPath` 会把 dll 映射成映像节**，禁用后文件不可写、程序集也不释放。
   改为 `LoadFromStream`（读字节 → 流加载），文件不再被映射，卸载后立刻可替换。
   代价是 `Assembly.Location` 为空、原生 dll 需插件自理（见 `PluginLoadContext.ReadIntoMemory`）。
2. **用反射式 JSON 序列化处理插件自己的类型会让 ALC 回收不掉。**
   System.Text.Json 把类型元数据缓存进运行时静态结构，而那个类型定义在插件程序集里，
   于是永远被持有。换 options、换 resolver、冻结 options 都无效 —— 这是运行时的行为。
   所以 `IPluginStorage` 新增 `LoadText` / `SaveText`（宿主只当字符串），作为推荐用法；
   泛型 `Load<T>` / `Save<T>` 保留但明确标注「会导致需要重启才能卸载」。

修完之后示例插件的加载 → 禁用 → 重新启用 → 再禁用**全程无需重启**。


- **宿主持有插件状态，插件不许碰**：崩溃标记、崩溃计数、
  "崩溃后是否仍自动加载"这些必须存在宿主自己的文件
  `<数据根>/plugins/state.json` 里，**不能放进插件目录或 `IPluginStorage`**。
  理由：崩溃后要读它来决定"这次不加载谁"，而那时插件代码一行都还没跑。
- **启动顺序**：`AppPaths.Initialize` → 数据库迁移 → `DatabaseService.Initialize` → `ToolRegistry` → `SkillRegistry` → `PluginManager.LoadAllAsync()`。插件加载放在最后，因为它依赖前面全部就绪。
- **总开关**：`AppSettingsData.EnablePlugins`（默认 `false`）。关闭时一个插件都不加载；运行中关闭 = 逐个走卸载流程。
- **版本检查**：`minHostVersion` 高于 `AppVersion.Current` 时拒绝加载并提示。
- **故障隔离（try 的边界必须说清）**：
  - 所有插件回调（事件、控件工厂、卡片点击、prompt 贡献、工具执行、窗口）统一包一层 try/catch，异常记日志不外抛。
  - **catch 不到的东西**：`StackOverflowException`、`OutOfMemoryException`、`AccessViolationException`，
    以及别的线程上逃逸的 `async void` 异常、渲染线程里的异常。这类问题的一线处理是"崩溃归因 + 下次不加载"（见 3.5），
    不是"拦住它"——进程该崩还是会崩，对用户不承诺插件绝不会导致崩溃。
  - `PluginHealth` 计数：同一插件连续失败 5 次 → 自动禁用 + 一条通知，设置页显示「已因错误自动禁用」。
  - 插件回调默认带超时（`prompt.contribute` 500 ms、`chat.observe` 200 ms），超时按失败计。
- **关闭顺序**：主窗口 `Closing` → 插件窗口全部关闭 → `ShutdownAsync` → 撤销全部 `IDisposable` → `Unload`。挂在 `desktop.ShutdownRequested` 上（与 `McpConnectionManager.ShutdownAll` 同一处），保证重启路径也覆盖。

### 3.5 崩溃归因与下次启动的处理

> 立场：装插件是用户自己的判断，宿主不假装能隔离，但**必须能事后查明是谁干的，并保证下次不会再被同一个插件崩一次**。

**难点：进程崩了，谁也没机会写日志。** 所以必须崩溃**之前**就把"此刻正在跑哪个插件"落盘。

1. **面包屑（breadcrumb）**——在每次可能崩溃的插件调用**之前**写一行到
   `<数据根>/logs/plugin-activity.log`（覆盖写固定文件，不是追加），内容：
   `{"plugin":"htaci.pet","phase":"window.create","at":1699999999}`。调用返回后清空。
   - 覆盖写小文件 + 每 N 秒最多一次（默认 1 s）节流；`Initialize`、窗口创建、工具执行、控件工厂
     **强制不节流**（这些是崩溃高发点）。
   - 事件投递节流，不逐条写——每 token 都写盘开销不可接受。
2. **运行标记**——启动时写 `<数据根>/plugins/running.flag`，正常退出时删除。
   下次启动若发现这个文件还在 → 上次是非正常退出。
3. **判定**——非正常退出 + 面包屑里有插件 id → 把该插件记为「疑似崩溃源」，
   在 `state.json` 里 `crashCount++`、`suspectAt=时间戳`、`crashLogPath=那一轮的日志`。
   用**"疑似"**措辞：宿主无法证明因果，可能是它、也可能只是最后一个跑的。
4. **下次启动**——该插件默认**不加载**（除非用户给它开了"崩溃后仍自动加载"）。插件列表里：
   - 状态标记「疑似导致上次崩溃」+ 崩溃次数；
   - 一个「查看崩溃日志」按钮，打开对应日志文件（`<数据根>/logs/crash-*.log`）；
   - 提供「启用」，点击后先弹确认：
     「该插件疑似导致程序崩溃。继续启用可能导致程序再次崩溃。是否继续？」
     下面是勾选框「崩溃后仍自动加载」（默认**不勾**），副标题写清后果：
     「勾选后，即使该插件再次导致崩溃，下次启动仍会自动加载；不勾时崩溃一次即自动停用。」
     用户在确认框里点继续 → 清除 suspect 标记 → 立即加载。
5. **崩溃日志内容**：时间、宿主版本、插件 id/版本、最后面包屑、异常类型与消息、
   若 `AppDomain.UnhandledException` 抓到了就带调用栈。三个兜底 handler
   （`Dispatcher` 未处理异常、`AppDomain.UnhandledException`、`TaskScheduler.UnobservedTaskException`）
   都往这个文件写，写完才让进程退出。
6. **写日志本身不能再抛异常**：面包屑与崩溃日志的写盘全部包在 try 里，
   写不了就退化成内存里最后一条，不影响主流程。

---

## 4. 桌宠插件的落地形态（验证案例）

分两档，对应第 1 节的阶段划分：

**依赖重构前的部分**
- 透明、无边框、置顶窗口，按住任意处拖动（`BeginMoveDrag`，与 `MainWindow.Form_OnDrag` 同思路）
- 状态显示：靠 `ActivityChanged` 显示「思考中 / 等你确认 / 完成」
- 气泡显示 AI 输出尾部：靠 `ContentDelta`（宿主已节流）

**依赖重构后的部分**
- 点击桌宠 → `Sessions.SendAsync`（预设快捷指令或弹一个小输入框）→ 真正「主动开口」
- 在会话未打开时也能收到事件（runtime 存活，视图不在）

打包：一个独立类库 + `plugin.json` + 图标，放进 `plugins/htaci.pet/`。作为契约的参考实现，与第 6 节的后两项一起交付。

---

## 5. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| Runtime 拆分的回归 | 高（会话核心路径） | 分 A/B/C 三步，每步全量回归；B 步前后保留旧路径可对照 |
| 两套 Avalonia 程序集 | 高（难查的样式失效） | 加载器强制解析到 default context；插件目录出现 `Avalonia*.dll` 直接跳过 |
| prompt 贡献是同步方法 | 中 | 改接口为异步，发送前一次性取好 |
| 事件风暴拖慢 UI | 中 | 宿主侧 50 ms 合并 + 有界队列 + 丢最旧 |
| 插件注入改变模型行为 | 中 | 配额 + 设置页显示各插件贡献字数 + 来源可见 |
| `Unload()` 回收不掉 | 中 | 功能禁用与内存回收解耦；`WeakReference` 探测，失败标「需重启」，不重试 |
| Avalonia 内部静态缓存吊住程序集 | 中（不确定） | 实测确认；不行就只提示「需重启才能完全释放」 |
| `ShutdownMode` 默认 `OnLastWindowClose` | 中 | 显式设 `OnMainWindowClose`；托盘模式用 `Hide()` 不触发关闭 |
| `async void` / 其他线程异常逃逸 | 高（会崩进程） | 崩溃归因（3.5）：面包屑 + 运行标记 + 下次不加载 |
| 归因误判（把无辜插件当崩溃源） | 中 | 措辞用「疑似」；给日志入口让用户自己核；一键恢复启用 |
| 单文件发布下的依赖解析 | 低 | `AssemblyDependencyResolver` + 插件目录不在发布包裹内 |
| 插件卡死 UI 线程 | 高 | 回调超时 + 连续失败自动禁用；已明确这不是沙箱 |

---

## 6. 里程碑

| 里程碑 | 内容 | 依赖 | 状态 |
| --- | --- | --- | --- |
| M0 | `HtaciAI.Plugin.Abstractions` + `PluginManager` + `AppPaths.Plugins` + `state.json` + 空插件能加载/卸载/重启提示 | 无 | ✅ 已完成并通过实测 |
| M1 | `ConversationRuntime` 拆分 A/B/C 三步 | 无 | 🔶 A 已完成，B/C 未开始 |
| M2 | `PromptComposer` + `IChatEventBus`（接在 runtime 上） | M1 | ⬜ 未开始 |
| M3 | `ISessionFacade`（主动发起、取消、改权限、agent 旁注） | M1 | ⬜ 未开始 |
| M4 | `IPluginWindowHost` + `IUiExtensionRegistry` + `PluginControlHost` + 插槽与入口卡片 | M0 | ⬜ 未开始 |
| M5 | 桌宠插件（先窗口+气泡，后主动发起） | M2、M3、M4 | ⬜ 未开始 |
| M6 | 「插件」页（新标签页入口）：列表、启用/禁用、能力清单、错误状态、需重启提示、崩溃记录与崩溃日志、崩溃后仍自动加载；与设置页总开关联动 | M0 | ✅ 已完成（「贡献配额」依赖 M2 的贡献模型） |

**下一步建议**：M1（`ConversationRuntime` 拆分）是 M2/M3 的前置，也是工作量大头；
M4/M6 只依赖 M0，可以先做掉以尽快看到插件跑起来。

---

## 7. 验收清单

**M1 每步共同回归**（长会话，≥ 50 轮，含工具调用）
- 打开 / 切走 / 切回会话，历史渲染与滚动位置正确
- 发送、流式输出、工具调用卡片、思考卡、聚合卡收尾正确
- 停止（流式中 / 工具执行中 / 审批等待中）都能立刻中断，工具卡收成中断态
- 失败后「重试」保留上一轮内容；「重新生成」替换旧轮
- 上下文压缩（手动 / 自动）后行为不变
- 会话缓存淘汰后重进，不出现两份视图、不出现「看起来已结束但还在跑」

**M0（已实现，见 `HtaciAI/Services/Plugins` 与 `samples/HtaciAI.Plugin.Sample`）**
- 关掉总开关时插件目录存在也不加载
- 清单非法（JSON 坏了 / id 与目录名不符 / 版本不够 / 入口缺失）时状态为「无法加载」并给出原因
- 加载成功后在 `logs/plugin.log` 留下记录；插件私有存储落在 `plugin-data/<id>/`
- 插件目录里带 `Avalonia*.dll` / `HtaciAI*.dll` 时被忽略并记警告
- 禁用插件后：`ShutdownAsync` 被调用、订阅撤销、状态变为「已禁用」
- **禁用 → 重新启用 → 再禁用，全程无需重启**（示例插件实测）
- 加载期间与禁用之后，插件 dll 都可以被替换/删除（流加载，不建立映像节）
- 用了泛型存储 API（序列化插件自身类型）的插件，禁用后显示「需重启」并说明原因
- 崩溃归因：非正常退出 + 面包屑 → 下次启动跳过该插件、写 `logs/crash-*.log`、
  列表标「疑似导致上次崩溃」；确认启用后清标记并正常加载；`autoLoadAfterCrash` 勾上则崩溃后仍加载
- 正常退出（`ShutdownRequested`）时：插件收尾 → 删运行标记 → 退出；
  第二次进入关闭流程不会被自己取消掉
- 主窗口关闭时进程退出（`ShutdownMode.OnMainWindowClose`）



**M2**
- 插件贡献出现在 system 末尾，超配额被截断且有日志
- 每轮 ContentDelta 合并后插件收到的文本与最终回复一致（不丢字、不重复）

**M3**
- 会话视图未打开时插件 `SendAsync` 能跑完整一轮，消息正常落库
- 插件并发 `SendAsync` 同一会话被拒绝（互斥生效）
- 审批在无视图时的 fallback 行为明确（通知作答 / 自动拒绝）

**M4**
- 新标签页出现插件入口卡片，点击回调生效
- 会话视图重建后插槽内容重新生成，不残留旧控件
- 插件控件构造抛异常时，页面其他部分照常工作（`PluginControlHost` 兜住）

**M5**
- 桌宠能显示流式内容尾部；点击能发起一轮；重启后位置被记住
- 禁用桌宠插件后窗口消失且不再响应事件
