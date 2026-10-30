## 零、命名规则（放图标前先看这一节）

两个目录，区分的是**图标画的是谁**：

| 目录 | 放什么 | 举例 |
|---|---|---|
| `Assets/icon/models/` | **模型品牌**图标，也就是 `models.ini` 里 `icon` 字段引用的那一层 | `claude` `qwen` `glm` `grok` `kimi` `gemini` `mimo` `hunyuan` |
| `Assets/icon/providers/` | **厂商/公司**图标，以及 API 服务商图标（目前还没有代码读取，备用） | `anthropic` `alibaba` `zhipu` `tencent` `xiaomi` `ollama` |

同一个名字如果既是厂商又是模型（MiniMax、Mistral、DeepSeek 这类），两个目录各放一份即可。

文件名区分亮暗的规则：

- 带 `-light` / `-dark` 后缀的一对 → 分别放进 `light/` 与 `dark/` 子目录，**去掉后缀**。
- 不带后缀（只有一份、亮暗通用）→ 只放 `light/`。`VendorIcons.Resolve` 在深色下找不到
  `dark/` 里的同名文件时会退回 `light/`，所以不需要复制一份到 `dark/`。
- 文件名一律**小写**：代码里比较名字不区分大小写，但拼出来的 avares 路径是照名字拼的，
  `Qwen.svg` 这种大小写混排会在资源查找时踩坑。

改完之后必须重跑生成器（见第三节）。

---

## 二、模型服务商图标（`providers/`）

当前只有 13 个（浅色 11 个、深色 4 个）：`alibaba` `amazon` `anthropic` `baidu` `ollama`
`tencent` `xai` `xiaomi` `zhipu`（浅色）；`amazon` `anthropic` `ollama` `xai`（深色）。

下面 2.1 / 2.2 那两份长名单是**历史上的完整清单**，本身来自 cherry-studio（AGPL），
已随图标一起移除，只作为「以后要补哪些」的参照，不代表现在存在。

**这些目前没有被任何代码使用。** 服务商卡片用的是「名称首字 + 稳定底色」的自绘头像
（`ModelServicePage.CreateAvatar`），不读图标文件。所以这批图标优先级最低，
甚至可以不补 —— 除非以后要把服务商头像也换成真 logo。

### 2.1 浅色（162 个）

- `302ai.svg`
- `3min-top.svg`
- `abacus.svg`
- `ai-only.svg`
- `ai-studio.svg`
- `ai21.svg`
- `aihubmix.svg`
- `aionlabs.svg`
- `alayanew.svg`
- `allenai.svg`
- `anthropic.svg`
- `application.svg`
- `arcee-ai.svg`
- `aws-bedrock.svg`
- `azureai.svg`
- `baai.svg`
- `baichuan.svg`
- `baidu.svg`
- `baidu-cloud.svg`
- `bailian.svg`
- `bfl.svg`
- `bocha.svg`
- `bolt-new.svg`
- `burncloud.svg`
- `bytedance.svg`
- `cephalon.svg`
- `cerebras.svg`
- `cherryin.svg`
- `claude-code.svg`
- `cloudflare.svg`
- `cohere.svg`
- `comfyui.svg`
- `composio.svg`
- `coze.svg`
- `dangbei.svg`
- `dashscope.svg`
- `databricks.svg`
- `deepcogito.svg`
- `deepseek.svg`
- `devv.svg`
- `dify.svg`
- `dmxapi.svg`
- `doc2x.svg`
- `dola.svg`
- `dolphin-ai.svg`
- `doubao.svg`
- `duck.svg`
- `elevenlabs.svg`
- `essential-ai.svg`
- `exa.svg`
- `felo.svg`
- `firecrawl.svg`
- `fireworks.svg`
- `flowith.svg`
- `genspark.svg`
- `gitee-ai.svg`
- `github.svg`
- `github-copilot.svg`
- `glama.svg`
- `google.svg`
- `gpustack.svg`
- `grok.svg`
- `groq.svg`
- `higress.svg`
- `huggingface.svg`
- `hyperbolic.svg`
- `ideogram.svg`
- `ima.svg`
- `inceptionlabs.svg`
- `infini.svg`
- `inflection.svg`
- `intel.svg`
- `internlm.svg`
- `jimeng.svg`
- `jina.svg`
- `kling.svg`
- `kwaipilot.svg`
- `lambda.svg`
- `lanyun.svg`
- `lepton.svg`
- `lingxi.svg`
- `liquid.svg`
- `litellm.svg`
- `lmstudio.svg`
- `longcat.svg`
- `macos.svg`
- `mcp.svg`
- `mcpso.svg`
- `meta.svg`
- `metaso.svg`
- `mineru.svg`
- `minimax.svg`
- `minimax-agent.svg`
- `mistral.svg`
- `mixedbread.svg`
- `modelscope.svg`
- `monica.svg`
- `moonshot.svg`
- `n8n.svg`
- `nami-ai.svg`
- `netease-youdao.svg`
- `newapi.svg`
- `nomic.svg`
- `notebooklm.svg`
- `nousresearch.svg`
- `nvidia.svg`
- `ocoolai.svg`
- `ollama.svg`
- `omlx.svg`
- `openai.svg`
- `openclaw.svg`
- `openrouter.svg`
- `paddleocr.svg`
- `parallel.svg`
- `perplexity.svg`
- `ph8.svg`
- `poe.svg`
- `ppio.svg`
- `pulse.svg`
- `qiniu.svg`
- `querit.svg`
- `qwen.svg`
- `recraft.svg`
- `relace.svg`
- `riverflow.svg`
- `runway.svg`
- `searxng.svg`
- `sensetime.svg`
- `serply.svg`
- `silicon.svg`
- `skywork.svg`
- `smithery.svg`
- `sophnet.svg`
- `stability.svg`
- `step.svg`
- `streamlake.svg`
- `suno.svg`
- `tavily.svg`
- `tencent-cloud-ti.svg`
- `tesseract-js.svg`
- `think-any.svg`
- `tng.svg`
- `together.svg`
- `tokendance.svg`
- `twitter.svg`
- `upstage.svg`
- `vercel.svg`
- `vertexai.svg`
- `vidu.svg`
- `volcengine.svg`
- `voyage.svg`
- `wenxin.svg`
- `workers-ai.svg`
- `xiaoyi.svg`
- `xinghuo.svg`
- `xirang.svg`
- `you.svg`
- `yuanbao.svg`
- `z-ai.svg`
- `zero-one.svg`
- `zhida.svg`
- `zhipu.svg`

