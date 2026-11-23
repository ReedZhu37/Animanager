using Animanager.Core.Motion;
using System.Text.Json;

namespace Animanager.Core.Storage;

internal sealed record AppTagSettings(int Version, string Revision, string[] Tags, AssetTagGroup[] Groups, string[] MigratedLibraries)
{
    internal static readonly AppTagSettings Empty = new(1, "", [], [], []);
}

internal sealed class AppTagCatalog(string path)
{
    internal const string AllGroupId = "all";
    internal const string AllGroupName = "全部";
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    internal AppTagSettings Read()
    {
        if (!File.Exists(path)) return AppTagSettings.Empty;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var state = JsonSerializer.Deserialize<AppTagSettings>(stream, MotionJson.Options);
        if (state is not { Version: 1, Tags: not null, Groups: not null, MigratedLibraries: not null } ||
            !Guid.TryParseExact(state.Revision, "N", out _) || state.Tags.Any(tag => !AssetDatabase.IsTagToken(tag)) ||
            state.MigratedLibraries.Any(id => !Guid.TryParseExact(id, "N", out _)))
            throw new InvalidDataException("应用标签设置损坏：" + path);

        var known = state.Tags.ToHashSet(Comparer);
        var ids = new HashSet<string>(Comparer);
        var names = new HashSet<string>(Comparer);
        if (known.Count != state.Tags.Length || state.Groups.Any(group => group == null ||
            !Guid.TryParseExact(group.Id, "N", out _) || !ids.Add(group.Id) || string.IsNullOrWhiteSpace(group.Name) ||
            !names.Add(group.Name) || group.Tags == null || group.Tags.Any(tag => !known.Contains(tag)) ||
            group.Tags.Distinct(Comparer).Count() != group.Tags.Length))
            throw new InvalidDataException("应用标签分组损坏：" + path);
        return state;
    }

    internal AppTagSettings ImportRegisteredLibraries(IEnumerable<LibraryEntry> libraries)
    {
        var current = Read();
        var migrated = current.MigratedLibraries.ToHashSet(Comparer);
        var sources = new List<(string Id, AssetCatalog Catalog)>();
        foreach (var library in libraries.Where(library => !migrated.Contains(library.Id)))
        {
            try
            {
                var catalogPath = Path.Combine(library.Path, AssetDatabase.CatalogName);
                if (!File.Exists(catalogPath) || LibraryManager.ReadIdentity(library.Path).Id != library.Id) continue;
                LocalStorageProvider.RequireLocalPath(catalogPath);
                sources.Add((library.Id, AssetDatabase.ReadCatalog(catalogPath)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                Godot.GD.PushWarning($"暂时无法导入「{library.Name}」的标签：{error.Message}");
            }
        }
        return Observe(current, sources);
    }

    internal AppTagSettings Observe(AppTagSettings current, IEnumerable<(string Id, AssetCatalog Catalog)> sources)
    {
        var observed = sources.Select(source => (source.Id, source.Catalog.TagGroups,
            Tags: source.Catalog.Assets.SelectMany(asset => WorkspaceApi.ParseTags(asset.Tags)).Distinct(Comparer).ToArray())).ToArray();
        var known = current.Tags.ToHashSet(Comparer);
        var imported = current.MigratedLibraries.ToHashSet(Comparer);
        if (observed.All(source => imported.Contains(source.Id) && source.Tags.All(known.Contains))) return current;

        return Update(state =>
        {
            var tags = state.Tags.ToHashSet(Comparer);
            var groups = state.Groups.ToDictionary(group => group.Name, Comparer);
            var migrated = state.MigratedLibraries.ToHashSet(Comparer);
            foreach (var source in observed)
            {
                tags.UnionWith(source.Tags);
                if (!migrated.Add(source.Id)) continue;
                foreach (var legacy in source.TagGroups)
                {
                    tags.UnionWith(legacy.Tags);
                    var group = groups.GetValueOrDefault(legacy.Name) ?? new AssetTagGroup(NewId(), legacy.Name, []);
                    groups[group.Name] = group with { Tags = group.Tags.Union(legacy.Tags, Comparer).ToArray() };
                }
            }
            return state with { Tags = tags.Order(Comparer).ToArray(), Groups = groups.Values.ToArray(), MigratedLibraries = migrated.ToArray() };
        });
    }

    internal void AddTags(IEnumerable<string> tags)
    {
        var names = ValidateTags(tags);
        if (names.Length == 0) throw new ArgumentException("请输入至少一个标签。");
        Update(state => state with { Tags = state.Tags.Union(names, Comparer).Order(Comparer).ToArray() });
    }

    internal void SaveGroup(string? id, string name, IEnumerable<string> tags)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("请输入标签组名称。");
        if (id == AllGroupId || name == AllGroupName) throw new ArgumentException("「全部」是内置标签组。");
        var members = ValidateTags(tags);
        Update(state =>
        {
            if (id != null && !state.Groups.Any(group => group.Id == id)) throw new KeyNotFoundException("找不到该标签组。");
            if (state.Groups.Any(group => group.Id != id && Comparer.Equals(group.Name, name)))
                throw new ArgumentException("已有同名标签组：" + name);

            var updated = new AssetTagGroup(id ?? NewId(), name, members);
            var groups = state.Groups.Select(group => group.Id == id ? updated : group).ToArray();
            return state with
            {
                Tags = state.Tags.Union(members, Comparer).Order(Comparer).ToArray(),
                Groups = id == null ? [.. groups, updated] : groups
            };
        });
    }

    internal void DeleteGroup(string id)
    {
        if (id == AllGroupId) throw new ArgumentException("「全部」是内置标签组。");
        Update(state => state with { Groups = state.Groups.Where(group => group.Id != id).ToArray() });
    }

    private AppTagSettings Update(Func<AppTagSettings, AppTagSettings> edit)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var next = edit(Read()) with { Revision = NewId() };
        MotionJson.Atomic(path, next);
        return next;
    }

    private static string[] ValidateTags(IEnumerable<string> tags)
    {
        var names = tags.Distinct(Comparer).Order(Comparer).ToArray();
        if (names.Any(tag => !AssetDatabase.IsTagToken(tag))) throw new ArgumentException("包含无效的标签。");
        return names;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
