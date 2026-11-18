using Godot;

namespace Animanager.UI.Shell;

/// <summary>
/// The selection model shared by every asset view. Views own no selection state of their own:
/// switching from grid to list must preserve exactly what the user had chosen, including the
/// range anchor, so Shift-clicking still works after the switch.
/// </summary>
public sealed class AssetSelection : IReadOnlyCollection<string>
{
    /// <summary>Fired after any change. Views redraw; the shell updates its selection bar.</summary>
    public event Action? Changed;

    private readonly List<string> _order = [];          // current view order, for range maths
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private string? _anchor;

    public IReadOnlyCollection<string> Ids => _ids;
    public int Count => _ids.Count;
    public bool IsEmpty => _ids.Count == 0;
    public string? Anchor => _anchor;
    public void SetAnchor(string? id) => _anchor = id;
    public IEnumerator<string> GetEnumerator() => _ids.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(string id) { _ids.Add(id); Changed?.Invoke(); }
    public bool Remove(string id) { bool removed = _ids.Remove(id); if (removed) Changed?.Invoke(); return removed; }
    public void UnionWith(IEnumerable<string> ids) => AddRange(ids);
    public void IntersectWith(IEnumerable<string> ids) => Prune(ids);

    public bool Contains(string id) => _ids.Contains(id);

    /// <summary>
    /// Re-points the selection at a new page. Ids that are gone from the view stay selected so a
    /// cross-page export does not silently lose items; only range maths needs the local order.
    /// </summary>
    public void SetView(IEnumerable<string> viewOrder)
    {
        _order.Clear();
        _order.AddRange(viewOrder);
    }

    public void Select(string id)
    {
        if (_ids.Count == 1 && _ids.Contains(id)) return;
        _ids.Clear();
        if (id.Length > 0) _ids.Add(id);
        _anchor = id.Length > 0 ? id : null;
        Changed?.Invoke();
    }

    public void Toggle(string id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        _anchor = id;
        Changed?.Invoke();
    }

    public void SelectRange(string target)
    {
        int from = _anchor != null ? _order.IndexOf(_anchor) : -1;
        int to = _order.IndexOf(target);
        if (from < 0 || to < 0)
        {
            Select(target);
            return;
        }
        _ids.Clear();
        for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++) _ids.Add(_order[i]);
        Changed?.Invoke();
    }

    public void AddRange(IEnumerable<string> ids)
    {
        foreach (var id in ids) _ids.Add(id);
        Changed?.Invoke();
    }

    public void RemoveRange(IEnumerable<string> ids)
    {
        foreach (var id in ids) _ids.Remove(id);
        Changed?.Invoke();
    }

    public void SetAll(IEnumerable<string> ids)
    {
        _ids.Clear();
        foreach (var id in ids) _ids.Add(id);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_ids.Count == 0 && _anchor == null) return;
        _ids.Clear();
        _anchor = null;
        Changed?.Invoke();
    }

    /// <summary>Drops ids that no longer exist, keeping the anchor if it survives.</summary>
    public void Prune(IEnumerable<string> existing)
    {
        var live = existing as ICollection<string> ?? existing.ToHashSet(StringComparer.Ordinal);
        bool removed = false;
        foreach (var id in _ids.ToArray())
        {
            if (live.Contains(id)) continue;
            _ids.Remove(id);
            removed = true;
        }
        if (_anchor != null && !live.Contains(_anchor)) _anchor = null;
        if (removed) Changed?.Invoke();
    }

    public bool SameAs(AssetSelection other) =>
        _ids.SetEquals(other._ids) && _anchor == other._anchor;
}
