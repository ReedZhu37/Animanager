using Animanager.Core.Models;
using Animanager.Core.Motion;
using Microsoft.Data.Sqlite;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Animanager.Core.Storage;

public sealed record AssetCollection(string Id, string Name, string[] AssetIds)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentId { get; init; }
    public CollectionAssetLink[] LinkedAssets { get; init; } = [];
}
public sealed record CollectionAssetLink(string LibraryId, string AssetId);
public sealed record AssetTagGroup(string Id, string Name, string[] Tags);
public sealed record AssetCatalog(int Version, string Revision, AssetItem[] Assets, AssetCollection[] Collections)
{
    public AssetTagGroup[] TagGroups { get; init; } = [];
}

public sealed partial class AssetDatabase
{
    private const string Schema = """
        CREATE TABLE assets (
          id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL,
          format TEXT NOT NULL, hash TEXT NOT NULL UNIQUE, tags TEXT NOT NULL,
          type TEXT NOT NULL, favorite INTEGER NOT NULL, size INTEGER NOT NULL,
          imported TEXT NOT NULL, trashed INTEGER NOT NULL);
        CREATE VIRTUAL TABLE asset_search USING fts5(id UNINDEXED, name, tags);
        CREATE TRIGGER assets_insert AFTER INSERT ON assets BEGIN
          INSERT INTO asset_search VALUES(new.id,new.name,new.tags); END;
        CREATE TRIGGER assets_delete AFTER DELETE ON assets BEGIN
          DELETE FROM asset_search WHERE id=old.id; END;
        CREATE TRIGGER assets_update AFTER UPDATE ON assets BEGIN
          DELETE FROM asset_search WHERE id=old.id;
          INSERT INTO asset_search VALUES(new.id,new.name,new.tags); END;
        CREATE TABLE collection_assets (collection TEXT, asset TEXT, PRIMARY KEY(collection,asset));
        CREATE TABLE catalog_revision (value TEXT NOT NULL);
        """;
    private const string AssetColumns = "id,name,path,format,hash,tags,type,favorite,size,imported";
    private readonly object _gate = new();
    private readonly string _path, _catalogPath;
    private AssetCatalog _catalog;

    public const string IndexName = "index.sqlite";
    public const string CatalogName = "asset-catalog.json";
    public string Root { get; }
    internal string CatalogRevision { get { lock (_gate) return _catalog.Revision; } }

