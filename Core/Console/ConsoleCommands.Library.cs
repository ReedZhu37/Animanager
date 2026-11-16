using System.Text.Json;
using Animanager.Core.Models;
using Animanager.Core.Storage;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private LibraryManager _libraries = null!;
    private Action _beforeLibrarySwitch = null!;
    private Action<LibrarySession> _activateLibrary = null!;
    private Task _maintenanceTask = Task.CompletedTask;
    private CachePlan? _approvedCacheScope;

    private void RegisterLibraryCommands(LibraryManager manager, Action beforeSwitch, Action<LibrarySession> activate)
    {
        _libraries = manager;
        _beforeLibrarySwitch = beforeSwitch;
        _activateLibrary = activate;
        Bind("library current", nameof(LibraryCurrent), "Show active library ID, name and path");
        Bind("library list", nameof(LibraryList), "List registered and recently opened libraries");
        Bind("library create", nameof(LibraryCreate), "Create and open an empty library: <path> <name>");
        Bind("library register", nameof(LibraryRegister), "Register an existing library directory");
        Bind("library open", nameof(LibraryOpen), "Open a registered ID or existing library path");
        Bind("library relocate", nameof(LibraryRelocate), "Replace a missing registered path: <id> <path>");
        Bind("library check", nameof(LibraryCheck), "Check library files, hashes and index");
        Bind("library backup", nameof(LibraryBackup), "Back up the active library to a new directory");
        Bind("library restore", nameof(LibraryRestore), "Verify and open a backup in a new directory: <backup> <destination>");
        Bind("library rebuild", nameof(LibraryRebuild), "Rebuild the index of a library directory from persistent metadata");
        Bind("library cache_preview", nameof(LibraryCachePreview), "List eligible conversion files and library sizes");
        Bind("library cache_clear", nameof(LibraryCacheClear), "Clear the last previewed conversion files: <confirm_bool>");
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

    private void OpenSession(Func<LibrarySession> open)
    {
        _beforeLibrarySwitch();
        _activateLibrary(open());
        LibraryCurrent();
    }

    public void LibraryCurrent() => Info(_libraries.Active is { } active
        ? JsonSerializer.Serialize(new { active.Identity.Id, active.Identity.Name, active.Root })
        : "No active library.");
    public void LibraryList() => Guard(() => Info(JsonSerializer.Serialize(_libraries.Entries())));
    public void LibraryCreate(string path, string name) => Guard(() => OpenSession(() => _libraries.CreateAndOpen(Expand(path), name)));
    public void LibraryRegister(string path) => Guard(() => Info(JsonSerializer.Serialize(_libraries.Register(Expand(path)))));
    public void LibraryOpen(string pathOrId) => Guard(() => OpenSession(() => Guid.TryParseExact(pathOrId, "N", out _)
        ? _libraries.OpenById(pathOrId) : _libraries.Open(Expand(pathOrId))));
    public void LibraryRelocate(string id, string path) => Guard(() => Info(JsonSerializer.Serialize(_libraries.Relocate(id, Expand(path)))));

    private LibraryMaintenance Maintenance() => new(_api);
    private void Maintain(Func<Task> action)
    {
        if (!_maintenanceTask.IsCompleted) { Info("ERROR: Wait for the current library operation."); return; }
        _maintenanceTask = PendingTask = MaintainAsync(action);
    }

    private async Task MaintainAsync(Func<Task> action)
    {
        try
        {
            _beforeLibrarySwitch();
            await action();
        }
        catch (Exception ex) { Fail(ex); }
    }

    public void LibraryCheck() => Maintain(async () =>
    {
        var issues = await Maintenance().CheckAsync();
        Info(issues.Length == 0 ? "Integrity check passed." : string.Join("\n", issues.Select(LibraryMaintenance.FormatIssue)));
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

    public void CollectionList() => Guard(() => Info(JsonSerializer.Serialize(_api.Database.Collections())));
    public void CollectionCreate(string name) => Guard(() =>
    {
        var collection = _api.CreateCollection(name);
        _refresh();
        Info(JsonSerializer.Serialize(collection));
    });
    public void CollectionRename(string id, string name) => AssetEdit(() => _api.RenameCollection(id, name));
    public void CollectionDelete(string id) => AssetEdit(() => _api.DeleteCollection(id));
    public void CollectionAdd(string id, string ids) => AssetEdit(() => _api.SetCollectionMembership(id, AssetIds(ids), true));
    public void CollectionRemove(string id, string ids) => AssetEdit(() => _api.SetCollectionMembership(id, AssetIds(ids), false));
    public void CollectionSearch(string id, string query) => Guard(() =>
    {
        if (!_api.Database.Collections().Any(c => c.Id == id)) throw new KeyNotFoundException("Collection not found: " + id);
        PrintAssets(_api.Database.SearchAll(query == "*" ? "" : query, id));
    });
    private void PrintAssets(IEnumerable<AssetItem> assets) =>
        Info(string.Join("\n", assets.Select(a => $"{a.Id} | {a.Name} | {a.Tags}")));
}
