using System.Text.Json;
using Animanager.Core.Browse;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private const string AssetBrowseHelp = "asset filter <query_or_*> <category_or_*> <skeleton_or_*> <seconds_or_*> <fps_or_*> <page>\nasset attributes <id_or_name> | asset skeletons\nFilters accept unknown, exact numbers, and ranges such as 1..3. Duration is the longest clip.";
    private CancellationTokenSource? _assetBrowseCommand;

    private void RegisterAssetBrowseCommands()
    {
        Bind("asset filter", nameof(AssetFilter), "Filter a page by query, category/collection, skeleton ID, seconds, and source FPS. Use * for any and unknown for missing metadata.");
        Bind("asset attributes", nameof(AssetAttributes), "Show parsed skeleton, longest clip duration, and source FPS: <id_or_name>");
        Bind("asset skeletons", nameof(AssetSkeletons), "List parsed skeleton names and IDs in the library.");
    }

    public void AssetFilter(string query, string category, string skeleton, string duration, string sourceFps, int page) => Guard(() =>
    {
        var filter = AssetAttributeFilter.Parse(skeleton, duration, sourceFps);
        StartAssetBrowseCommand(async (api, token) =>
        {
            var result = await api.BrowseAssetsAsync(query == "*" ? "" : query, category == "*" ? "All" : category, filter,
                page, cancellationToken: token);
            return JsonSerializer.Serialize(new
            {
                result.Total, result.Page,
                Items = result.Items.Select(row => new { row.Asset.Id, row.Asset.Name, row.Asset.SourceFormat, row.Attributes })
            });
        });
    });

    public void AssetAttributes(string asset) => Guard(() => StartAssetBrowseCommand(async (api, token) =>
    {
        var item = api.Database.Find(asset) ?? api.Database.Find(asset, trash: true)
            ?? throw new KeyNotFoundException("Asset not found (use its full ID or exact unique name): " + asset);
        return JsonSerializer.Serialize(new { item.Id, item.Name, Attributes = await api.GetAssetAttributesAsync(item, token) });
    }));

    public void AssetSkeletons() => Guard(() => StartAssetBrowseCommand(async (api, token) =>
        JsonSerializer.Serialize(await api.GetAssetSkeletonsAsync(token))));

    private void StartAssetBrowseCommand(Func<WorkspaceApi, CancellationToken, Task<string>> action)
    {
        var api = _api;
        CancelAssetBrowseCommands();
        var request = new CancellationTokenSource();
        _assetBrowseCommand = request;
        PendingTask = FinishAssetBrowseCommandAsync(api, request, action);
    }

    private async Task FinishAssetBrowseCommandAsync(WorkspaceApi api, CancellationTokenSource request,
        Func<WorkspaceApi, CancellationToken, Task<string>> action)
    {
        bool Current() => !_closing && _sessionApi == api && ReferenceEquals(_assetBrowseCommand, request);
        try
        {
            var result = await action(api, request.Token);
            if (Current()) Info(result);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (Current()) Fail(ex); }
        finally
        {
            if (ReferenceEquals(_assetBrowseCommand, request)) _assetBrowseCommand = null;
            request.Dispose();
        }
    }

    private void CancelAssetBrowseCommands()
    {
        var previous = _assetBrowseCommand;
        _assetBrowseCommand = null;
        previous?.Cancel();
    }
}
