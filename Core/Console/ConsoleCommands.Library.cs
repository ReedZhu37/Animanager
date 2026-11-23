using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Storage;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private LibraryManager _libraries = null!;
    private Action _beforeLibrarySwitch = null!;
    private Func<Func<Task<LibrarySession>>, Task> _switchLibrary = null!;
    private Action<LibrarySession> _activateLibrary = null!;
    private Task _maintenanceTask = Task.CompletedTask;
    private CachePlan? _approvedCacheScope;

    private void RegisterLibraryCommands(LibraryManager manager, Action beforeSwitch,
        Func<Func<Task<LibrarySession>>, Task> switchLibrary, Action<LibrarySession> activate)
    {
        _libraries = manager;
        _beforeLibrarySwitch = beforeSwitch;
        _switchLibrary = switchLibrary;
        _activateLibrary = activate;
        Bind("library current", nameof(LibraryCurrent), "Show active library ID, name and path");
        Bind("library list", nameof(LibraryList), "List registered and recently opened libraries");
        Bind("library create", nameof(LibraryCreate), "Create and open an empty library: <path> <name>");
        Bind("library register", nameof(LibraryRegister), "Register an existing library directory");
        Bind("library open", nameof(LibraryOpen), "Open a registered ID or existing library path");
        Bind("library relocate", nameof(LibraryRelocate), "Replace a missing registered path: <id> <path>");
        Bind("library check", nameof(LibraryCheck), "Check library files, hashes and index");
        Bind("library organize_motion", nameof(LibraryOrganizeMotion), "Tag the active library by root displacement and move InPlace variants to trash");
        Bind("library backup", nameof(LibraryBackup), "Back up the active library to a new directory");
        Bind("library restore", nameof(LibraryRestore), "Verify and open a backup in a new directory: <backup> <destination>");
        Bind("library rebuild", nameof(LibraryRebuild), "Rebuild the index of a library directory from persistent metadata");
        Bind("library cache_preview", nameof(LibraryCachePreview), "List eligible conversion / preview cache files and library sizes");
        Bind("library cache_clear", nameof(LibraryCacheClear), "Clear the last previewed conversion / preview cache files: <confirm_bool>");
        Bind("asset rename", nameof(AssetRename), "Change a display name: <id_or_name> <name>");
        Bind("asset tags_add", nameof(AssetTagsAdd), "Add tags: <comma_separated_ids> <tags>");
        Bind("asset tags_remove", nameof(AssetTagsRemove), "Remove tags: <comma_separated_ids> <tags>");
        Bind("asset favorites", nameof(AssetFavorites), "Set favorites: <comma_separated_ids> <bool>");
        Bind("asset trash", nameof(AssetTrash), "Move assets to the trash: <comma_separated_ids>");
        Bind("asset trash_list", nameof(AssetTrashList), "List trash assets: <query_or_*>");
        Bind("asset restore", nameof(AssetRestore), "Restore trashed assets: <comma_separated_ids>");
        Bind("asset purge", nameof(AssetPurge), "Permanently remove trashed assets: <comma_separated_ids> <confirm_bool>");
        Bind("collection list", nameof(CollectionList), "List collections and IDs");
        Bind("collection create", nameof(CollectionCreate), "Create a collection by name");
        Bind("collection rename", nameof(CollectionRename), "Change collection name: <collection_id> <name>");
        Bind("collection delete", nameof(CollectionDelete), "Delete collection references: <collection_id>");
        Bind("collection add", nameof(CollectionAdd), "Add assets: <collection_id> <comma_separated_ids>");
        Bind("collection remove", nameof(CollectionRemove), "Remove assets: <collection_id> <comma_separated_ids>");
        Bind("collection search", nameof(CollectionSearch), "Search assets: <collection_id> <query_or_*>");
    }

    internal void ChangeLibrary(WorkspaceApi? api)
    {
        CancelAssetBrowseCommands();
        if (_sessionApi != null) _sessionApi.Jobs.Changed -= OnJobChanged;
        _sessionApi = api;
        if (_sessionApi != null) _sessionApi.Jobs.Changed += OnJobChanged;
        ForgetSelection();
        _lastPlan = null;
        _approvedCacheScope = null;
        _printedProgress.Clear();
        while (_updates.TryDequeue(out _)) { }
    }

    internal void ForgetSelection() => SelectedId = null;
    internal void TrackSelection(string id) => SelectedId = id;

    private void OpenSession(Func<Task<LibrarySession>> open) => Maintain(async () =>
    {
        await _switchLibrary(open);
        LibraryCurrent();
    }, cancelPreview: false);

    public void LibraryCurrent() => Info(_libraries.Active is { } active
        ? JsonSerializer.Serialize(new { active.Identity.Id, active.Identity.Name, active.Root })
        : "No active library.");
    public void LibraryList() => Guard(() => Info(JsonSerializer.Serialize(_libraries.Entries())));
    public void LibraryCreate(string path, string name) => OpenSession(() => _libraries.OpenAsync(Expand(path), newName: name));
    public void LibraryRegister(string path) => Maintain(async () => Info(JsonSerializer.Serialize(await _libraries.RegisterAsync(Expand(path)))), cancelPreview: false);
    public void LibraryOpen(string pathOrId) => OpenSession(() => Guid.TryParseExact(pathOrId, "N", out _)
        ? _libraries.OpenByIdAsync(pathOrId) : _libraries.OpenAsync(Expand(pathOrId)));
    public void LibraryRelocate(string id, string path) => Guard(() => Info(JsonSerializer.Serialize(_libraries.Relocate(id, Expand(path)))));

    private LibraryMaintenance Maintenance() => new(_api);
    private void Maintain(Func<Task> action, bool cancelPreview = true)
    {
        if (!_maintenanceTask.IsCompleted) { Info("ERROR: Wait for the current library operation."); return; }
        _maintenanceTask = PendingTask = MaintainAsync(action, cancelPreview);
    }

    private async Task MaintainAsync(Func<Task> action, bool cancelPreview)
    {
        try
        {
            if (cancelPreview) _beforeLibrarySwitch();
            await action();
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void LibraryCheck() => Maintain(async () =>
    {
        var issues = await Maintenance().CheckAsync();
        Info(issues.Length == 0 ? "Integrity check passed." : string.Join("\n", issues.Select(LibraryMaintenance.FormatIssue)));
    });

    public void LibraryOrganizeMotion() => Maintain(async () =>
    {
        Info("正在分析动画根轨迹…");
        var result = await _api.OrganizeAnimationsAsync();
        _refresh();
        Info($"已分类 {result.Tagged.Values.Sum()} 个动画 · InPlace 移入回收站 {result.MovedToTrash} 个"
            + "\n" + string.Join(" · ", result.Tagged.Select(entry => $"{entry.Key} {entry.Value}"))
            + (result.Errors.Count > 0 ? "\n无法判定位移 " + result.Errors.Count + " 个\n" + string.Join("\n", result.Errors) : ""));
    });

    public void LibraryBackup(string destination) => Maintain(async () => Info("Backup complete: " + await Maintenance().BackupAsync(Expand(destination))));
    public void LibraryRestore(string backup, string destination) => Maintain(async () =>
    {
        _activateLibrary(await _libraries.RestoreAsync(Expand(backup), Expand(destination)));
        LibraryCurrent();
    });

    public void LibraryRebuild(string path) => Maintain(async () =>
    {
        var root = Expand(path);
        await Task.Run(() => _libraries.RebuildAt(root));
        _refresh();
        Info("Index rebuilt: " + root);
    });

    public void LibraryCachePreview() => Maintain(async () =>
    {
        _approvedCacheScope = await Maintenance().PreviewCacheAsync();
        Info(JsonSerializer.Serialize(_approvedCacheScope, new JsonSerializerOptions { WriteIndented = true }));
    });

    public void LibraryCacheClear(bool confirm) => Maintain(async () =>
    {
        var plan = _approvedCacheScope ?? throw new InvalidOperationException("Run library cache_preview first.");
        RequireConfirmation(confirm);
        var result = await Maintenance().ClearCacheAsync(plan);
        _approvedCacheScope = null;
        Info(JsonSerializer.Serialize(result));
    });

    private static string[] AssetIds(string ids) => ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToArray();
    private static void RequireConfirmation(bool confirm)
    {
        if (!confirm) throw new InvalidOperationException("Pass true to confirm the operation.");
    }

    private void AssetEdit(Action edit) => Guard(() => { edit(); _refresh(); Info("Saved."); });

    public void AssetRename(string asset, string name) => AssetEdit(() => _api.RenameAsset(asset, name));
    public void AssetTagsAdd(string ids, string tags) => AssetEdit(() => _api.ChangeTags(AssetIds(ids), tags, true));
    public void AssetTagsRemove(string ids, string tags) => AssetEdit(() => _api.ChangeTags(AssetIds(ids), tags, false));
    public void AssetFavorites(string ids, bool favorite) => AssetEdit(() => _api.SetFavorites(AssetIds(ids), favorite));
    public void AssetTrash(string ids) => AssetEdit(() => _api.SetTrash(AssetIds(ids), true));
    public void AssetRestore(string ids) => AssetEdit(() => _api.SetTrash(AssetIds(ids), false));
    public void AssetTrashList(string query) => Guard(() => PrintAssets(_api.Database.SearchAll(query == "*" ? "" : query, "Trash")));
    public void AssetPurge(string ids, bool confirm) => Maintain(async () =>
    {
        RequireConfirmation(confirm);
        var result = await Maintenance().PurgeAsync(AssetIds(ids));
        _refresh();
        Info(JsonSerializer.Serialize(result));
    });

    internal AppCollectionCatalog AppCollections { get; set; } = null!;

    public void CollectionList() => Guard(() => Info(JsonSerializer.Serialize(AppCollections.Read().Collections)));
    public void CollectionCreate(string name) => Guard(() =>
    {
        var collection = AppCollections.Create(name);
        _refresh();
        Info(JsonSerializer.Serialize(collection));
    });
    public void CollectionRename(string id, string name) => AssetEdit(() => AppCollections.Rename(id, name));
    public void CollectionDelete(string id) => AssetEdit(() => AppCollections.Delete(id));
    public void CollectionAdd(string id, string ids) => EditCollectionMembers(id, ids, add: true);
    public void CollectionRemove(string id, string ids) => EditCollectionMembers(id, ids, add: false);

    private void EditCollectionMembers(string id, string ids, bool add) => AssetEdit(() =>
    {
        var library = _libraries.Active ?? throw new InvalidOperationException("Open a library first.");
        var assetIds = AssetIds(ids);
        if (add && library.Api.Database.ExistingIds(assetIds, trash: false).Length != assetIds.Length)
            throw new InvalidOperationException("Some assets are no longer in the active library.");
        AppCollections.SetMembership(id, assetIds.Select(assetId => new CollectionAssetLink(library.Identity.Id, assetId)).ToArray(), add);
    });

    public void CollectionSearch(string id, string query) => Guard(() =>
    {
        var collection = AppCollections.Read().Collections.FirstOrDefault(item => item.Id == id)
            ?? throw new KeyNotFoundException("Collection not found: " + id);
        var members = collection.Members.ToLookup(member => member.LibraryId, member => member.AssetId);
        PrintAssets(_libraries.OpenLibraries().Where(library => members.Contains(library.Identity.Id))
            .SelectMany(library => library.Api.Database.SearchAll(query == "*" ? "" : query, "All",
                JsonSerializer.Serialize(members[library.Identity.Id]))));
    });
    private void PrintAssets(IEnumerable<AssetItem> assets) =>
        Info(string.Join("\n", assets.Select(a => $"{a.Id} | {a.Name} | {a.Tags}")));
}
