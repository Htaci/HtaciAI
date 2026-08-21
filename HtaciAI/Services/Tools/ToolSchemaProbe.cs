using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HtaciAI.Services.ScriptRuntimes;

namespace HtaciAI.Services.Tools;

/// <summary>
/// 脚本工具的 --describe 协议探测：运行 {解释器} {脚本} --describe，
/// 脚本在 stdout 输出 {"name": "...", "description": "...", "parameters": {...}} JSON，
/// 用于"创建新工具"时自动生成工具定义（name / description / 入参 JSON Schema）。
/// </summary>
public static class ToolSchemaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<ToolSchemaProbeResult> ProbeAsync(
        ScriptRuntimeKind kind, string scriptPath, CancellationToken ct = default)
    {
        var probe = await RuntimeDetector.ProbeAsync(kind);
        if (!probe.Found || string.IsNullOrWhiteSpace(probe.ResolvedPath))
            return new ToolSchemaProbeResult
            {
                Success = false,
                Error = $"未检测到 {RuntimeDetector.DisplayName(kind)} 运行时，请到「设置 → 环境配置」检查",
            };

        try
        {
            var psi = new ProcessStartInfo(probe.ResolvedPath, $"\"{scriptPath}\" --describe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = new Process { StartInfo = psi };
            if (!p.Start())
                return new ToolSchemaProbeResult { Success = false, Error = $"无法启动解释器：{probe.ResolvedPath}" };

            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            using var timeoutCts = new CancellationTokenSource(Timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await p.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (ct.IsCancellationRequested)
                    return new ToolSchemaProbeResult { Success = false, Error = "探测已取消" };
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                return new ToolSchemaProbeResult { Success = false, Error = $"探测超时（>{Timeout.TotalSeconds:0}s）" };
            }

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (p.ExitCode != 0 || string.IsNullOrEmpty(stdout))
                return new ToolSchemaProbeResult
                {
                    Success = false,
                    Error = string.IsNullOrWhiteSpace(stderr) ? $"脚本 --describe 无输出（退出码 {p.ExitCode}）" : stderr,
                };

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var desc = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            var parameters = root.TryGetProperty("parameters", out var pa) ? pa.GetRawText() : "{}";

            if (string.IsNullOrWhiteSpace(name))
                return new ToolSchemaProbeResult { Success = false, Error = "脚本 --describe 输出缺少 name 字段" };

            return new ToolSchemaProbeResult
            {
                Success = true,
                Name = name.Trim(),
                Description = desc.Trim(),
                ParametersJson = parameters,
            };
        }
        catch (JsonException)
        {
            return new ToolSchemaProbeResult { Success = false, Error = "脚本 --describe 输出不是有效 JSON" };
        }
        catch (Exception ex)
        {
            return new ToolSchemaProbeResult { Success = false, Error = ex.Message };
        }
    }
}

/// <summary>一次 --describe 探测的结果。</summary>
public sealed class ToolSchemaProbeResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string ParametersJson { get; init; } = "{}";
}
