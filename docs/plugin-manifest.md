# 插件清单（plugin.json）设计草稿

> 状态：**设计记录，尚未实现**。当前代码只认 `id/name/version/description/minHostVersion/entry/entryType/capabilities`，
> 下面带「待实现」的字段宿主还不会读。
>
> 结论先写在这里：**清单继续用 JSON**，不上 ini。

## 1. 目录约定（已实现）

```
<数据根>/plugins/
  htaci.pet/            ← 一个插件一个文件夹，文件夹名 = 插件 id
    plugin.json         ← 有这个文件才认它是插件
    HtaciAI.Pet.dll
    assets/...
```

- 扫描只看一级子目录；以 `.` 开头的目录跳过（宿主自己的东西、用户有意隐藏）。
- 目录名即插件 id，`plugin.json` 里的 `id` 必须与之一致，不一致直接判 `Blocked`
  （状态记录、私有数据目录都按 id 索引，两边错位会让配置串台）。
- 私有数据**不放插件目录**，放 `<数据根>/plugin-data/<id>/`：升级插件时整目录替换，
  用户在插件目录里手放的东西会被覆盖。
- 这与工具（`<数据根>/tools/*.py`）、技能（`<数据根>/skills/<id>/SKILL.md`）是同一套思路：
  一个扩展 = 一个文件夹 + 一份声明。

## 2. 为什么是 JSON 而不是 ini

| 需求 | JSON | ini |
| --- | --- | --- |
| 字符串数组（能力、平台、前置） | 原生 | 只能逗号分隔一行，值里有逗号就完蛋 |
| 嵌套（编辑器配置、贡献声明） | 原生 | 不支持，得摊平成 `a.b.c=` |
| 布尔/数字类型 | 原生 | 全靠字符串，解析规则各家不同 |
| 中文 / 换行（长描述） | 原生（转义） | 续行与编码是老大难 |
| 已有基础设施 | `JsonFileStore` 已在用 | 要自己写解析器 |
| 手改容错 | 解析失败能明确报错行号 | 同左，但无嵌套后错误更隐蔽 |

YAML 更可读，但要引新依赖（且多文档/锚点引入的复杂度对插件作者不友好）。
**结论：`plugin.json`，UTF-8，允许注释以外的宽松（`ReadCommentHandling` 由宿主决定，默认不允许）。**

## 3. 字段一览

### 已实现

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `id` | string | 唯一标识，= 目录名；`^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` |
| `name` | string | 显示名，空则用 id |
| `version` | string | 插件版本，语义化 |
| `description` | string? | 一句话介绍，显示在插件页 |
| `minHostVersion` | string | 要求的最低宿主版本，解析失败视为不限制 |
| `entry` | string | 入口 dll 文件名（相对插件目录，必须是 `.dll`） |
| `entryType` | string | 实现 `IPlugin` 的类型全名 |
| `capabilities` | string[] | 声明能力，见 `PluginCapabilities`；**给用户看的，不是安全边界** |

### 待实现

| 字段 | 类型 | 语义 |
| --- | --- | --- |
| `author` | string? | 作者 |
| `homepage` | string? | 主页 / 仓库地址 |
| `license` | string? | 许可证标识 |
| `tags` | string[] | 分类标签，插件页可筛 |
| `platforms` | string[] | 平台独占。空 = 全平台；`windows` `linux` `macos`。当前平台不在列表里 → `Blocked`，说明「该插件仅支持 …」 |
| `minPlatformVersion` | string? | 平台最低版本（Windows 10 2004 / macOS 12 之类），拿不准时不写 |
| `requires` | object[] | 插件前置：`[{ "id": "...", "minVersion": "1.0.0" }]`。按拓扑序先加载前置；缺失或不满足版本 → 跳过加载并写明原因 |
| `conflicts` | string[] | 与哪些插件互斥（同一功能的两份实现） |
| `settings` | object? | 插件自己的配置项声明，宿主渲染成通用表单（键、类型、默认值、说明）。不加也能用，插件可自己写 UI |

### `requires` 的加载规则（要写清，否则会踩）

1. 先扫全部清单 → 建依赖图。
2. 拓扑排序，前置先加载；**只允许单向**，出现环 → 环上所有插件判 `Blocked`，原因写「存在循环依赖」，
   而不是让宿主去猜。
3. 前置缺失 / 版本不够 / 前置自身加载失败 → 依赖它的插件**不加载**，
   状态标成「缺少前置：xxx」；不让插件在半初始化状态下跑起来（它的代码会调前置的 API，必崩）。
4. 禁用某个插件时，依赖它的插件一起停（插件页里要提前告知，不能静默连坐）。
5. 崩溃跳过同样传染：前置被「疑似崩溃」跳过时，依赖方也跳过，理由指向前置。

### 版本比较

`minHostVersion` 与 `minVersion` 用同一套解析（`AppVersion.Parse`，逐段数字比较，忽略预发布标记）。
写不出来就当没写 —— 插件作者笔误不该让插件永远无法加载，但要记一条警告日志。

## 4. 与现有实现的关系

- 现在的 `PluginManifest`（`HtaciAI.Plugin.Abstractions`）已是「显式 `JsonPropertyName` 绑定」形态，
  加字段是**向后兼容**的：老清单不带新字段，全部走默认值（空列表 / null）。
- `platforms` 为空时的语义要小心：**空 = 不限制**，不是「不支持任何平台」。
- `requires` 引入的顺序问题只影响 `PluginManager` 的加载循环，不影响插件 API。

## 5. 待办

- [ ] 契约加字段（`author` / `homepage` / `license` / `tags` / `platforms` / `requires` / `conflicts`）
- [ ] `PluginManager`：平台校验、拓扑排序、缺失前置的状态与文案
- [ ] 插件页：显示平台限制与依赖关系；禁用时提示会连带停掉谁
- [ ] 示例插件：加一个「依赖示例插件」的最小前置插件，验证顺序与连带停用
