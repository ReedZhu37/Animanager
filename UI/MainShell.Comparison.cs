using Animanager.Core;
using Animanager.Core.Retarget;
using Animanager.Core.Storage;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.UI;

public partial class MainShell
{
    private CancellationTokenSource? _comparisonRequest;
    private LibrarySession? _comparisonLoadingLibrary;
    private ComparisonDropHint? _comparisonDropHint, _hiddenPreviewDropHint;
    private bool AnimationComparisonActive => _preview.AssetComparison != null ||
        _comparisonRequest is { IsCancellationRequested: false };
    internal Task PendingAnimationComparison { get; private set; } = Task.CompletedTask;

    private void BuildPreviewLocateMenu(Preview3DViewport viewport)
    {
        var button = viewport.LocateAssetButton;
        var menu = new PopupMenu { Name = "PreviewLocateMenu", AllowSearch = true };
        viewport.AddChild(menu);
        button.Pressed += () => LibraryAction(() =>
        {
            _previewTabs.Activate((PreviewStage)viewport.GetParent(), reveal: false);
            if (_previewAssetId.Length == 0) return;
            if (_preview.AssetComparison is not { } comparison)
            {
                LocatePreviewAsset(_previewAssetId);
                return;
            }

            menu.Clear();
            var duplicateNames = comparison.Entries.GroupBy(entry => entry.Label.Text)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
            foreach (var entry in comparison.Entries)
            {
                string key = entry.Key ?? _previewAssetId;
                string name = entry.Label.Text;
                if (duplicateNames.Contains(name) && FindLiveAsset(key) is { } row)
                    name += " · " + row.Asset.VirtualPath;
                int index = menu.ItemCount;
                menu.AddItem(Typography.Fit(Typography.Regular, name, Shell.Type.Body, 360));
                menu.SetItemMetadata(index, key);
                menu.SetItemTooltip(index, name);
            }
            var bounds = button.GetGlobalRect();
            var position = GetWindow().GetFinalTransform() * new Vector2(bounds.Position.X, bounds.End.Y);
            menu.ResetSize();
            menu.PopupOnParent(new Rect2I((Vector2I)position, Vector2I.One));
        });
        menu.IndexPressed += index => LibraryAction(() =>
        {
            if (_preview != viewport) return;
            string key = menu.GetItemMetadata((int)index).AsString();
            if (_preview.AssetComparison is { } comparison && (key == _previewAssetId || comparison.Contains(key)))
                LocatePreviewAsset(key);
        });
    }

    private ComparisonDropHint BuildComparisonDropHint(Control parent, string name, string text)
    {
        var hint = new ComparisonDropHint(name, text);
        hint.SetDragForwarding(default,
            Callable.From<Vector2, Variant, bool>((_, data) => AssetBrowser.DraggedAssets(data).Any(CanCompareAsset)),
            Callable.From<Vector2, Variant>((_, data) => CompareAssets(AssetBrowser.DraggedAssets(data))));
        parent.AddChild(hint);
        return hint;
    }

    public override void _Notification(int what)
    {
        if (_comparisonDropHint == null || _hiddenPreviewDropHint == null) return;
        if (what == NotificationDragBegin)
        {
            var keys = AssetBrowser.DraggedAssets(GetViewport().GuiGetDragData());
            bool canCompare = keys.Any(CanCompareAsset);
            bool sameAnimation = !canCompare && keys.Contains(_previewAssetId);
            if (sameAnimation || canCompare)
            {
                var hint = _workspace.IsPanelVisible("preview") ? _comparisonDropHint : _hiddenPreviewDropHint;
                hint.ShowForDrag(sameAnimation);
            }
        }
        else if (what == NotificationDragEnd)
        {
            _comparisonDropHint.Hide();
            _hiddenPreviewDropHint.Hide();
        }
    }

    private bool CanCompareAsset(string key) =>
        !_closing && !_libraryLoading && _comparisonRequest == null && _category != "Trash" &&
        _selectedKey != null && _selectedKey == _previewAssetId && key != _selectedKey &&
        PendingAssetPreview.IsCompleted && PendingRetargetPreview.IsCompleted && _preview.Model != null &&
        _preview.AssetComparison?.Contains(key) != true && DragSourceLibrary(key) != null;

    private void CompareAssets(IEnumerable<string> keys)
    {
        var candidates = keys.Distinct().Where(CanCompareAsset).ToArray();
        if (candidates.Length == 0) return;
        if (!_workspace.IsPanelVisible("preview")) _workspace.ShowPanel("preview");
        PendingAnimationComparison = Task.WhenAll(PendingAnimationComparison, LoadComparisonAssets(candidates));
    }

