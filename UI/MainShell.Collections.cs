using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Models;
using Animanager.Core.Storage;
using Animanager.UI.Shell;
using Godot;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Animanager.UI;

public partial class MainShell
{
    internal AppCollectionCatalog AppCollections { get; private set; } = null!;
    private AppCollection[] _collections = [];
    private AppCollection? CurrentCollection => _collections.FirstOrDefault(collection => collection.Id == _category);
    private string _collectionRevision = "";
    private (string Id, string Name, string? ParentId)[] _collectionEntries = [];
    private CollectionBrowseReturn? _collectionBrowseReturn;

    private sealed record CollectionBrowseReturn(string Category, int Page, string? Library, string Query,
        AssetAttributeFilter Filter, AssetTagFilter? Tags, AssetSort Sort);

    private void ToggleCollection(string id)
    {
        if (_category == id)
        {
            CloseCollection();
            return;
        }
        if (CategoryIncludesMountedLibraries)
        {
            int page = _publishedBrowseScope.Query == _search.Text ? _page : 0;
            _collectionBrowseReturn = new(_category, page, _assetLibraryFilter, _search.Text,
                _attributeFilter, CurrentAssetTagFilter(), _sort);
        }
        SetAssetCategory(id, 0);
        Refresh();
        _workspace.ShowPanel("assets");
    }

    private void CloseCollection()
    {
        RestoreCollectionBrowse();
        Refresh();
        _workspace.ShowPanel("assets");
    }

    private void RestoreCollectionBrowse()
    {
        var previous = _collectionBrowseReturn;
        _collectionBrowseReturn = null;
        int page = previous != null && previous.Library == _assetLibraryFilter &&
            previous.Query == _search.Text && previous.Filter == _attributeFilter && previous.Sort == _sort &&
            AssetTagFilter.Equivalent(previous.Tags, CurrentAssetTagFilter()) ? previous.Page : 0;
        SetAssetCategory(previous?.Category ?? "All", page);
    }

    private void RefreshCollections()
    {
        try
        {
            var state = AppCollections.Read();
            var sources = LoadedLibraries().Where(library => !state.MigratedLibraries.Contains(library.Identity.Id))
                .Select(library => (library.Identity.Id, library.Api.Database.Collections()));
            state = AppCollections.Migrate(state, sources);
            var next = state.OrderedCollections();
            var entries = next.Select(collection => (collection.Id, collection.Name, collection.ParentId)).ToArray();
            bool hierarchyChanged = !_collectionEntries.SequenceEqual(entries);
            if (hierarchyChanged) _sideBar.SetCollections(entries);
            _collectionEntries = entries;
            if (next.All(collection => collection.Id != _category) && _collections.Any(collection => collection.Id == _category))
                RestoreCollectionBrowse();
            _collections = next;
            _collectionRevision = state.Revision;
            if (hierarchyChanged && _exportCollectionChoice != null) RefreshExportCollectionChoices();
        }
        catch (Exception error) when (IsAppSettingsError(error))
        {
            GD.PushWarning("无法读取应用集合：" + error.Message);
        }
        RefreshTagCatalog();
    }

    private void RunCollectionAction(string id, CollectionMenuAction action)
    {
        var collection = _collections.Single(c => c.Id == id);
        switch (action)
        {
            case CollectionMenuAction.CreateChild:
                ShowCollectionEditor(parent: collection);
                break;
            case CollectionMenuAction.Rename:
                ShowCollectionEditor(collection);
                break;
            case CollectionMenuAction.Copy:
                ApplyCollectionChange(() => AppCollections.Copy(id), $"已复制集合「{collection.Name}」");
                break;
            case CollectionMenuAction.Close:
                if (_category == id) CloseCollection();
                break;
            case CollectionMenuAction.Delete:
                ConfirmCollectionDeletion(collection);
                break;
            case CollectionMenuAction.Export:
                OpenCollectionExport(collection);
                break;
        }
    }

    private void ShowCollectionEditor(AppCollection? collection = null, AppCollection? parent = null)
    {
        string title = collection != null ? "编辑集合" : parent != null ? "新建子集合" : "新建集合";
        string label = parent == null ? "集合名称" : "在「" + parent.Name + "」中新建集合";
        ShowEntry(title, label, collection?.Name ?? "", null, "", false, (name, _) =>
        {
            ApplyCollectionChange(() =>
            {
                if (collection == null) AppCollections.Create(name, parent?.Id);
                else AppCollections.Rename(collection.Id, name);
            }, collection == null ? "集合已创建" : "集合已更新");
            if (parent != null) _sideBar.ExpandCollection(parent.Id);
        });
    }

