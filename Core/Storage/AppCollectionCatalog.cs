using Animanager.Core.Motion;
using System.Text.Json;

namespace Animanager.Core.Storage;

internal sealed record AppCollection(string Id, string Name, string? ParentId, CollectionAssetLink[] Members);

internal enum CollectionPlacement { Inside, Before, After }

internal sealed record AppCollectionSettings(int Version, string Revision, AppCollection[] Collections,
    string[] MigratedLibraries, bool ManualOrder = false)
{
    internal static readonly AppCollectionSettings Empty = new(1, "", [], []);

    internal AppCollection[] OrderedCollections() => ManualOrder ? Collections :
        Collections.OrderBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase).ToArray();
}

internal sealed class AppCollectionCatalog(string path)
{
    internal AppCollectionSettings Read()
    {
        if (!File.Exists(path)) return AppCollectionSettings.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var state = JsonSerializer.Deserialize<AppCollectionSettings>(stream, MotionJson.Options);
        if (state is not { Version: 1, Collections: not null, MigratedLibraries: not null } ||
            !IsId(state.Revision) || state.MigratedLibraries.Any(id => !IsId(id)) ||
            state.Collections.Any(collection => collection == null || !IsId(collection.Id) ||
                string.IsNullOrWhiteSpace(collection.Name) || collection.Members == null || collection.Members.Any(member => !ValidMember(member))) ||
            state.Collections.Select(collection => collection.Id).Distinct().Count() != state.Collections.Length ||
            !ValidHierarchy(state.Collections))
            throw new InvalidDataException("应用集合设置损坏：" + path);
        return state;
    }

    internal void ImportRegisteredLibraries(IEnumerable<LibraryEntry> libraries)
    {
        var state = Read();
        var migrated = state.MigratedLibraries.ToHashSet();
        var sources = new List<(string Id, AssetCollection[] Collections)>();
        foreach (var library in libraries.Where(library => !migrated.Contains(library.Id)))
        {
            try
            {
                var catalogPath = Path.Combine(library.Path, AssetDatabase.CatalogName);
                if (!File.Exists(catalogPath) || LibraryManager.ReadIdentity(library.Path).Id != library.Id) continue;
                LocalStorageProvider.RequireLocalPath(catalogPath);
                sources.Add((library.Id, AssetDatabase.ReadCatalog(catalogPath).Collections));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                Godot.GD.PushWarning($"暂时无法迁移「{library.Name}」的集合：{error.Message}");
            }
        }
        Migrate(state, sources);
    }

    internal AppCollectionSettings Migrate(AppCollectionSettings state, IEnumerable<(string Id, AssetCollection[] Collections)> sources)
    {
        var pending = sources.Where(source => !state.MigratedLibraries.Contains(source.Id)).ToArray();
        if (pending.Length == 0) return state;
        return Update(current =>
        {
            var migrated = current.MigratedLibraries.ToHashSet();
            var collections = current.Collections.ToList();
            var ids = collections.Select(collection => collection.Id).ToHashSet();
            foreach (var (libraryId, legacy) in pending)
            {
                if (!migrated.Add(libraryId)) continue;
                var map = legacy.ToDictionary(collection => collection.Id, collection => ReserveId(collection.Id));
                foreach (var collection in legacy)
                {
                    var members = collection.AssetIds.Select(id => new CollectionAssetLink(libraryId, id))
                        .Concat(collection.LinkedAssets).Distinct().ToArray();
                    collections.Add(new(map[collection.Id], collection.Name,
                        collection.ParentId == null ? null : map[collection.ParentId], members));
                }
            }
            return current with { Collections = collections.ToArray(), MigratedLibraries = migrated.ToArray() };

            string ReserveId(string id)
            {
                while (!ids.Add(id)) id = NewId();
                return id;
            }
        });
    }

    internal AppCollection Create(string name, string? parentId = null)
    {
        var collection = new AppCollection(NewId(), name.Trim(), parentId, []);
        Update(state =>
        {
            if (parentId != null) Find(state, parentId);
            ValidateName(state, collection);
            return state with { Collections = [.. state.Collections, collection] };
        });
        return collection;
    }

    internal void Rename(string id, string name) => Update(state =>
    {
        var renamed = Find(state, id) with { Name = name.Trim() };
        ValidateName(state, renamed);
        return state with { Collections = state.Collections.Select(item => item.Id == id ? renamed : item).ToArray() };
    });

    internal void Copy(string id) => Update(state =>
    {
        var source = Find(state, id);
        var siblingNames = state.Collections.Where(item => item.ParentId == source.ParentId)
            .Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = $"{source.Name}（副本）";
        for (int number = 2; siblingNames.Contains(name); number++) name = $"{source.Name}（副本 {number}）";

        var copy = source with { Id = NewId(), Name = name };
        var copies = new List<AppCollection> { copy };
        var children = state.Collections.ToLookup(item => item.ParentId);
        var pending = new Queue<(string SourceId, string CopyId)>();
        pending.Enqueue((source.Id, copy.Id));
        while (pending.TryDequeue(out var pair))
        {
            foreach (var child in children[pair.SourceId])
            {
                var childCopy = child with { Id = NewId(), ParentId = pair.CopyId };
                copies.Add(childCopy);
                pending.Enqueue((child.Id, childCopy.Id));
            }
        }

        var collections = state.Collections.ToList();
        collections.InsertRange(collections.IndexOf(source) + 1, copies);
        return state with { Collections = collections.ToArray() };
    });

