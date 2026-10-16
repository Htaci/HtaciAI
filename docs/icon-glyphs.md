# 图标字形映射（Fluent System Icons）

图标字体为 **Fluent System Icons**（`HtaciAI/Assets/Fonts/FluentSystemIcons-Regular.ttf`，MIT，
许可证见同目录 `LICENSE-FluentSystemIcons.txt`）。代码与 XAML 里统一通过
`AppFonts.Icons` / `{StaticResource IconFont}` 引用，勿硬编码族名。

## 为什么不是 Segoe Fluent Icons

Segoe Fluent Icons 是微软专有字体，其 EULA 只授权在**微软平台上的程序**里使用，
并明确禁止再分发。把字体内置进跨平台应用发布，等于把字体分发给第三方，属于侵权。
Fluent System Icons 同为微软出品、观感一致，但以 MIT 发布，可自由再分发。

## 换字体时必读

**两套字体的私用区（PUA）码位分配毫无关系**，不能只替换字体文件——
每一处字形都要按语义逐个重映射，否则会显示成完全不相干的图标或方框。
下表是本次从 Segoe 迁移到 Fluent 时的完整对应关系，可作为再次更换时的语义索引。

| Segoe 码位 | 语义 | Fluent 图标名 | Fluent 码位 |
|---|---|---|---|
| U+E70D | ChevronDown | `chevron_down_24_regular` | U+F2A4 |
| U+E70E | ChevronUp | `chevron_up_24_regular` | U+F2B7 |
| U+E70F | Edit(pencil) | `edit_24_regular` | U+F3DE |
| U+E710 | Add(+ ) | `add_24_regular` | U+F10A |
| U+E711 | Cancel(x) | `dismiss_24_regular` | U+F36A |
| U+E712 | More(...) | `more_horizontal_24_regular` | U+E825 |
| U+E713 | Settings(gear) | `settings_24_regular` | U+F6AA |
| U+E721 | Search | `search_24_regular` | U+F690 |
| U+E723 | Attach(paperclip) | `attach_24_regular` | U+F1AA |
| U+E724 | Send | `send_24_regular` | U+F69A |
| U+E72C | Refresh | `arrow_clockwise_24_regular` | U+F13E |
| U+E73E | CheckMark | `checkmark_24_regular` | U+F295 |
| U+E74A | ArrowUp | `arrow_up_24_regular` | U+F19C |
| U+E74D | Delete(trash) | `delete_24_regular` | U+F34D |
| U+E74E | Save(floppy) | `save_24_regular` | U+F680 |
| U+E76C | ChevronRight | `chevron_right_24_regular` | U+F2B1 |
| U+E794 | Certificate | `book_24_regular` | U+F8FE |
| U+E7B8 | Package(box) | `box_24_regular` | U+E1BC |
| U+E7BE | Education(cap) | `hat_graduation_24_regular` | U+E6E7 |
| U+E7C3 | Page(file) | `document_24_regular` | U+F379 |
| U+E838 | FolderOpen | `folder_open_24_regular` | U+F42F |
| U+E895 | Sync(regenerate) | `arrow_sync_24_regular` | U+F191 |
| U+E896 | Download | `arrow_download_24_regular` | U+F151 |
| U+E8A5 | Document | `document_24_regular` | U+F379 |
| U+E8AC | Rename | `rename_24_regular` | U+F66A |
| U+E8AF | Remote(MCP) | `plug_connected_24_regular` | U+E99A |
| U+E8B7 | Folder | `folder_24_regular` | U+F419 |
| U+E8BB | Close(x) | `dismiss_24_regular` | U+F36A |
| U+E8C8 | Copy | `copy_24_regular` | U+F32C |
| U+E8F2 | Chat | `chat_24_regular` | U+F287 |
| U+E90F | Repair(wrench) | `wrench_24_regular` | U+F8C1 |
| U+E916 | Stopwatch | `timer_24_regular` | U+F827 |
| U+E921 | Minimize | `subtract_24_regular` | U+EBD1 |
| U+E922 | Maximize | `square_24_regular` | U+EB76 |
| U+E923 | Restore | `square_multiple_24_regular` | U+F78C |
| U+E932 | Edit(square) | `edit_24_regular` | U+F3DE |
| U+E943 | Code(braces) | `code_24_regular` | U+F2F0 |
| U+E950 | Chip | `developer_board_24_regular` | U+F35C |
| U+EA18 | Shield | `shield_24_regular` | U+F6BF |
| U+EA80 | Lightbulb | `lightbulb_24_regular` | U+F4D7 |
| U+EB9F | Picture | `image_24_regular` | U+F489 |
| U+F158 | Cube | `cube_24_regular` | U+F336 |
| U+F5ED | Copy | `copy_24_regular` | U+F32C |

## 在 XAML 里写字形

XAML 用 XML 实体（可读、不会被编辑器吞掉）：

```xml
<!-- 新建（add） -->
<TextBlock Text="&#xF10A;" FontFamily="{StaticResource IconFont}"/>
```

## 在 C# 里写字形

C# 用 `\u` 转义：

```csharp
// 新建（add）
var add = new TextBlock { Text = "\uF10A", FontFamily = AppFonts.Icons };
```

> 私用区字符在多数编辑器里**不可见**，务必写成上表的转义 / 实体形式，
> 不要直接粘贴字形本身，否则后续无人能看出那是什么图标。

## 后续调整记录

首批迁移按「语义直译」选的图标里，有两个在小尺寸（工具栏 14px）下可读性差，已改为更易辨识的：

| 用途 | 初选（语义直译） | 现用 | 原因 |
|---|---|---|---|
| 技能库 | `certificate`（证书卡片） | `book` | 证书在 14px 下糊成一块，读起来像扫描仪；书本更干净且贴合「技能库」 |
| MCP 服务 | `cloud`（云） | `plug_connected`（插头） | 云太泛，和「云服务」无区别；插头点出「接入外部服务」且辨识度高 |

换图标时除了改这里的表，还要同步这四处：
`Controls/SkillSelectorControl.axaml`、`Controls/McpSelectorControl.axaml`、
`Views/NewTabPage.axaml.cs` 的技能卡与 MCP 卡（卡片与选择器是刻意保持一致的）。

## 后加的图标（无 Segoe 对应）

这些是原生设计在 Fluent 上的，不是从 Segoe 迁移来的，所以没有 Segoe 码位可对照。
**码位一律从字体文件的 cmap 里读，不要凭印象写**——Fluent 的字形码位是算出来的，
不是按语义顺序排的，猜错会显示成完全不相干的图标或方框：

```bash
uv run --with fonttools python -c "
from fontTools.ttLib import TTFont
f = TTFont('HtaciAI/Assets/Fonts/FluentSystemIcons-Regular.ttf')
rev = {n: cp for cp, n in f.getBestCmap().items()}
print(hex(rev['ic_fluent_warning_24_regular']))"
```

| 语义 | Fluent 图标名 | Fluent 码位 | 用在何处 |
|---|---|---|---|
| 警告（配置需补充） | `warning_24_regular` | U+F86A | `Views/Settings/ModelServicePage.axaml.cs` 模型行的黄叹号 |
| 未知（表里没有） | `question_circle_24_regular` | U+F63E | 同上的灰问号 |

这两个提示图标点开都会跳到该模型的编辑页，补完保存即消失（见 `docs/model-table.md`）。
