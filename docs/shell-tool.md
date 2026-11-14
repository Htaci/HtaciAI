# bash 工具的执行器（shell 选择）

`bash` 工具不是自己解释命令：它把 `command` 原样交给一个**子进程 shell** 去跑。
这个 shell 是哪一个，过去写死在代码里（Windows 上固定 `cmd.exe`，其他平台 `/bin/bash`），
现在可以在设置里选，并且**选中的那个会写进工具描述**给模型看。

## 一句话结论

命令语法依 shell 而变（`%VAR%` / `$env:VAR` / `$VAR` 互不通用）。模型看不见执行器时只能猜，
猜错就得到「引号被吃掉」「命令退出码 1」这类难以归因的失败。所以：让用户选，并把它写进描述。

## 在哪里选

**设置 → 环境配置 → bash 工具**（`Views/Settings/RuntimeSettingsPage`）。
每行一个候选，行上显示检测到的版本与完整路径：

| 行 | 实际程序 | 说明 |
|---|---|---|
| 自动（默认） | Windows：`%ComSpec%`；其他：`/bin/bash` | 等价于升级前的行为 |
| cmd | `cmd.exe` | `/d /s /c` 执行整条命令 |
| Windows PowerShell | `powershell.exe` | 系统自带 5.1，`-NoProfile -NonInteractive -EncodedCommand` |
| PowerShell 7 | `pwsh.exe` / `pwsh` | 需单独安装，支持 `&&` / `\|\|` |
| Git Bash | `Git\bin\bash.exe` 或 `Git\usr\bin\bash.exe` | 找不到时退回 PATH 里的 `bash` |
| bash | PATH 里的 `bash`（WSL / Git Bash / MSYS2） | 以 `-c` 执行整条命令 |

选择存进 `settings.json` 的 `CommandShell`（`ShellKind` 的名字，`null` = 自动）。
**用字符串不用枚举**：`JsonFileStore` 遇到无法识别的枚举值会让整份设置静默回退默认值，
字符串解析失败只影响这一项。

检测本身只在点「重新检测」时跑（`ShellEnvironment.DetectAllAsync`，并发、单个 8 秒超时），
执行路径 `ShellEnvironment.Resolve` 只按设置与固定路径判断，不启动进程、不读版本。

## 工具描述里写了什么

`BuiltinTools.GetAll()` 里 bash 的 `Description` 来自 `ShellEnvironment.BashToolDescription()`，
即每个会话、每次注册都按**当前**执行器生成，例如 cmd 时是：

> 执行 shell 命令。当前执行器：cmd.exe（Windows 命令提示符），命令由它解释执行。
> 命令按 cmd 语法写：&& 连接、%VAR% 取环境变量、字符串用双引号（内层双引号连写成 ""）；
> PowerShell 的 $env:VAR 与单引号字面量在这里无效。要换用别的 shell 就在命令开头显式调用
> （如 powershell -NoProfile -Command "..."、bash -c "..."）。命令在无窗口的子进程里跑：
> 不会弹窗，stdin 已关闭（pause / choice / Read-Host 这类等输入的命令会立刻失败，别指望交互），
> start / explorer / Start-Process 这类「打开一个东西」的命令会被拒绝。可设置工作目录与超时。危险：可影响系统。

换成 PowerShell 7 后同一句话会变成 `$env:VAR` / `&&` 那套提示，模型据此换写法。
设置页点一下选择后立刻 `ToolRegistry.Instance.RegisterBuiltins()`，不重启、不改提示词。

## 执行路径

`BuiltinToolExecutor.RunBashAsync`：

1. 会弹系统窗口的命令（`start` / `explorer` / `rundll32` / `mshta` / `control` / `mmc` / `Start-Process`）
   **直接拒绝**，理由写回给模型。判定只看每段命令（`&&` / `||` / `&` / `|` / `;` / 换行切开）的第一个词，
   `npm start` 这类不算。见 `ShellEnvironment.RejectGuiCommand`。
2. 命令**开头就是** `powershell` / `pwsh`（仅 Windows）→ 单独起那个进程，`-NoProfile -NonInteractive -EncodedCommand`。
3. 否则 → `ShellEnvironment.Resolve()` 给出 shell，由 `EffectiveShell.Configure(psi, command)` 装参数：
   - cmd：`psi.Arguments = "/d /s /c \"<command>\""`（**整串给 Arguments，不走 ArgumentList**）
   - PowerShell 5.1 / 7：`-NoProfile -NonInteractive -EncodedCommand <base64(UTF-16LE)>`
   - bash / Git Bash：`ArgumentList = -c, <command>`
4. 启动后立刻关掉子进程的 stdin。

### 为什么 cmd 不能走 ArgumentList

cmd 的 `/c` 自己按「剥一层外层引号」的规则解析命令行；而 `ArgumentList` 会按 C 语言规则把命令里的
`"` 转成 `\"`，cmd 不认反斜杠转义。于是

```
dir /b "C:\Program Files"
→ cmd /c "dir /b \"C:\Program Files\""
→ cmd 剥外层引号后得到  dir /b \"C:\Program Files\
→ 文件名、目录名或卷标语法不正确
```

`/d` 避开注册表 AutoRun 里被别的软件插进来的命令；`/s` 保证只剥一层外层引号、其余原样，
连「整条命令本身就是带引号的路径」也能正确还原。

## 卡住不返回的几类原因（已处理）

| 原因 | 表现 | 处理 |
|---|---|---|
| `start` 之类弹系统窗口 | 桌面弹出「Windows 找不到 … 文件」，工具永远不返回；模态框不点掉，超时也救不回来 | 直接拒绝这类命令（`RejectGuiCommand`） |
| 命令在等输入（`pause` / `choice` / `Read-Host` / git 要账号） | 一直没有输出，直到超时 | 启动后立刻 `StandardInput.Close()`；PowerShell 另加 `-NonInteractive`。`pause` 实测 30ms 返回 |
| 超时后仍然不返回 | 进程树杀掉了，但孙进程还攥着管道，`ReadToEndAsync` 不完成 | 收尾最多等 2 秒（`TryDrainAsync`） |
| 中文输出全是 `�` | 模型看到乱码路径，接着按乱码拼命令 | 先严格 UTF-8 解，不合法再按系统 OEM 代码页（`ReadTextAsync` / `DecodeOutput`） |
| PowerShell 输出乱码 + stderr 一坨 CLIXML | 5.1 按控制台代码页写 stdout，进度记录以 CLIXML 灌 stderr | 命令前垫 `ShellEnvironment.PowerShellPrefix`：`[Console]::OutputEncoding=UTF8` + `$ProgressPreference='SilentlyContinue'` |

## 与脚本运行时无关

Python / Node（`RuntimeDetector` + `ScriptRuntimeKind`）是**脚本工具**的解释器，
这里选的是**内置 bash 工具**的执行器，两者互不影响，只是都放在环境配置页。

## 排错

| 现象 | 原因 |
|---|---|
| 选中项显示「未找到可执行文件」 | 该 shell 没装或不在 PATH；`pwsh`、Git Bash 在精简系统上常见缺失 |
| 选了 PowerShell 但命令仍按 cmd 报错 | 命令里写的是 cmd 语法；看工具描述里那句「当前执行器」 |
| 命令退出码 1 且 stderr 只有一个引号 | 命令被外层 shell 二次解析；改用命令开头显式调用（`bash -c "…"`） |
| PowerShell 行显示 `System.Collections.Hashtable…` | 探测表达式被插值了；这里已改用 `(Get-Host).Version.ToString()` 规避 |
