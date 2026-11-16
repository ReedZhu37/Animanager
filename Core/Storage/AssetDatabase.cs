using Animanager.Core.Models;
using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace Animanager.Core.Storage;

public sealed class AssetDatabase
{
    private readonly string _connection;
    private readonly string _root;
    public AssetDatabase(string path)
    {
        _root=Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS assets (
              id TEXT PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL,
              format TEXT NOT NULL, hash TEXT NOT NULL UNIQUE, tags TEXT NOT NULL,
              type TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0,
              size INTEGER NOT NULL, imported TEXT NOT NULL);
            CREATE VIRTUAL TABLE IF NOT EXISTS asset_search USING fts5(id UNINDEXED, name, tags);
            CREATE TRIGGER IF NOT EXISTS assets_insert AFTER INSERT ON assets BEGIN
              INSERT INTO asset_search(id,name,tags) VALUES(new.id,new.name,new.tags); END;
            CREATE TRIGGER IF NOT EXISTS assets_update AFTER UPDATE ON assets BEGIN
              DELETE FROM asset_search WHERE id=old.id;
              INSERT INTO asset_search(id,name,tags) VALUES(new.id,new.name,new.tags); END;
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var db = new SqliteConnection(_connection); db.Open(); return db; }
    public bool HasHash(string hash)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM assets WHERE hash=$hash";
        cmd.Parameters.AddWithValue("$hash", hash);
        return (long)cmd.ExecuteScalar()! > 0;
    }
    public AssetItem? Find(string idOrName)
    {
        using var db=Open();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT * FROM assets WHERE id=$value OR name=$value COLLATE NOCASE LIMIT 2";
        cmd.Parameters.AddWithValue("$value",idOrName);
        using var r=cmd.ExecuteReader();if(!r.Read())return null;
        var a=new AssetItem{Id=r.GetString(0),Name=r.GetString(1),VirtualPath=r.GetString(2),SourceFormat=r.GetString(3),FileHash=r.GetString(4),Tags=r.GetString(5),Type=r.GetString(6),Favorite=r.GetInt32(7)==1,Size=r.GetInt64(8),ImportedAt=r.GetString(9)};
        if(r.Read())throw new InvalidOperationException("存在同名资产，请使用 asset search 返回的完整 ID。");
        return a;
    }
    public AssetItem? FindByHash(string hash)
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT id FROM assets WHERE hash=$hash";cmd.Parameters.AddWithValue("$hash",hash);
        return cmd.ExecuteScalar() is string id?Find(id):null;
    }
    public bool Insert(AssetItem a)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO assets VALUES($id,$name,$path,$format,$hash,$tags,$type,$fav,$size,$date)";
        foreach (var pair in new Dictionary<string,object> { ["id"]=a.Id,["name"]=a.Name,["path"]=a.VirtualPath,
            ["format"]=a.SourceFormat,["hash"]=a.FileHash,["tags"]=a.Tags,["type"]=a.Type,["fav"]=a.Favorite?1:0,["size"]=a.Size,["date"]=a.ImportedAt })
            cmd.Parameters.AddWithValue("$"+pair.Key,pair.Value);
        return cmd.ExecuteNonQuery() > 0;
    }
    public void Update(AssetItem a)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE assets SET tags=$tags,type=$type,favorite=$fav WHERE id=$id";
        cmd.Parameters.AddWithValue("$tags",a.Tags); cmd.Parameters.AddWithValue("$type",a.Type);
        cmd.Parameters.AddWithValue("$fav",a.Favorite?1:0); cmd.Parameters.AddWithValue("$id",a.Id);
        cmd.ExecuteNonQuery();
        if(a.SourceFormat==".motion")Animanager.Core.Motion.MotionJson.Atomic(new LocalStorageProvider(_root).Resolve(a.VirtualPath)+".asset.json",a);
    }
    public (List<AssetItem> Items, int Total) Search(string query="", string category="All", int page=0, int pageSize=60)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        var tokens = Regex.Matches(query, @"[\p{L}\p{N}_]+").Select(m => "\"" + m.Value + "\"*").ToArray();
        var where = " WHERE ($cat='All' OR ($cat='Favorites' AND favorite=1) OR type=$cat)";
        if (!string.IsNullOrWhiteSpace(query))
        {
            where += " AND (name LIKE $q ESCAPE '!' OR tags LIKE $q ESCAPE '!'";
            cmd.Parameters.AddWithValue("$q", "%"+query.Replace("!","!!").Replace("%","!%").Replace("_","!_")+"%");
            if (tokens.Length>0) { where += " OR id IN (SELECT id FROM asset_search WHERE asset_search MATCH $fts)"; cmd.Parameters.AddWithValue("$fts",string.Join(" AND ",tokens)); }
            where += ")";
        }
        cmd.Parameters.AddWithValue("$cat",category);
        cmd.CommandText="SELECT count(*) FROM assets"+where;
        int total=Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText="SELECT * FROM assets"+where+" ORDER BY imported DESC,name LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit",pageSize); cmd.Parameters.AddWithValue("$offset",page*pageSize);
        using var reader=cmd.ExecuteReader(); var items=new List<AssetItem>();
        while(reader.Read()) items.Add(new AssetItem {Id=reader.GetString(0),Name=reader.GetString(1),VirtualPath=reader.GetString(2),SourceFormat=reader.GetString(3),FileHash=reader.GetString(4),Tags=reader.GetString(5),Type=reader.GetString(6),Favorite=reader.GetInt32(7)==1,Size=reader.GetInt64(8),ImportedAt=reader.GetString(9)});
        return (items,total);
    }
    // One query captures the entire selection; batch export must not inherit the UI's 60-item page limit.
    public List<AssetItem> SearchAll(string query="",string category="All")=>Search(query,category,0,int.MaxValue).Items;
}
