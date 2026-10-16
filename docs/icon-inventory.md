## 一、模型开发商图标（`models/`）

### 1.1 当前内置表**必需**的 21 个

这张清单来自 `Services/ModelTable/models.ini` 的开发商段（`icon` 覆盖优先于开发商 id）。
**补图标时优先补这些** —— 缺哪个，哪个开发商的模型就没有头像。

| # | 开发商 id | 展示名 | 需要的图标名 | 曾有(浅色) | 曾有(深色) |
|---|---|---|---|---|---|
| 1 | `anthropic` | Anthropic | `claude.svg` | ✓ | — |
| 2 | `openai` | OpenAI | `openai.svg` | ✓ | ✓ |
| 3 | `google` | Google | `google.svg` | ✓ | — |
| 4 | `deepseek` | DeepSeek | `deepseek.svg` | ✓ | — |
| 5 | `alibaba` | 阿里通义 | `qwen.svg` | ✓ | — |
| 6 | `moonshot` | 月之暗面 | `kimi.svg` | ✓ | ✓ |
| 7 | `zhipu` | 智谱 | `glm.svg` | ✓ | — |
| 8 | `xai` | xAI | `grok.svg` | ✓ | ✓ |
| 9 | `meta` | Meta | `meta.svg` | ✓ | — |
| 10 | `mistral` | Mistral | `mistral.svg` | **缺** | — |
| 11 | `bytedance` | 字节跳动 | `bytedance.svg` | ✓ | — |
| 12 | `tencent` | 腾讯 | `hunyuan.svg` | ✓ | — |
| 13 | `minimax` | MiniMax | `minimax.svg` | ✓ | — |
| 14 | `xiaomi` | 小米 | `mimo.svg` | ✓ | ✓ |
| 15 | `baidu` | 百度 | `wenxin.svg` | ✓ | — |
| 16 | `nvidia` | NVIDIA | `nvidia.svg` | ✓ | — |
| 17 | `amazon` | Amazon | `nova.svg` | ✓ | — |
| 18 | `microsoft` | 微软 | `microsoft.svg` | ✓ | — |
| 19 | `baai` | 智源 | `baai.svg` | ✓ | ✓ |
| 20 | `meituan` | 美团 | `longcat.svg` | ✓ | ✓ |
| 21 | `nousresearch` | Nous Research | `nousresearch.svg` | ✓ | ✓ |

浅色已有 20/21，深色已有 7/21。
深色缺失不是问题：解析时会退回浅色图标（见 `VendorIcons.Resolve`）。

### 1.2 其余曾有、当前表未使用的模型图标（10 个）

这些大多是**同一厂商的另一种品牌写法**（如 `anthropic` vs `claude`、`google` vs `gemini`、
`moonshot` vs `kimi`），留着备用即可，不是必须补。

| 图标名 | 说明 |
|---|---|
| `anthropic.svg` | Anthropic 公司图标（当前用品牌图标 `claude`） |
| `aws.svg` | 亚马逊云 / Bedrock |
| `bilibili.svg` | 哔哩哔哩 |
| `codegeex.svg` | 智谱的代码模型品牌（当前用 `glm`） |
| `gemini.svg` | Google 模型品牌（当前用 `google`） |
| `gemma.svg` | Google 开源模型线 |
| `jimeng.svg` | 字节即梦 |
| `moonshot.svg` | 月之暗面公司图标（当前用品牌图标 `kimi`） |
| `sora.svg` | OpenAI 视频模型 |
| `tencent.svg` | 腾讯公司图标（当前用品牌图标 `hunyuan`） |

另有 11 个深色图标：

`anthropic` `aws` `baai` `grok` `kimi` `longcat` `mimo` `moonshot` `nousresearch` `openai` `zai`

---

## 二、模型服务商图标（`providers/`）

共 162 个名字（浅色 162 个，深色 50 个）。

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

另外注意：项目目前**没有 SVG 渲染器**（依赖里没有 `Avalonia.Svg.Skia`），
所以即使图标到位，也还显示不出来。要让头像真正出现，需要先引入该依赖，
然后在 UI 里把 `VendorIcons.Resolve(…)` 返回的路径加载成图片。

