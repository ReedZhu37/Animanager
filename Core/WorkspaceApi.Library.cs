namespace Animanager.Core;

public sealed partial class WorkspaceApi
{
    private readonly object _activityGate = new();
    private int _activities;
    private bool _maintenance, _retired;
    public bool LibraryBusy { get { lock (_activityGate) return _maintenance || _activities > 0; } }

    public IDisposable BeginActivity()
    {
        lock (_activityGate)
        {
            if (_maintenance || _retired) throw new InvalidOperationException("库正在维护或已切换，请重试。");
            _activities++;
            return new ActivityLease(() => { lock (_activityGate) _activities--; });
        }
    }

    internal IDisposable BeginMaintenance()
    {
        lock (_activityGate)
        {
            if (_maintenance || _activities > 0 || _retired)
                throw new InvalidOperationException("请等待当前任务完成，或取消任务后重试。");
            _maintenance = true;
            return new ActivityLease(() => { lock (_activityGate) _maintenance = false; });
        }
    }

    internal void Retire() { lock (_activityGate) _retired = true; }

    private sealed class ActivityLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    private void Edit(string[] ids, Func<Models.AssetItem, Models.AssetItem> update, bool trash = false)
    {
        using var activity = BeginActivity();
        Database.Edit(ids, update, trash);
    }

    public void RenameAsset(string asset, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("请输入资产名称。");
        Edit([Find(asset).Id], a => a with { Name = name });
    }

    public void SetAssetType(string id, string type) => Edit([id], a => a with { Type = type });
    public void SetFavorites(string[] ids, bool favorite) => Edit(ids, a => a with { Favorite = favorite });
    public void SetTrash(string[] ids, bool trash) => Edit(ids, a => a with { Trashed = trash }, !trash);

    public void ChangeTags(string[] ids, string text, bool add)
    {
        var tags = ParseTags(text).ToArray();
        if (tags.Length == 0) throw new ArgumentException("请输入至少一个标签。");
        Edit(ids, a =>
        {
            var current = ParseTags(a.Tags);
            var comparer = StringComparer.OrdinalIgnoreCase;
            return a with { Tags = string.Join(" ", add ? current.Union(tags, comparer) : current.Except(tags, comparer)) };
        });
    }

    private static IEnumerable<string> ParseTags(string text) =>
        text.Split([' ', ',', '，', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public Storage.AssetCollection CreateCollection(string name)
    {
        using var activity = BeginActivity();
        return Database.CreateCollection(name);
    }

    public void RenameCollection(string id, string name)
    {
        using var activity = BeginActivity();
        Database.RenameCollection(id, name);
    }

    public void DeleteCollection(string id)
    {
        using var activity = BeginActivity();
        Database.DeleteCollection(id);
    }

    public void SetCollectionMembership(string id, string[] ids, bool add)
    {
        using var activity = BeginActivity();
        Database.SetMembership(id, ids, add);
    }
}
