using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace HtaciAI.Services.KnowledgeBase;

/// <summary>
/// 本地嵌入推理：Qwen3-Embedding 0.6B 的 ONNX 权重 + <see cref="QwenTokenizer"/>。
///
/// <b>池化方式照官方，不是 BERT 的 mean</b>：Qwen3 是 decoder，取<b>最后一个 token</b> 的隐状态，
/// 再 L2 归一化。做成 mean pooling 不会报错，只是相似度整体变差 —— 那种错误没有异常可抓，
/// 只能靠对照官方实现发现，所以这里写死官方口径并在注释里留痕。
///
/// <b>查询要带指令、文档不带</b>（见 <see cref="EmbedQuery"/> / <see cref="EmbedDocuments"/>）：
/// 官方给出的是 <c>Instruct: {任务}\nQuery:{内容}</c>。只在一侧加会让两次向量落在不同的分布上，
/// 检索分数整体塌下去。两侧都加或都不加都好过只加一侧。
///
/// <b>一次一条，不做 batch padding</b>：切片本身只有几百个 token，padding 到 batch 内最长一条
/// 反而多算一大截无效位置；而 decoder 的注意力是 O(L²)，无效位置也是真金白银的算力。
/// </summary>
public static class LocalEmbedder
{
    /// <summary>单条最多多少 token。切片上限 700 字符，正常只有几百；这里给查询留余量。</summary>
    private const int MaxTokens = 1024;

    /// <summary>查询侧的任务说明。与官方 MTEB 用法一致，只影响查询向量。</summary>
    private const string QueryInstruction = "Given a web search query, retrieve relevant passages that answer the query";

    private static readonly object Gate = new();
    private static InferenceSession? _session;
    private static QwenTokenizer? _tokenizer;
    private static string? _outputName;
    private static string? _loadError;

    /// <summary>模型文件是否齐备（不加载）＋是否已经加载成功。界面据此决定能不能选它。</summary>
    public static bool IsInstalled => LocalEmbeddingModel.Detect().Installed;

    /// <summary>
    /// 把查询语句嵌成向量（带指令前缀）。
    /// </summary>
    public static float[] EmbedQuery(string text)
        => EmbedOne($"{QueryInstruction}\nQuery:{text}");

