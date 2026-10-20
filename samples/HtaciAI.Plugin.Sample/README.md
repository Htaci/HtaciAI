# 示例插件

用来验证插件加载链路的最小实现，也是写新插件的起点。

## 它是怎么被找到的

宿主启动后扫描 `<数据根>/plugins/` 下的每个子目录，认目录里的 `plugin.json`。
目录名必须等于 `plugin.json` 的 `id`。

## 构建并安装

```powershell
dotnet build samples/HtaciAI.Plugin.Sample -c Release
```

然后把这两个文件（不要带 `HtaciAI.Plugin.Abstractions.dll`）复制到：

```
<数据根>/plugins/htaci.sample/
    HtaciAI.Plugin.Sample.dll
    plugin.json
```

`<数据根>` 在「设置 → 数据管理」里能看到：便携版是程序目录下的 `data`，
否则是 `%APPDATA%\HtaciAI`。设置页的「插件」卡片上有「打开插件目录」按钮。

## 验证

1. 打开「设置 → 常规设置 → 插件」，勾选「启用插件」。
2. 状态行应显示「已启用：1/1 个插件运行中」。
3. 看日志 `<数据根>/logs/plugin.log`，应有两条带 `[htaci.sample]` 的记录。
4. 看 `<数据根>/plugin-data/htaci.sample/stats.json`，`launchCount` 会每次启动加一。
5. 取消勾选 → 再勾上，插件应能在不重启的情况下重新加载（日志里出现新的「启动」记录）。
   若出现「需重启才能完全更新启用状态」，说明插件自己没把后台任务停干净。

## 注意

- 插件目录里**不要**放 `Avalonia*.dll` 或 `HtaciAI*.dll`。宿主会忽略它们并改用自己那份 ——
  带上只会造成两套类型，表现为「控件类型不匹配」之类很难查的问题。
- 插件在宿主进程里运行，不是沙箱。
