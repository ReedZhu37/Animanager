using Animanager.Core.Jobs;

namespace Animanager.Core.Plugins;

internal interface IAnimationPlugin
{
    string Id { get; }
    string Name { get; }
    string ConnectionStatus { get; }
    AnimationPluginSetup? Setup { get; }
    Task<AnimationPluginResult> SendAsync(AnimationPluginRequest request, IProgress<string> progress, CancellationToken ct);
}

internal sealed record AnimationPluginSetup(string Instructions, string DirectoryPrompt, Action<string> Install);

internal sealed record AnimationPluginRequest(string Clip, Func<string, JobKind, CancellationToken, Task<string>> ExportAsync);

internal sealed record AnimationPluginResult(string Message, string OutputPath);

internal sealed class AnimationPlugins
{
    private readonly Dictionary<string, IAnimationPlugin> _plugins = new(StringComparer.Ordinal);

    public IEnumerable<IAnimationPlugin> All => _plugins.Values;

    public void Register(IAnimationPlugin plugin) => _plugins.Add(plugin.Id, plugin);

    public IAnimationPlugin? Find(string id) => _plugins.GetValueOrDefault(id);
}