    private void ConfirmCollectionDeletion(AppCollection collection)
    {
        string details = $"删除「{collection.Name}」及它的资产归属记录。";
        if (_collections.Any(child => child.ParentId == collection.Id)) details += "\n它的子集合将移到上一级。";
        ShowApproval("删除集合", details,
            () => ApplyCollectionChange(() => AppCollections.Delete(collection.Id), "集合已删除"));
    }

    private bool CanDropAssetsOnCollection(string collectionId, string[] keys) =>
        _collections.Any(collection => collection.Id == collectionId) && keys.Any(key => DragSourceLibrary(key) != null);

    private bool CanMoveCollection(string id, string? targetId, CollectionPlacement placement) =>
        AppCollectionCatalog.CanMove(_collections, id, targetId, placement);

    private void MoveCollection(string id, string? targetId, CollectionPlacement placement)
    {
        string status = placement switch
        {
            CollectionPlacement.Inside when targetId != null =>
                "集合已移入「" + _collections.Single(collection => collection.Id == targetId).Name + "」",
            CollectionPlacement.Inside => "集合已移到顶层",
            _ => "集合顺序已更新"
        };
        ApplyCollectionChange(() => AppCollections.Move(id, targetId, placement), status);
        if (placement == CollectionPlacement.Inside && targetId != null) _sideBar.ExpandCollection(targetId);
    }

    private void DropAssetsOnCollection(string collectionId, string[] keys)
    {
        var groups = DraggedAssetGroups(keys);
        if (groups.Length == 0 || _collections.FirstOrDefault(collection => collection.Id == collectionId) is not { } collection)
            return;
        AddAssetsToCollection(collection, groups);
    }

    private void AddAssetsToCollection(AppCollection collection, (LibrarySession Library, string[] Ids)[] groups)
    {
        ApplyCollectionChange(() =>
        {
            foreach (var (source, ids) in groups)
            {
                if (_libraries.FindOpenLibrary(source.Identity.Id) != source)
                    throw new InvalidOperationException("资产的来源库已关闭，请重新选择资产。");
                if (source.Api.Database.ExistingIds(ids, trash: false).Length != ids.Length)
                    throw new InvalidOperationException("部分资产已不在来源库，请重新选择资产。");
            }
            var members = groups.SelectMany(group => group.Ids.Select(id => new CollectionAssetLink(group.Library.Identity.Id, id)));
            AppCollections.SetMembership(collection.Id, members.ToArray(), add: true);
        }, $"已加入「{collection.Name}」");
    }

    private IEnumerable<(string Key, CollectionAssetLink Link)> SelectedCollectionLinks(AppCollection collection)
    {
        var selected = new Dictionary<CollectionAssetLink, string>();
        foreach (var key in _assetSelection)
            if (SelectionLibrary(key) is { } library)
                selected[new(library.Identity.Id, SelectionAssetId(key))] = key;
        foreach (var link in collection.Members)
            if (selected.TryGetValue(link, out var key))
                yield return (key, link);
    }

    private void ConfirmCollectionMemberRemoval(AppCollection collection)
    {
        var members = SelectedCollectionLinks(collection).Distinct().ToArray();
        if (members.Length == 0) throw new InvalidOperationException("请先选中要从此集合移出的资产。");
        ConfirmCollectionMemberRemoval(collection, members);
    }

    private void ConfirmCollectionMemberRemoval(AppCollection collection, (string Key, CollectionAssetLink Link)[] members)
    {
        int hidden = members.Count(member => _browser.IndexOf(member.Key) < 0);
        string scope = $"将 {members.Length} 个资产移出此集合" + (hidden > 0 ? $"，其中 {hidden} 个在其他页或筛选外" : "");
        ShowApproval($"从「{collection.Name}」移出", scope + "。资产仍保留在来源库。", () =>
            ApplyCollectionChange(() =>
            {
                AppCollections.SetMembership(collection.Id, members.Select(member => member.Link).ToArray(), add: false);
                if (_category == collection.Id)
                    _assetSelection.Prune(_assetSelection.Except(members.Select(member => member.Key)).ToArray());
            }, $"已从「{collection.Name}」移出 {members.Length} 个资产"));
    }

    private static LibraryBrowseScope[] CollectionBrowseScopes(AppCollection collection, LibrarySession[] libraries)
    {
        var members = collection.Members.ToLookup(member => member.LibraryId, member => member.AssetId);
        return libraries.Where(library => members.Contains(library.Identity.Id))
            .Select(library => new LibraryBrowseScope(library, "All", JsonSerializer.Serialize(members[library.Identity.Id]))).ToArray();
    }

    private sealed record ClosedCollectionAsset(string LibraryId, string LibraryName, AssetBrowseRow Row, bool LocateLibrary);