    internal static bool CanMove(AppCollection[] collections, string id, string? targetId, CollectionPlacement placement) =>
        MoveError(collections, id, targetId, placement, out _) == null;

    internal void Move(string id, string? targetId, CollectionPlacement placement) => Update(state =>
    {
        bool manualOrder = state.ManualOrder || placement != CollectionPlacement.Inside;
        var collections = manualOrder ? state.OrderedCollections() : state.Collections;
        if (MoveError(collections, id, targetId, placement, out var parentId) is { } error)
            throw new InvalidOperationException(error);
        var moved = Find(state, id) with { ParentId = parentId };
        if (!manualOrder)
            return state with { Collections = collections.Select(item => item.Id == id ? moved : item).ToArray() };

        var ordered = collections.Where(item => item.Id != id).ToList();
        int index = placement == CollectionPlacement.Inside ? ordered.Count : ordered.FindIndex(item => item.Id == targetId);
        if (placement == CollectionPlacement.After) index++;
        ordered.Insert(index, moved);
        return state with { Collections = ordered.ToArray(), ManualOrder = true };
    });

    internal void Delete(string id) => Update(state =>
    {
        var collection = Find(state, id);
        return state with
        {
            Collections = state.Collections.Where(item => item.Id != id)
                .Select(item => item.ParentId == id ? item with { ParentId = collection.ParentId } : item).ToArray()
        };
    });

    internal void SetMembership(string id, CollectionAssetLink[] members, bool add)
    {
        if (members.Any(member => !ValidMember(member))) throw new ArgumentException("集合包含无效的资产引用。");
        Replace(id, collection => collection with
        {
            Members = (add ? collection.Members.Union(members) : collection.Members.Except(members)).ToArray()
        });
    }

    private void Replace(string id, Func<AppCollection, AppCollection> edit) => Update(state =>
    {
        var revised = edit(Find(state, id));
        return state with { Collections = state.Collections.Select(item => item.Id == id ? revised : item).ToArray() };
    });

    private AppCollectionSettings Update(Func<AppCollectionSettings, AppCollectionSettings> edit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var state = edit(Read()) with { Revision = NewId() };
        MotionJson.Atomic(path, state);
        return state;
    }

    private static AppCollection Find(AppCollectionSettings state, string id) =>
        state.Collections.FirstOrDefault(collection => collection.Id == id) ?? throw new KeyNotFoundException("找不到该集合。");

    private static void ValidateName(AppCollectionSettings state, AppCollection collection)
    {
        if (collection.Name.Length == 0) throw new ArgumentException("请输入集合名称。");
        if (HasSiblingName(state.Collections, collection.Id, collection.Name, collection.ParentId))
            throw new ArgumentException("此处已有同名集合：" + collection.Name);
    }

    private static string? MoveError(AppCollection[] collections, string id, string? targetId, CollectionPlacement placement,
        out string? parentId)
    {
        parentId = targetId;
        int source = Array.FindIndex(collections, item => item.Id == id);
        if (source < 0) return "找不到该集合。";
        var collection = collections[source];
        if (placement == CollectionPlacement.Inside)
        {
            if (parentId == collection.ParentId) return "集合已在此层级。";
        }
        else
        {
            int target = Array.FindIndex(collections, item => item.Id == targetId);
            if (target < 0) return "找不到目标集合。";
            if (source == target) return "不能将集合放在自身旁边。";
            parentId = collections[target].ParentId;
            if (parentId == collection.ParentId)
            {
                bool alreadyOnRequestedSide = placement == CollectionPlacement.Before ? source < target : source > target;
                int start = Math.Min(source, target) + 1;
                int count = Math.Abs(source - target) - 1;
                return alreadyOnRequestedSide && Array.FindIndex(collections, start, count,
                    item => item.ParentId == collection.ParentId) < 0 ? "集合已在此位置。" : null;
            }
        }

        var byId = collections.ToDictionary(item => item.Id);
        for (var ancestorId = parentId; ancestorId != null;)
        {
            if (ancestorId == id) return "不能将集合移入自身或其子集合。";
            if (!byId.TryGetValue(ancestorId, out var ancestor)) return "找不到目标集合。";
            ancestorId = ancestor.ParentId;
        }
        return HasSiblingName(collections, id, collection.Name, parentId) ? "此处已有同名集合：" + collection.Name : null;
    }

    private static bool HasSiblingName(AppCollection[] collections, string id, string name, string? parentId) =>
        collections.Any(item => item.Id != id && item.ParentId == parentId &&
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool ValidHierarchy(AppCollection[] collections)
    {
        var children = collections.ToLookup(collection => collection.ParentId);
        var pending = new Queue<AppCollection>(children[null]);
        int visited = 0;
        while (pending.TryDequeue(out var collection))
        {
            visited++;
            foreach (var child in children[collection.Id]) pending.Enqueue(child);
        }
        return visited == collections.Length;
    }

    private static bool IsId(string? id) => Guid.TryParseExact(id, "N", out _);
    private static bool ValidMember(CollectionAssetLink? member) => member != null && IsId(member.LibraryId) &&
        !string.IsNullOrWhiteSpace(member.AssetId);
    private static string NewId() => Guid.NewGuid().ToString("N");
}