    private async Task LoadComparisonAssets(string[] keys)
    {
        var primaryLibrary = _selectedLibrary;
        var primaryKey = _selectedKey;
        var primaryModel = _preview.Model;
        var character = _preview.AppliedRetarget is { } applied
            ? applied with { SourceRig = "", SourceHash = "" } : null;
        bool showLibrary = LoadedLibraries().Length > 1;
        string primaryName = showLibrary ? _selectedLibrary!.Identity.Name + " · " + _selected!.Name : _selected!.Name;
        using var request = new CancellationTokenSource();
        _comparisonRequest = request;
        bool Current() => !_closing && _selectedLibrary == primaryLibrary && _selectedKey == primaryKey && _preview.Model == primaryModel &&
            _comparisonRequest == request && !request.IsCancellationRequested;
        try
        {
            int added = 0;
            var failures = new List<string>();
            foreach (var key in keys)
            {
                if (!Current()) return;
                if (FindLiveAsset(key) is not { } row) continue;
                string name = showLibrary ? row.Library.Identity.Name + " · " + row.Asset.Name : row.Asset.Name;
                _comparisonLoadingLibrary = row.Library;
                try
                {
                    _stage.SetLoadStatus("正在准备对比 · " + name + "…");
                    using var activity = row.Library.Api.BeginActivity();
                    var path = await row.Library.Api.Storage.AcquireLocalFileAsync(row.Asset.VirtualPath, request.Token);
                    if (!Current()) return;
                    var ct = request.Token;
                    using var source = await PreviewSource.LoadAsync(path, ct, fullTracks: character != null);
                    if (!Current()) return;
                    if (character != null && source.HasAnimation)
                    {
                        RetargetBaker.RejectAdditive(path);
                        var readiness = await AssetReadiness.InspectPreviewAsync(source.Root, GetTree(), ct);
                        if (!Current()) return;
                        var options = WorkspaceApi.ResolveRetargetRig(source.Root, character, readiness, out var rig);
                        var result = await row.Library.Api.PreviewRetargetAsync(source, options, rig,
                            () => _previewTargets.InstantiateAsync(options, ct), ct);
                        try
                        {
                            if (!Current()) return;
                            if (FindLiveAsset(key) == null) continue;
                            _preview.AddComparisonAsset(primaryName, key, name, result.Root);
                        }
                        finally { if (result.Root.GetParent() == null) result.Root.Free(); }
                    }
                    else
                    {
                        if (FindLiveAsset(key) == null) continue;
                        _preview.AddComparisonAsset(primaryName, key, name, source.Root);
                        source.TakeRoot();
                    }
                    UpdateTransport();
                    added++;
                }
                catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
                {
                    failures.Add(name + " · " + error.Message);
                }
            }
            if (!Current()) return;
            if (added > 0) _preview.FrameModel();
            _stage.SetLoadStatus(failures.Count == 0 ? "" : "无法对比 · " + string.Join("\n", failures));
            SetStatus(failures.Count > 0 ? $"{failures.Count} 个资产无法对比" :
                $"正在对比 {_preview.AssetComparison?.Count ?? 1} 个资产");
        }
        catch (OperationCanceledException)
        {
            if (!_closing && _comparisonRequest == request && _selectedKey == primaryKey && _preview.Model == primaryModel)
                _stage.SetLoadStatus("");
        }
        finally
        {
            if (_comparisonRequest == request)
            {
                _comparisonRequest = null;
                _comparisonLoadingLibrary = null;
            }
        }
    }

    private void RefreshComparisonAssets()
    {
        foreach (var stage in _previewTabs.Stages)
        {
            var viewport = stage.Viewport;
            if (viewport.AssetComparison is not { } comparison) continue;
            foreach (var (key, _) in comparison.ComparedAssets.ToArray())
                if (FindLiveAsset(key) == null)
                {
                    if (viewport == _preview) RemoveComparisonAsset(key);
                    else viewport.RemoveComparisonAsset(key);
                }
        }
    }

    private void RemoveComparisonAsset(string key)
    {
        if (!_preview.RemoveComparisonAsset(key)) return;
        UpdateTransport();
        SetStatus(_preview.AssetComparison is { } comparison ? $"正在对比 {comparison.Count} 个资产" : "已退出对比");
    }

    private void EndAnimationComparison()
    {
        if (!AnimationComparisonActive) return;
        _comparisonRequest?.Cancel();
        bool wasComparing = _preview.AssetComparison != null;
        _preview.EndAnimationComparison();
        _stage.SetLoadStatus("");
        UpdateTransport();
        SetStatus(wasComparing ? "已退出对比" : "已取消对比");
    }
}