    private ClosedCollectionAsset[] ReadClosedCollectionAssets(AppCollection collection, LibraryBrowseScope[] openScopes,
        string query, AssetAttributeFilter filter, AssetSort sort, AssetTagFilter? tags, CancellationToken ct)
    {
        var openIds = openScopes.Select(scope => scope.Library.Identity.Id).ToHashSet();
        var closed = collection.Members.Where(member => !openIds.Contains(member.LibraryId))
            .ToLookup(member => member.LibraryId, member => member.AssetId);
        if (closed.Count == 0) return [];
        LibraryEntry[] registered;
        try { registered = _libraries.Entries(); }
        catch (Exception error) when (IsAppSettingsError(error)) { registered = []; }
        var entries = registered.ToDictionary(entry => entry.Id);
        var result = new List<ClosedCollectionAsset>();
        foreach (var group in closed)
        {
            ct.ThrowIfCancellationRequested();
            var entry = entries.GetValueOrDefault(group.Key);
            LocalStorageProvider? storage = null;
            try
            {
                if (entry != null && LibraryManager.ReadIdentity(entry.Path).Id == entry.Id)
                    storage = new LocalStorageProvider(entry.Path);
            }
            catch (Exception error) when (IsAppSettingsError(error)) { }

            var ids = group.ToHashSet();
            var assets = ReadClosedMemberMetadata(storage, ids, query);
            // An unavailable directory can still contribute its saved collection references.
            assets ??= ids.Where(id => id.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(id => new AssetItem { Id = id, Name = "动画或模型 · " + id, ImportedAt = "", Type = "" });
            var cache = storage != null && (filter.IsActive || sort.NeedsAttributes) ? new AssetBrowseCache(storage, _ => { }) : null;
            foreach (var asset in assets)
            {
                ct.ThrowIfCancellationRequested();
                if (tags?.Matches(asset.Tags) == false) continue;
                var attributes = cache?.ReadCachedAttributes(asset);
                if (!filter.Matches(attributes ?? AssetAttributes.Unknown)) continue;
                result.Add(new(group.Key, entry?.Name ?? "未登记的来源库", new(asset, attributes), storage == null));
            }
        }
        var offlineSort = sort.Field == AssetSortField.LibraryMix ? sort with { Field = AssetSortField.Imported } : sort;
        return WorkspaceApi.OrderBrowseRows(result, offlineSort, item => item.Row)
            .ThenBy(item => item.LibraryId, StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<AssetItem>? ReadClosedMemberMetadata(LocalStorageProvider? storage, HashSet<string> ids, string query)
    {
        if (storage == null) return null;
        try { return AssetDatabase.SearchClosedCollectionMembers(storage, query, JsonSerializer.Serialize(ids)); }
        catch (Exception error) when (IsAppSettingsError(error) || error is SqliteException)
        {
            try
            {
                // The durable catalog is still usable if the search index needs rebuilding.
                return AssetDatabase.ReadCatalog(storage.Resolve(AssetDatabase.CatalogName)).Assets.Where(asset =>
                    !asset.Trashed && ids.Contains(asset.Id) && (string.IsNullOrWhiteSpace(query) ||
                    asset.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || asset.Tags.Contains(query, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception catalogError) when (IsAppSettingsError(catalogError)) { return null; }
        }
    }

    private void OpenCollectionSourceLibrary(string libraryId, bool locate)
    {
        if (_libraryLoading || _libraries.FindOpenLibrary(libraryId) != null) return;
        var entry = _libraries.Entries().FirstOrDefault(item => item.Id == libraryId);
        if (!locate && entry != null && LibraryManager.HasLibrary(entry.Path))
            Open(entry.Path);
        else
            ShowEntry("定位来源库", "选择对应的 Animanager 资产库目录", "", null, "", true, (path, _) => Open(path));

        void Open(string path) => LibraryLoadAction(() => RunLibraryLoadAsync("正在打开来源库…", async () =>
        {
            var root = NormalizeFolder(path);
            if (entry != null && !LibraryManager.SamePath(entry.Path, root))
                await Task.Run(() => _libraries.Relocate(libraryId, root));
            var library = await _libraries.OpenAsync(root, browse: true, expectedId: libraryId);
            if (!_closing) await ShowOpenedLibrary(library);
        }));
    }

    private void ApplyCollectionChange(Action edit, string status)
    {
        edit();
        Refresh();
        SetStatus(status);
    }

    private static CollectionAssetLink[] ExportCollectionMembers(AppCollection collection, AppCollection[] collections)
    {
        var children = collections.ToLookup(item => item.ParentId);
        var pending = new Queue<AppCollection>();
        var members = new HashSet<CollectionAssetLink>();
        pending.Enqueue(collection);
        while (pending.TryDequeue(out var current))
        {
            members.UnionWith(current.Members);
            foreach (var child in children[current.Id]) pending.Enqueue(child);
        }
        return members.ToArray();
    }
}