    /// <summary>把一组文档嵌成向量（不加指令）。返回顺序与入参一致。</summary>
    public static List<float[]> EmbedDocuments(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var text in texts)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(EmbedOne(text));
        }
        return result;
    }

    /// <summary>阻塞式单条推理。调用方负责放到后台线程（见 <see cref="EmbedBatchAsync"/>）。</summary>
    private static float[] EmbedOne(string text)
    {
        var (session, tokenizer, outputName) = EnsureLoaded();

        var ids = tokenizer.Encode(text, MaxTokens);
        if (ids.Length == 0) ids = new[] { tokenizer.Encode(" ", MaxTokens).FirstOrDefault() };

        var length = ids.Length;
        var idsTensor = new DenseTensor<long>(new[] { 1, length });
        var maskTensor = new DenseTensor<long>(new[] { 1, length });
        for (var i = 0; i < length; i++)
        {
            idsTensor[0, i] = ids[i];
            maskTensor[0, i] = 1;
        }

        // 输入名按 session 实际声明的来：不同导出工具的命名不一样（input_ids / attention_mask /
        // position_ids / token_type_ids 都有可能出现），写死两个名字会在某个导出上直接报「缺少输入」。
        //
        // past_key_values 必须喂「空的」，不能省：它们在图上不是可选输入，缺了会在 Concat 节点上
        // 直接报 Missing Input。喂一个第三维为 0 的张量（[1, 8, 0, 128]）就等于告诉模型
        // 「没有缓存」，与增量解码的第一步完全等价 —— 嵌入本来也不需要缓存。
        var inputs = new List<NamedOnnxValue>(session.InputMetadata.Count);
        foreach (var pair in session.InputMetadata)
        {
            var name = pair.Key;

            if (name.Contains("past_key", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, EmptyCache(pair.Value.Dimensions)));
                continue;
            }

            if (pair.Value.ElementType != typeof(long)) continue;

            if (name.Contains("attention", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, maskTensor));
            }
            else if (name.Contains("position", StringComparison.OrdinalIgnoreCase))
            {
                var posTensor = new DenseTensor<long>(new[] { 1, length });
                for (var i = 0; i < length; i++) posTensor[0, i] = i;
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, posTensor));
            }
            else if (name.Contains("token_type", StringComparison.OrdinalIgnoreCase))
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(new[] { 1, length })));
            }
            else
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, idsTensor));
            }
        }

        using var results = session.Run(inputs, new[] { outputName });
        var output = results.First().AsTensor<float>();

        var dims = output.Dimensions;
        var hidden = dims[dims.Length - 1];

        var vector = new float[hidden];
        if (dims.Length == 2)
        {
            // 已经是句向量（[1, hidden]），直接用
            for (var i = 0; i < hidden; i++) vector[i] = output[0, i];
        }
        else
        {
            // [1, seq, hidden]：取最后一个位置。不做 mean —— 见类注释
            var last = length - 1;
            for (var i = 0; i < hidden; i++) vector[i] = output[0, last, i];
        }

        Normalize(vector);
        return vector;
    }

    /// <summary>后台线程跑批量，避免卡住界面线程（一次嵌入几百毫秒，切片多了就是几十秒）。</summary>
    public static Task<List<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        => Task.Run(() => EmbedDocuments(texts, ct), ct);

    /// <summary>预热：装好之后第一次调用要加载 585MB 的模型，提前加载能避免用户以为卡死。</summary>
    public static Task WarmUpAsync() => Task.Run(() =>
    {
        try { EnsureLoaded(); } catch { /* 预热失败留给真正调用时报错 */ }
    });

    private static (InferenceSession Session, QwenTokenizer Tokenizer, string OutputName) EnsureLoaded()
    {
        lock (Gate)
        {
            if (_session is not null && _tokenizer is not null && _outputName is not null)
                return (_session, _tokenizer, _outputName);

            if (_loadError is not null) throw new EmbeddingException(_loadError);

            var modelPath = LocalEmbeddingModel.ModelPath;
            var dir = LocalEmbeddingModel.DirectoryPath;

            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // 留一个核给界面。全核吃满时窗口会明显卡顿，而这只是后台建索引
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount - 1, 1, 4),
                };

                var session = new InferenceSession(modelPath, options);
                var tokenizer = new QwenTokenizer(dir);
                var outputName = PickOutput(session);

                _session = session;
                _tokenizer = tokenizer;
                _outputName = outputName;
                return (session, tokenizer, outputName);
            }
            catch (Exception ex)
            {
                _loadError = $"本地嵌入模型加载失败：{ex.Message}。可以到「设置 → 环境配置」重新下载一次。";
                throw new EmbeddingException(_loadError);
            }
        }
    }

    /// <summary>
    /// 从输出里挑「最后隐状态」：优先名字，其次按形状推断。
    ///
    /// 为什么不能直接取第一个：decoder 导出的输出常常不止一个（还有 <c>logits</c>、
    /// 甚至带 <c>past_key_values.*</c>）。取错形状不报错，只会在取下标时越界或算出一个没意义的向量。
    /// </summary>
    private static string PickOutput(InferenceSession session)
    {
        foreach (var pair in session.OutputMetadata)
        {
            if (pair.Key.Contains("last_hidden", StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains("sentence_embedding", StringComparison.OrdinalIgnoreCase))
                return pair.Key;
        }

        foreach (var pair in session.OutputMetadata)
        {
            var dims = pair.Value.Dimensions;
            if (dims.Length == 3 && dims[2] == 1024) return pair.Key;
            if (dims.Length == 2 && dims[1] == 1024) return pair.Key;
        }

        throw new EmbeddingException(
            $"这个 ONNX 模型没有 1024 维的输出（实际输出：{string.Join("、", session.OutputMetadata.Keys)}）。" +
            "可能是下载到了别的模型文件。");
    }

    /// <summary>
    /// 造一个「没有缓存」的 KV 张量。形状取自图里声明的（<c>[-1, 8, -1, 128]</c>），
    /// 把 batch 定成 1、序列维定成 0 —— 0 长度表示空，模型据此做一次完整的前向。
    /// </summary>
    private static DenseTensor<float> EmptyCache(int[] declared)
    {
        var dims = new int[declared.Length];
        for (var i = 0; i < declared.Length; i++)
        {
            // 第 0 维是 batch，第 2 维是序列长度（要 0）；其余静态维照抄
            dims[i] = i switch
            {
                0 => 1,
                2 => 0,
                _ => declared[i] > 0 ? declared[i] : 1,
            };
        }
        return new DenseTensor<float>(dims);
    }

    /// <summary>L2 归一化。归一化之后余弦相似度就退化成点积，检索侧可以直接乘加。</summary>
    private static void Normalize(float[] vector)
    {
        var sum = 0.0;
        foreach (var v in vector) sum += (double)v * v;

        var norm = Math.Sqrt(sum);
        if (norm <= 0) return;

        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
    }

    /// <summary>设置里选中本地模型后用的模型 id（与 API 模型 id 同一个字段，靠前缀区分）。</summary>
    public const string OptionId = "local:" + LocalEmbeddingModel.Id;

    /// <summary>这个 id 是不是「本地模型」。</summary>
    public static bool IsLocal(string? modelId)
        => string.Equals(modelId, OptionId, StringComparison.OrdinalIgnoreCase);
}