    public AssetDatabase(string path, bool rebuild = false, bool repairProjection = true)
    {
        _path = Path.GetFullPath(path);
        Root = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(Root);
        _catalogPath = Path.Combine(Root, CatalogName);
        LocalStorageProvider.RequireLocalPath(_path);
        LocalStorageProvider.RequireLocalPath(_catalogPath);
        if (File.Exists(_catalogPath))
        {
            _catalog = ReadCatalog(_catalogPath);
        }
        else
        {
            if (!repairProjection) throw new InvalidDataException("缺少持久资产清单：" + _catalogPath);
            var assets = File.Exists(_path) ? ReadLegacy(_path) : [];
            _catalog = new(1, NewId(), assets, []);
            SaveCatalog(_catalog);
        }

        if (rebuild || !IndexExistsAtRevision())
        {
            if (!repairProjection) throw new InvalidDataException("备份索引与持久资产清单版本不一致。");
            RebuildIndex();
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = transaction;
        return cmd;
    }

    private static AssetItem ReadAsset(SqliteDataReader r, bool current) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), VirtualPath = r.GetString(2), SourceFormat = r.GetString(3),
        FileHash = r.GetString(4), Tags = r.GetString(5), Type = r.GetString(6), Favorite = r.GetBoolean(7),
        Size = r.GetInt64(8), ImportedAt = r.GetString(9), Trashed = current && r.GetBoolean(10)
    };

    private static AssetItem[] ReadLegacy(string path)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        CheckSqlite(db);
        using var cmd = Command(db, "SELECT " + AssetColumns + " FROM assets");
        using var reader = cmd.ExecuteReader();
        var result = new List<AssetItem>();
        while (reader.Read()) result.Add(ReadAsset(reader, false));
        return result.ToArray();
    }

    internal static void ValidateLegacy(string path) => ReadLegacy(path);

    internal static AssetCatalog ReadCatalog(string path)
    {
        var catalog = MotionJson.Read<AssetCatalog>(path);
        if (catalog.Version != 1 || !Guid.TryParseExact(catalog.Revision, "N", out _) ||
            catalog.Assets == null || catalog.Collections == null ||
            catalog.Assets.Any(a => a == null || string.IsNullOrWhiteSpace(a.Id) || string.IsNullOrWhiteSpace(a.FileHash) ||
                a.Name == null || a.Tags == null || a.Type == null || a.SourceFormat == null || a.ImportedAt == null || string.IsNullOrWhiteSpace(a.VirtualPath)) ||
            catalog.Assets.Select(a => a.Id).Distinct().Count() != catalog.Assets.Length ||
            catalog.Assets.Select(a => a.FileHash).Distinct().Count() != catalog.Assets.Length)
            throw new InvalidDataException("持久资产清单损坏：" + path);
        var ids = catalog.Assets.Select(a => a.Id).ToHashSet();
        if (catalog.Collections.Any(c => c == null || !Guid.TryParseExact(c.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(c.Name) || c.AssetIds == null || c.AssetIds.Distinct().Count() != c.AssetIds.Length || c.AssetIds.Any(id => !ids.Contains(id)) ||
                c.LinkedAssets == null || c.LinkedAssets.Any(link => !ValidCollectionLink(link)) ||
                c.LinkedAssets.Distinct().Count() != c.LinkedAssets.Length) ||
            catalog.Collections.Select(c => c.Id).Distinct().Count() != catalog.Collections.Length ||
            catalog.Collections.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Collections.Length ||
            !ValidCollectionParents(catalog.Collections))
            throw new InvalidDataException("持久清单的集合关系损坏：" + path);
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (catalog.TagGroups == null || catalog.TagGroups.Any(group => group == null || !Guid.TryParseExact(group.Id, "N", out _) ||
                string.IsNullOrWhiteSpace(group.Name) || group.Tags == null || group.Tags.Any(tag => !IsTagToken(tag) || !tags.Add(tag))) ||
            catalog.TagGroups.Select(group => group.Id).Distinct().Count() != catalog.TagGroups.Length ||
            catalog.TagGroups.Select(group => group.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.TagGroups.Length)
            throw new InvalidDataException("持久清单的标签组损坏：" + path);
        return catalog;
    }

    private static bool ValidCollectionParents(AssetCollection[] collections)
    {
        var children = collections.ToLookup(c => c.ParentId);
        var pending = new Queue<AssetCollection>(children[null]);
        int visited = 0;
        while (pending.TryDequeue(out var parent))
        {
            visited++;
            foreach (var child in children[parent.Id]) pending.Enqueue(child);
        }
        return visited == collections.Length;
    }

    private static bool ValidCollectionLink(CollectionAssetLink? link) => link != null &&
        Guid.TryParseExact(link.LibraryId, "N", out _) && !string.IsNullOrWhiteSpace(link.AssetId);

    private void SaveCatalog(AssetCatalog catalog) => MotionJson.Atomic(_catalogPath, catalog);

    private bool IndexExistsAtRevision()
    {
        if (!File.Exists(_path)) return false;
        using var db = Open(_path, SqliteOpenMode.ReadOnly);
        using var exists = Command(db, "SELECT count(*) FROM sqlite_master WHERE name='catalog_revision' AND type='table'");
        if ((long)exists.ExecuteScalar()! == 0) return false;
        using var revision = Command(db, "SELECT value FROM catalog_revision");
        return (string?)revision.ExecuteScalar() == _catalog.Revision;
    }

    public AssetCatalog Snapshot()
    {
        lock (_gate)
            return _catalog with { Assets = _catalog.Assets.Select(a => a with { }).ToArray(), Collections = Collections(), TagGroups = TagGroups() };
    }

    public AssetCollection[] Collections()
    {
        lock (_gate)
            return _catalog.Collections.Select(c => c with { AssetIds = c.AssetIds.ToArray(), LinkedAssets = c.LinkedAssets.ToArray() }).ToArray();
    }

    private AssetTagGroup[] TagGroups()
    {
        lock (_gate)
            return _catalog.TagGroups.Select(group => group with { Tags = group.Tags.ToArray() }).ToArray();
    }

    internal static bool IsTagToken(string? tag) => tag != null && WorkspaceApi.ParseTags(tag).SequenceEqual([tag]);

    public string[] ExistingIds(IEnumerable<string> ids, bool trash)
    {
        lock (_gate)
        {
            var requested = ids.ToHashSet();
            return _catalog.Assets.Where(a => a.Trashed == trash && requested.Contains(a.Id)).Select(a => a.Id).ToArray();
        }
    }

    internal Dictionary<string, int> BrowseCategoryCounts()
    {
        lock (_gate)
        {
            var counts = new Dictionary<string, int> { ["All"] = 0, ["Animation"] = 0, ["Model"] = 0, ["Favorites"] = 0, ["Trash"] = 0 };
            foreach (var asset in _catalog.Assets)
            {
                if (asset.Trashed) { counts["Trash"]++; continue; }
                counts["All"]++;
                if (asset.Type is "Model" or "Animation") counts[asset.Type]++;
                if (asset.Favorite) counts["Favorites"]++;
            }
            return counts;
        }
    }

    public bool HasHash(string hash)
    {
        lock (_gate) return _catalog.Assets.Any(a => a.FileHash == hash);
    }

    public AssetItem? FindByHash(string hash)
    {
        lock (_gate) return _catalog.Assets.FirstOrDefault(a => a.FileHash == hash) is { } asset ? asset with { } : null;
    }

    public AssetItem? Find(string idOrName, bool trash = false)
    {
        lock (_gate)
        {
            var asset = _catalog.Assets.FirstOrDefault(a => a.Trashed == trash && a.Id == idOrName);
            if (asset != null) return asset with { };
            var matches = _catalog.Assets.Where(a => a.Trashed == trash && a.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length switch
            {
                0 => null, 1 => matches[0] with { },
                _ => throw new InvalidOperationException("存在同名资产，请使用完整资产 ID。")
            };
        }
    }

    public bool Insert(AssetItem asset) => Insert(asset, null);

    internal bool Insert(AssetItem asset, StoredDuration? duration)
    {
        lock (_gate)
        {
            if (_catalog.Assets.Any(a => a.Id == asset.Id || a.FileHash == asset.FileHash)) return false;
            Commit(_catalog with { Assets = [.. _catalog.Assets, asset with { }] }, duration);
            return true;
        }
    }

    public void Update(AssetItem asset) => Edit([asset.Id], current => current with
        { Name = asset.Name, Tags = asset.Tags, Type = asset.Type, Favorite = asset.Favorite });

    public void Edit(string[] ids, Func<AssetItem, AssetItem> update, bool trash = false)
    {
        lock (_gate)
        {
            var selected = RequireAssets(ids, trash).Select(a => a.Id).ToHashSet();
            Commit(_catalog with { Assets = _catalog.Assets.Select(a => selected.Contains(a.Id) ? update(a with { }) : a).ToArray() });
        }
    }

    public AssetCollection CreateCollection(string name, string? parentId = null)
    {
        lock (_gate)
        {
            if (parentId != null) GetCollection(parentId);
            var collection = new AssetCollection(NewId(), CollectionName(name), []) { ParentId = parentId };
            Commit(_catalog with { Collections = [.. _catalog.Collections, collection] });
            return collection;
        }
    }

    public void RenameCollection(string id, string name)
    {
        lock (_gate)
        {
            var collection = GetCollection(id);
            var replacement = collection with { Name = CollectionName(name, id) };
            Commit(_catalog with { Collections = _catalog.Collections.Select(c => c.Id == id ? replacement : c).ToArray() });
        }
    }

    public void DeleteCollection(string id)
    {
        lock (_gate)
        {
            var collection = GetCollection(id);
            var remaining = _catalog.Collections.Where(c => c.Id != id)
                .Select(c => c.ParentId == id ? c with { ParentId = collection.ParentId } : c).ToArray();
            Commit(_catalog with { Collections = remaining });
        }
    }

    public void SetMembership(string id, string[] ids, bool add) => SetMembership(id, ids, [], add);

    internal void SetMembership(string id, string[] ids, CollectionAssetLink[] links, bool add)
    {
        lock (_gate)
        {
            var collection = GetCollection(id);
            if (ids.Length > 0 || links.Length == 0) RequireAssets(ids, false);
            if (links.Any(link => !ValidCollectionLink(link))) throw new ArgumentException("集合包含无效的资产引用。");
            var members = (add ? collection.AssetIds.Union(ids) : collection.AssetIds.Except(ids)).ToArray();
            var linked = (add ? collection.LinkedAssets.Union(links) : collection.LinkedAssets.Except(links)).ToArray();
            if (collection.AssetIds.SequenceEqual(members) && collection.LinkedAssets.SequenceEqual(linked)) return;
            var replacement = collection with { AssetIds = members, LinkedAssets = linked };
            Commit(_catalog with { Collections = _catalog.Collections.Select(c => c.Id == id ? replacement : c).ToArray() });
        }
    }

    internal void Purge(string[] ids)
    {
        lock (_gate)
        {
            var selected = RequireAssets(ids, true).Select(a => a.Id).ToHashSet();
            Commit(_catalog with
            {
                Assets = _catalog.Assets.Where(a => !selected.Contains(a.Id)).ToArray(),
                Collections = _catalog.Collections.Select(c => c with { AssetIds = c.AssetIds.Where(id => !selected.Contains(id)).ToArray() }).ToArray()
            });
        }
    }

    private AssetCollection GetCollection(string id) =>
        _catalog.Collections.FirstOrDefault(c => c.Id == id) ?? throw new KeyNotFoundException("找不到集合：" + id);

    private string CollectionName(string name, string? except = null)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("请输入集合名称。");
        if (_catalog.Collections.Any(c => c.Id != except && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("已有同名集合：" + name);
        return name;
    }

    private AssetItem[] RequireAssets(string[] ids, bool trash)
    {
        var selected = ids.ToHashSet();
        if (selected.Count == 0) throw new ArgumentException("请先选择资产。");
        var result = _catalog.Assets.Where(a => a.Trashed == trash && selected.Contains(a.Id)).ToArray();
        if (result.Length != selected.Count) throw new KeyNotFoundException("有选中资产已不在当前列表，请刷新后重试。");
        return result;
    }

    private void Commit(AssetCatalog next, StoredDuration? duration = null)
    {
        var previous = _catalog;
        if (previous.Assets.SequenceEqual(next.Assets) && previous.Collections.SequenceEqual(next.Collections) &&
            previous.TagGroups.SequenceEqual(next.TagGroups)) return;
        if (!IndexExistsAtRevision()) RebuildIndex();
        next = next with { Revision = NewId() };
        using var db = Open(_path);
        using var transaction = db.BeginTransaction();
        Project(db, transaction, next, previous);
        if (duration != null) WriteDurations(db, transaction, [duration]);
        SaveCatalog(next);
        _catalog = next;
        try { transaction.Commit(); }
        catch (Exception error)
        {
            transaction.Dispose();
            db.Dispose();
            try { RebuildIndex(); }
            catch (Exception rebuild) { throw new IOException("资产清单已保存，索引需要重建。", new AggregateException(error, rebuild)); }
        }
    }

    private static void Project(SqliteConnection db, SqliteTransaction tx, AssetCatalog next, AssetCatalog? previous = null)
    {
        var oldAssets = previous?.Assets.ToDictionary(a => a.Id) ?? [];
        foreach (var asset in next.Assets)
        {
            if (oldAssets.Remove(asset.Id, out var old) && old == asset) continue;
            using var cmd = Command(db, """
                INSERT INTO assets VALUES($id,$name,$path,$format,$hash,$tags,$type,$favorite,$size,$date,$trashed)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,tags=excluded.tags,type=excluded.type,
                  favorite=excluded.favorite,trashed=excluded.trashed
                """, tx);
            cmd.Parameters.AddWithValue("$id", asset.Id);
            cmd.Parameters.AddWithValue("$name", asset.Name);
            cmd.Parameters.AddWithValue("$path", asset.VirtualPath);
            cmd.Parameters.AddWithValue("$format", asset.SourceFormat);
            cmd.Parameters.AddWithValue("$hash", asset.FileHash);
            cmd.Parameters.AddWithValue("$tags", asset.Tags);
            cmd.Parameters.AddWithValue("$type", asset.Type);
            cmd.Parameters.AddWithValue("$favorite", asset.Favorite);
            cmd.Parameters.AddWithValue("$size", asset.Size);
            cmd.Parameters.AddWithValue("$date", asset.ImportedAt);
            cmd.Parameters.AddWithValue("$trashed", asset.Trashed);
            cmd.ExecuteNonQuery();
        }
        foreach (var id in oldAssets.Keys)
        {
            using var cmd = Command(db, "DELETE FROM assets WHERE id=$id", tx);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        if (previous == null || !previous.Collections.SequenceEqual(next.Collections))
        {
            using var clear = Command(db, "DELETE FROM collection_assets", tx);
            clear.ExecuteNonQuery();
            using var add = Command(db, "INSERT INTO collection_assets VALUES($collection,$asset)", tx);
            var collectionParam = add.Parameters.Add("$collection", SqliteType.Text);
            var assetParam = add.Parameters.Add("$asset", SqliteType.Text);
            foreach (var collection in next.Collections)
                foreach (var id in collection.AssetIds)
                {
                    collectionParam.Value = collection.Id;
                    assetParam.Value = id;
                    add.ExecuteNonQuery();
                }
        }
        using var revision = Command(db, "DELETE FROM catalog_revision; INSERT INTO catalog_revision VALUES($revision)", tx);
        revision.Parameters.AddWithValue("$revision", next.Revision);
        revision.ExecuteNonQuery();
    }

    public void RebuildIndex()
    {
        lock (_gate)
        {
            var temporary = _path + "." + NewId() + ".tmp";
            var moved = new List<(string Source, string Backup)>();
            try
            {
                using (var db = Open(temporary, SqliteOpenMode.ReadWriteCreate))
                {
                    using var schema = Command(db, Schema);
                    schema.ExecuteNonQuery();
                    using var tx = db.BeginTransaction();
                    Project(db, tx, _catalog);
                    tx.Commit();
                    CheckSqlite(db);
                }
                var backup = Path.Combine(Root, "recovery", NewId());
                foreach (var path in new[] { _path, _path + "-wal", _path + "-shm", _path + "-journal" }.Where(File.Exists))
                {
                    LocalStorageProvider.RequireLocalPath(backup);
                    Directory.CreateDirectory(backup);
                    var destination = Path.Combine(backup, Path.GetFileName(path));
                    File.Move(path, destination);
                    moved.Add((path, destination));
                }
                File.Move(temporary, _path);
            }
            catch
            {
                foreach (var (source, backup) in moved.AsEnumerable().Reverse()) File.Move(backup, source);
                throw;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void CheckSqlite(SqliteConnection db)
    {
        using var check = Command(db, "PRAGMA integrity_check");
        if ((string?)check.ExecuteScalar() != "ok") throw new InvalidDataException("SQLite 索引完整性检查失败。");
    }

    public void CheckIndex()
    {
        lock (_gate)
        {
            using var db = Open(_path, SqliteOpenMode.ReadOnly);
            CheckSqlite(db);
            using (var rows = Command(db, "SELECT " + AssetColumns + ",trashed FROM assets ORDER BY id"))
            using (var reader = rows.ExecuteReader())
            {
                foreach (var asset in _catalog.Assets.OrderBy(a => a.Id, StringComparer.Ordinal))
                    if (!reader.Read() || ReadAsset(reader, true) != asset) throw new InvalidDataException("索引记录不一致：" + asset.Id);
                if (reader.Read()) throw new InvalidDataException("索引包含未知资产：" + reader.GetString(0));
            }
            CheckIndexReferences(db, "SELECT collection,asset FROM collection_assets",
                _catalog.Collections.SelectMany(c => c.AssetIds.Select(id => (c.Id, id, ""))), false);
            CheckIndexReferences(db, "SELECT id,name,tags FROM asset_search",
                _catalog.Assets.Select(a => (a.Id, a.Name, a.Tags)), true);
            if (!IndexExistsAtRevision()) throw new InvalidDataException("索引版本与持久清单不一致。");
        }
    }

    private static void CheckIndexReferences(SqliteConnection db, string sql, IEnumerable<(string, string, string)> expected, bool search)
    {
        var remaining = expected.ToHashSet();
        using var cmd = Command(db, sql);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!remaining.Remove((reader.GetString(0), reader.GetString(1), search ? reader.GetString(2) : "")))
                throw new InvalidDataException("索引引用不一致：" + reader.GetString(search ? 0 : 1));
        if (remaining.Count != 0)
            throw new InvalidDataException("索引缺少引用：" + string.Join(", ", remaining.Select(x => search ? x.Item1 : x.Item2).Distinct()));
    }

    public void BackupIndex(string destination)
    {
        lock (_gate)
        {
            using var db = Open(_path, SqliteOpenMode.ReadOnly);
            using var backup = Open(destination, SqliteOpenMode.ReadWriteCreate);
            db.BackupDatabase(backup);
            CheckSqlite(backup);
        }
    }

    public (List<AssetItem> Items, int Total) Search(string query = "", string category = "All", int page = 0, int pageSize = 60)
        => Search(query, category, Animanager.Core.Browse.AssetSort.Default, page, pageSize);

    public (List<AssetItem> Items, int Total) Search(string query, string category, Animanager.Core.Browse.AssetSort sort, int page = 0, int pageSize = 60) =>
        Search(query, category, sort, page, pageSize, null);

    internal (List<AssetItem> Items, int Total) Search(string query, string category, Animanager.Core.Browse.AssetSort sort,
        int page, int pageSize, string? memberIdsJson)
    {
        lock (_gate) return SearchIndex(_path, query, category, sort, page, pageSize, memberIdsJson);
    }

    internal static List<AssetItem> SearchClosedCollectionMembers(LocalStorageProvider storage, string query, string memberIdsJson) =>
        SearchIndex(storage.Resolve(IndexName), query, "All", Animanager.Core.Browse.AssetSort.Default, 0, int.MaxValue, memberIdsJson).Items;

    private static (List<AssetItem> Items, int Total) SearchIndex(string path, string query, string category,
        Animanager.Core.Browse.AssetSort sort, int page, int pageSize, string? memberIdsJson)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = db.CreateCommand();
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(m => "\"" + m.Value + "\"*").ToArray();
        var where = """
            WHERE trashed=($cat='Trash') AND ($cat IN ('All','Trash') OR ($cat='Favorites' AND favorite=1)
                OR type=$cat OR id IN (SELECT asset FROM collection_assets WHERE collection=$cat))
            """;
        if (memberIdsJson != null)
        {
            where += " AND id IN (SELECT value FROM json_each($members))";
            cmd.Parameters.AddWithValue("$members", memberIdsJson);
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            where += " AND (name LIKE $q ESCAPE '!' OR tags LIKE $q ESCAPE '!'";
            cmd.Parameters.AddWithValue("$q", "%" + query.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%");
            if (tokens.Length > 0)
            {
                where += " OR id IN (SELECT id FROM asset_search WHERE asset_search MATCH $fts)";
                cmd.Parameters.AddWithValue("$fts", string.Join(" AND ", tokens));
            }
            where += ")";
        }
        cmd.Parameters.AddWithValue("$cat", category);
        cmd.CommandText = "SELECT count(*) FROM assets " + where;
        int total = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT " + AssetColumns + ",trashed FROM assets " + where + " ORDER BY " + sort.SqlOrder + " LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (long)page * pageSize);
        using var reader = cmd.ExecuteReader();
        var items = new List<AssetItem>();
        while (reader.Read()) items.Add(ReadAsset(reader, true));
        return (items, total);
    }

    // Batch export must not inherit the UI's page limit.
    public List<AssetItem> SearchAll(string query = "", string category = "All") => Search(query, category, 0, int.MaxValue).Items;

    internal List<AssetItem> SearchAll(string query, string category, string? memberIdsJson) =>
        Search(query, category, Animanager.Core.Browse.AssetSort.Default, 0, int.MaxValue, memberIdsJson).Items;
}