### 2.2 深色（50 个）

- `ai-studio.svg`
- `ai21.svg`
- `baai.svg`
- `bfl.svg`
- `bolt-new.svg`
- `cephalon.svg`
- `cerebras.svg`
- `composio.svg`
- `devv.svg`
- `dolphin-ai.svg`
- `elevenlabs.svg`
- `flowith.svg`
- `genspark.svg`
- `gitee-ai.svg`
- `github.svg`
- `github-copilot.svg`
- `glama.svg`
- `grok.svg`
- `ideogram.svg`
- `inceptionlabs.svg`
- `inflection.svg`
- `internlm.svg`
- `lambda.svg`
- `liquid.svg`
- `litellm.svg`
- `macos.svg`
- `mcp.svg`
- `mineru.svg`
- `minimax.svg`
- `moonshot.svg`
- `nami-ai.svg`
- `nomic.svg`
- `notebooklm.svg`
- `nousresearch.svg`
- `ocoolai.svg`
- `ollama.svg`
- `openai.svg`
- `openrouter.svg`
- `parallel.svg`
- `poe.svg`
- `recraft.svg`
- `relace.svg`
- `riverflow.svg`
- `runway.svg`
- `suno.svg`
- `tesseract-js.svg`
- `twitter.svg`
- `vercel.svg`
- `voyage.svg`
- `z-ai.svg`

### 2.3 只有深色、没有浅色的（0 个）

无。

---

## 三、替换后要做的事

图标放进 `models/light`（需要深色版再放 `models/dark`）之后，**必须重跑生成器**：

```bash
dotnet run --project tools/ModelTableGen
```

生成器会扫描这两个目录，把实际存在的文件名写进 `BuiltinModelTable.g.cs` 的
`IconNamesLight` / `IconNamesDark`。`VendorIcons.Resolve` 判断「有没有这个图标」
靠的就是这两张名单（打包成 avares 之后查不了文件系统，只能在生成期固化）。

**不重跑生成器的话，图标放了也不会生效** —— 名单里没有它，解析会返回空。

### 渲染

SVG 由 `Svg.Controls.Skia.Avalonia` 渲染（`HtaciAI.csproj` 里的包引用）。
统一入口是 `Controls/VendorAvatar.cs`：

```csharp
// 有图标就返回一个 16/20/40px 的 Image，没有就返回 null（调用方留空，不给空白方块）
if (VendorAvatar.CreateLogo(model.Vendor, 16) is { } logo) row.Children.Add(logo);
```

`SvgSource` 按资源路径缓存 —— 设置页里模型行动辄几十条，不缓存的话每次重建都要重新解析 XML。
解析失败按「没有图标」处理，坏一个文件不该让整页崩掉。

接上的位置：
- `Views/Settings/ModelServicePage.axaml.cs` —— 模型列表行，模型名前 16px logo；
- `Views/Settings/ModelEditWindow.axaml.cs` —— 改「开发商」时旁边实时预览 16px logo。

菜单（模型选择器）**刻意不加图标**：菜单行本来就密，加图标反而更乱。

### 当前状态

`models/light` 22 个、`models/dark` 6 个；`providers/light` 11 个、`providers/dark` 4 个。

21 个开发商里 **20 个有模型图标**，只差 `nousresearch`（表里没有覆盖 `icon`，
所以文件名必须就是 `nousresearch.svg`）。`providers/` 那批目前没有代码读取
（服务商头像仍用「首字 + 底色」的自绘头像），留着备用。

