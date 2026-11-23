using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Animanager.Core.Jobs;

namespace Animanager.Core.Plugins;

internal sealed class CascadeurPlugin(string exchangeRoot, string script) : IAnimationPlugin
{
    private const int Version = 3;
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(8);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Id => "cascadeur";
    public string Name => "Cascadeur";
    public string ConnectionStatus => IsConnected ? "接收插件已连接" : "等待接收插件连接";
    public AnimationPluginSetup Setup => new(
        "安装脚本后，在 Cascadeur 重新加载命令或重启，然后选择 Commands → Animanager → Toggle connection。",
        "选择包含 cascadeur.exe 的安装目录", Install);

    public static CascadeurPlugin Create() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Animanager", "Plugins", "Cascadeur", $"v{Version}"),
        RuntimePaths.Bundled("BuiltinAssets/Scripts/animanager_cascadeur.py"));

    private bool IsConnected => Directory.Exists(exchangeRoot) && Directory.EnumerateFiles(exchangeRoot, "receiver-*.ready")
        .Any(path => DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < Freshness);

    private void Install(string directory)
    {
        var commands = Path.Combine(directory, "resources", "scripts", "python", "commands");
        if (!File.Exists(Path.Combine(directory, "cascadeur.exe")) || !Directory.Exists(commands))
            throw new DirectoryNotFoundException("请选择包含 cascadeur.exe 和 Python 命令目录的 Cascadeur 安装目录。");

        var destination = Path.Combine(commands, Path.GetFileName(script));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(script, temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public Task<AnimationPluginResult> SendAsync(AnimationPluginRequest request, IProgress<string> progress, CancellationToken ct) =>
        SendAsync(request, progress, ct, null);

    public async Task<AnimationPluginResult> SendAsync(AnimationPluginRequest request, IProgress<string> progress, CancellationToken ct,
        Func<CascadeurBindPose, CancellationToken, Task<Quaternion?>>? configure)
    {
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(exchangeRoot, "transfers", id);
        progress.Report("正在为 Cascadeur 导出 · " + request.Clip);
        var output = await request.ExportAsync(folder, JobKind.ExportGlb, ct);
        ct.ThrowIfCancellationRequested();

        CascadeurBindPose pose;
        try { pose = await Task.Run(() => CascadeurBindPose.Read(output), ct); }
        catch (InvalidDataException) { pose = CascadeurBindPose.Empty; }
        var orientation = configure == null ? Quaternion.Identity : await configure(pose, ct);
        if (orientation is not { } rotation)
        {
            if (Path.GetDirectoryName(output) == folder) File.Delete(output);
            return new("已取消发送到 Cascadeur。", "");
        }
        if (rotation != Quaternion.Identity)
            await Task.Run(() => CascadeurBindPose.RotateExport(output, rotation), ct);
        ct.ThrowIfCancellationRequested();

        var inbox = Path.Combine(exchangeRoot, "inbox");
        var pending = Path.Combine(inbox, id + ".json");
        var receipt = Path.Combine(folder, "result.json");
        var claimed = Path.Combine(folder, "processing.json");
        var withdrawn = Path.Combine(folder, "withdrawn.json");
        try
        {
            if (!IsConnected) return new("Cascadeur 接收插件未连接，GLB 已导出。", output);

            var requestLifetime = TimeSpan.FromSeconds(15);
            var expires = DateTimeOffset.UtcNow.Add(requestLifetime).ToUnixTimeMilliseconds();
            Directory.CreateDirectory(inbox);
            var staged = Path.Combine(folder, "request.tmp");
            File.WriteAllText(staged, JsonSerializer.Serialize(
                new TransferRequest(Version, Path.GetFileName(output), expires, pose.ToTransfer(rotation)), Json));
            File.Move(staged, pending);
            progress.Report("已导出 GLB，等待 Cascadeur 接收 · " + request.Clip);

            var wait = Stopwatch.StartNew();
            bool receiving = false;
            while (wait.Elapsed < TimeSpan.FromSeconds(90))
            {
                ct.ThrowIfCancellationRequested();
                if (File.Exists(receipt))
                {
                    var result = JsonSerializer.Deserialize<TransferReceipt>(File.ReadAllText(receipt), Json);
                    if (result is not { Imported: true })
                        return new("Cascadeur 导入失败 · " + (result?.Error ?? "接收插件未返回详情。"), output);
                    return result.Rig is { Length: > 0 } rig
                        ? new($"已发送到 Cascadeur · {request.Clip} · {rig} AutoPosing 已就绪", output)
                        : new("Cascadeur 已导入动画，AutoPosing 未完成 · " + (result.RigIssue ?? "未返回 Rig 状态。"), output);
                }

                if (!receiving && File.Exists(claimed))
                {
                    receiving = true;
                    progress.Report("Cascadeur 已接收，正在导入 · " + request.Clip);
                }
                if (!receiving && wait.Elapsed >= requestLifetime && Withdraw(pending, withdrawn))
                    return new("Cascadeur 未接收任务，GLB 已导出。", output);
                await Task.Delay(200, ct);
            }

            return new("Cascadeur 未回报导入结果，请检查场景标签页。", output);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new("Cascadeur 连接出错 · " + error.Message, output);
        }
        finally { Withdraw(pending, withdrawn); }
    }

    // The receiver and sender compete to rename the same file. Only the winner owns the request.
    private static bool Withdraw(string pending, string withdrawn)
    {
        try { File.Move(pending, withdrawn); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record TransferRequest(int Version, string File, long ExpiresUnixMilliseconds, CascadeurWireJoint[] BindPose);

    private sealed record TransferReceipt(bool Imported, string? Error, string? Rig, string? RigIssue);
}
