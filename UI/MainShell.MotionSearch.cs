using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Storage;
using Animanager.UI.Components;
using Animanager.UI.Shell;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private sealed record MotionReference(LibrarySession Library, AssetItem Asset, string Clip, double Start, double End,
        MotionSamples? Pose = null);
    private sealed record RankedMotion(LibraryAssetRow Row, MotionHit Hit);
    private MotionReference? _motionReference, _motionStart;
    private RankedMotion[] _motionRows = [];
    private int _motionSkipped;
    private SpinBox _motionWindow = null!, _motionMinimum = null!;
    private Label _motionHint = null!;
    private bool _customMotionWindow;

    private void BuildMotionFilter(VBoxContainer column)
    {
        column.AddChild(new HSeparator());
        column.AddChild(Label("动作相似度", false, 13));
        var actions = new HFlowContainer();
        column.AddChild(actions);
        Add("使用当前帧", "MotionUsePose", () => CaptureMotion(false));
        Add("标记片段起点", "MotionMarkStart", () =>
        {
            if (CurrentMotionFrame() is not { } point) return;
            _motionStart = point;
            _motionHint.Text = $"已标记 {point.Clip} · {point.Start:0.###} 秒。移动播放头后使用片段。";
        });
        Add("使用片段", "MotionUseRange", () => CaptureMotion(true));
        _motionWindow = Number("搜索开头（秒，0 为全片）", "MotionWindow", 3600, .1, 1);
        _motionMinimum = Number("最低相似度（分）", "MotionMinimum", 100, 1, 0);
        _motionHint = Wrapped("在预览中定位要匹配的姿势或片段。");
        column.AddChild(_motionHint);

        void Add(string text, string name, Action action)
        {
            var button = Button(text, action);
            button.Name = name;
            actions.AddChild(button);
        }
        SpinBox Number(string title, string name, double max, double step, double initial)
        {
            var row = Row(column);
            row.AddChild(Label(title, true, 12));
            var input = new SpinBox { Name = name, MaxValue = max, Step = step, Value = initial };
            row.AddChild(input);
            input.ValueChanged += _ =>
            {
                if (input == _motionWindow) _customMotionWindow = true;
                if (_motionReference != null) RefreshAfterFilterChange();
            };
            return input;
        }
    }

    private MotionReference? CurrentMotionFrame()
    {
        if (_selectedLibrary == null || _selected == null || _selectedKey != _previewAssetId ||
            !PendingAssetPreview.IsCompleted || _preview.CurrentClip.Length == 0 || AnimationComparisonActive)
        {
            _motionHint.Text = "请先打开一个人体动画，在单动画预览中定位播放头。";
            return null;
        }
        double t = _preview.Playhead;
        return new(_selectedLibrary, _selected, _preview.CurrentClip, t, t);
    }

    private void CaptureMotion(bool range)
    {
        if (CurrentMotionFrame() is not { } point) return;
        if (range)
        {
            if (_motionStart is not { } mark || mark.Library != point.Library || mark.Asset.Id != point.Asset.Id ||
                mark.Clip != point.Clip || point.End <= mark.Start)
            {
                _motionHint.Text = "请在同一片段中先标记起点，再将播放头移到终点。";
                return;
            }
            point = point with { Start = mark.Start };
        }
        else
        {
            try
            {
                point = point with { Pose = MotionSampleReader.CurrentPose(_preview) };
                if (point.Pose == null) { _motionHint.Text = "当前姿势缺少可识别的人体骨骼。"; return; }
            }
            catch (InvalidDataException ex) { _motionHint.Text = ex.Message; return; }
        }
        _motionReference = point;
        _motionStart = null;
        if (!_customMotionWindow) _motionWindow.SetValueNoSignal(range ? 0 : 1);
        _motionHint.Text = $"{point.Asset.Name} · {point.Clip} · {point.Start:0.###}–{point.End:0.###} 秒";
        RefreshAfterFilterChange();
    }

    private void ClearMotionFilter()
    {
        _motionStart = _motionReference = null;
        _motionRows = [];
        if (_motionHint != null) _motionHint.Text = "在预览中定位要匹配的姿势或片段。";
    }

    private async Task<MotionSamples[]> SamplesFor(LibrarySession library, AssetItem asset, CancellationToken ct)
    {
        using var activity = library.Api.BeginActivity();
        var cache = library.Api.BrowseCache;
        var source = await Task.Run(() => cache.PrepareMotion(asset), ct);
        ct.ThrowIfCancellationRequested();
        var samples = await Task.Run(() => cache.ReadMotion(source.Cache), ct);
        ct.ThrowIfCancellationRequested();
        if (samples == null)
        {
            samples = await MotionSampleReader.Read(source.Source, GetTree(), ct);
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => cache.SaveMotion(source.Cache, samples), ct);
        }
        ct.ThrowIfCancellationRequested();
        return samples;
    }

    private async Task<(RankedMotion[] Rows, int Skipped)> RankMotionRows(MotionReference reference,
        LibraryBrowseScope[] scopes, string query, AssetAttributeFilter filter, AssetTagFilter? tags, CancellationToken ct)
    {
        double window = _motionWindow.Value, minimum = _motionMinimum.Value;
        var source = reference.Pose ?? (await SamplesFor(reference.Library, reference.Asset, ct)).FirstOrDefault(c => c.Name == reference.Clip)
            ?? throw new InvalidOperationException("参考片段缺少可识别的人体骨骼轨道。");
        if (reference.Start < reference.End && source.Frame(reference.Start) == source.Frame(reference.End))
            throw new InvalidOperationException("片段过短，请扩大时间范围或使用当前帧。");
        var groups = await Task.WhenAll(scopes.Select(async scope => (scope.Library, Assets:
            await scope.Library.Api.GetMatchingAssetsAsync(query, scope.Category, filter, ct, tags, scope.MemberIdsJson))));
        var matches = new List<RankedMotion>();
        int done = 0, skipped = 0, total = groups.Sum(g => g.Assets.Count);
        foreach (var (library, assets) in groups)
        {
            using var activity = library.Api.BeginActivity();
            var cache = library.Api.BrowseCache;
            foreach (var batch in assets.Chunk(16))
            {
                ct.ThrowIfCancellationRequested();
                _count.Text = $"正在匹配 {done:N0} / {total:N0}";
                // Cached clips and split-body files can be ranked without a single scene-tree call.
                var uncachedScenes = await Task.Run(() =>
                {
                    var missing = new List<AssetItem>();
                    foreach (var asset in batch)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var identity = cache.PrepareMotion(asset);
                            var clips = cache.ReadMotion(identity.Cache);
                            if (clips == null)
                            {
                                if (!MotionJson.IsMotion(identity.Source)) { missing.Add(asset); continue; }
                                clips = MotionSampleReader.ReadBody(identity.Source, ct);
                                cache.SaveMotion(identity.Cache, clips);
                            }
                            Match(library, asset, clips);
                        }
                        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { Skip(asset, ex); }
                    }
                    return missing;
                }, ct);
                foreach (var asset in uncachedScenes)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var clips = await SamplesFor(library, asset, ct);
                        await Task.Run(() => Match(library, asset, clips), ct);
                    }
                    catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { Skip(asset, ex); }
                }
                done += batch.Length;
            }
        }
        ct.ThrowIfCancellationRequested();
        return (matches.OrderByDescending(m => m.Hit.Score).ThenBy(m => m.Row.Asset.Name, StringComparer.Ordinal)
            .ThenBy(m => m.Row.Library.Identity.Id).ThenBy(m => m.Row.Asset.Id).ToArray(), skipped);

        void Match(LibrarySession library, AssetItem asset, MotionSamples[] clips)
        {
            if (clips.Length == 0) { skipped++; return; }
            if (library == reference.Library && asset.Id == reference.Asset.Id)
                clips = clips.Where(c => c.Name != reference.Clip).ToArray();
            var hit = MotionSimilarity.Find(source, reference.Pose == null ? reference.Start : 0,
                reference.Pose == null ? reference.End : 0, clips, window, ct);
            if (hit != null && hit.Score >= minimum) matches.Add(new(new(library, new(asset, null)), hit));
        }

        void Skip(AssetItem asset, Exception ex)
        {
            skipped++;
            GD.PushWarning($"无法比较 {asset.Name} · {ex.Message}");
        }
    }

    private static Task<LibraryAssetPage> ReadMotionPage(RankedMotion[] matches, int page, CancellationToken ct) => Task.Run(() =>
    {
        var rows = matches.Skip(page * AssetPageSize).Take(AssetPageSize).Select(m =>
        {
            ct.ThrowIfCancellationRequested();
            var row = m.Row;
            return new LibraryAssetRow(row.Library, new(row.Asset, row.Library.Api.ReadBrowseAttributes(row.Asset, ct)));
        }).ToArray();
        return new LibraryAssetPage(rows, matches.Length, page);
    }, ct);

    private AssetCardModel BrowseCard(LibraryAssetRow row, int index, bool showLibrary)
    {
        var card = CardModel(row.Asset, row.Attributes, showLibrary ? row.Library.Identity.Name : "", AssetKey(row.Library, row.Asset.Id));
        if (_motionReference == null) return card;
        var hit = _motionRows[_page * AssetPageSize + index].Hit;
        card.MotionLabel = $"{hit.Score:0.#} 分 · {hit.Start:0.##}s";
        card.DetailLine = hit.Start == hit.End ? card.MotionLabel : $"{card.MotionLabel}–{hit.End:0.##}s";
        card.Tooltip += $"\n{hit.Clip} · {card.DetailLine}\n双击试接";
        return card;
    }
}
