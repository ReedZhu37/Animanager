using Animanager.Core;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Retarget;
using Animanager.Core.Storage;
using Animanager.UI.Components;
using Animanager.UI.Panels;
using Animanager.UI.Shell;
using Godot;

namespace Animanager.UI;

public partial class MainShell
{
    private sealed class PreviewTab(PreviewStage stage)
    {
        internal PreviewStage Stage { get; } = stage;
        internal LibrarySession? Library;
        internal AssetItem? Asset;
        internal LibrarySession? DisplayedLibrary;
        internal AssetItem? DisplayedAsset;
        internal PreviewLoad? Load;
        internal string CharacterStatus = "";
        internal string RetargetMessage = "";
    }

    private sealed class PreviewLoad(LibrarySession library, AssetItem asset, CancellationToken external) : IDisposable
    {
        internal LibrarySession Library { get; } = library;
        internal AssetItem Asset { get; } = asset;
        internal CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(external);
        internal Task Task { get; set; } = Task.CompletedTask;
        public void Dispose() => Cancellation.Dispose();
    }

    private PreviewTabs _previewTabs = null!;
    private PreviewTab _activePreviewTab = null!;
    private BoneTreePanel _bonePanel = null!;
    private readonly Dictionary<PreviewStage, PreviewTab> _previewTabsByStage = [];
    private readonly HashSet<PreviewLoad> _previewLoads = [];
    private readonly VBoxContainer _previewCharacterControls = new();
    private readonly VBoxContainer _previewWeaponControls = new();
    private event Action? FocusedViewportChanged;
    internal Task PendingAssetPreview => _activePreviewTab?.Load?.Task ?? Task.CompletedTask;

    private Control BuildPreview()
    {
        _previewTabs = new PreviewTabs();
        _previewTabs.Activated += FocusPreviewTab;
        _previewTabs.CloseRequested += stage => ClosePreviewTab(_previewTabsByStage[stage]);
        var first = CreatePreviewTab();
        _previewTabs.Activate(first.Stage);
        BuildCharacterSelector(_previewCharacterControls);
        return _previewTabs;
    }

    private PreviewTab CreatePreviewTab()
    {
        var stage = new PreviewStage();
        var viewport = stage.Viewport;
        var tab = new PreviewTab(stage);
        _previewTabsByStage.Add(stage, tab);
        _previewTabs.AddStage(stage);
        stage.SettingsPressed += () =>
        {
            _previewTabs.Activate(stage, reveal: false);
            stage.SyncSettings();
            PopupFit(stage.SettingsDialog, new(520, 540));
        };
        viewport.ResetCameraOnAnimationChange = _preferences.ResetCameraOnAnimationChange;
        stage.ResetCameraToggle.SetPressedNoSignal(_preferences.ResetCameraOnAnimationChange);
        stage.ResetCameraToggle.Toggled += enabled =>
        {
            _preferences.ResetCameraOnAnimationChange = enabled;
            foreach (var previewStage in _previewTabs.Stages)
            {
                previewStage.Viewport.ResetCameraOnAnimationChange = enabled;
                previewStage.ResetCameraToggle.SetPressedNoSignal(enabled);
            }
            SaveSoon();
        };
        viewport.ComparisonExitRequested += () =>
        {
            if (tab == _activePreviewTab) EndAnimationComparison();
            else viewport.EndAnimationComparison();
        };
        viewport.ComparisonRemoveRequested += key =>
        {
            _previewTabs.Activate(stage, reveal: false);
            RemoveComparisonAsset(key);
        };
        viewport.SourceLoaded += () =>
        {
            if (tab != _activePreviewTab) return;
            _retargetCancellation?.Cancel();
            _retargetEnabled.ButtonPressed = false;
        };
        BuildPreviewLocateMenu(viewport);
        stage.PlayPressed += () => { _previewTabs.Activate(stage, reveal: false); TogglePlayback(); };
        stage.SeekRequested += time => { _previewTabs.Activate(stage, reveal: false); SeekPlayback(time); };
        stage.StepRequested += direction =>
        {
            _previewTabs.Activate(stage, reveal: false);
            SeekPlayback(viewport.TransportPlayhead + direction * viewport.TransportStep);
        };
        stage.SpeedChanged += viewport.SetSpeed;
        stage.LoopToggled += () =>
        {
            viewport.SetLoop(!viewport.LoopEnabled);
            stage.SetLoop(viewport.LoopEnabled);
        };
        stage.SetLoop(true);
        stage.ClayToggled += () => viewport.SetClay(!viewport.ClayEnabled);
        stage.FramePressed += viewport.FrameModel;
        stage.BonesPressed += () => _workspace.ShowPanel("bones");
        stage.ClipSelected += clip =>
        {
            _previewTabs.Activate(stage, reveal: false);
            bool playing = viewport.Playing;
            viewport.SelectClip(clip);
            if (playing) viewport.TogglePlay();
            UpdateTransport();
        };
        return tab;
    }

    private void FocusPreviewTab(PreviewStage stage)
    {
        if (_stage != null)
        {
            _stage.SettingsDialog.Hide();
            _retargetCancellation?.Cancel();
            if (_retargetSourceRequest != null || _comparisonRequest != null) _stage.SetLoadStatus("");
            StopRequest(ref _retargetSourceRequest);
            if (_comparisonRequest != null)
            {
                StopRequest(ref _comparisonRequest);
                _comparisonLoadingLibrary = null;
            }
        }
        _activePreviewTab = _previewTabsByStage[stage];
        _stage = stage;
        _preview = stage.Viewport;
        MoveControls(_previewCharacterControls, stage.CharacterSlot);
        MoveControls(_previewWeaponControls, stage.WeaponSlot);
        if (_comparisonDropHint == null)
            _comparisonDropHint = BuildComparisonDropHint(_preview, "ComparisonDropHint", "拖入视口即可对比");
        else
        {
            _comparisonDropHint.Hide();
            _comparisonDropHint.Reparent(_preview, false);
        }
        _comparisonDropHint.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _bonePanel?.BindPreview(_preview);
        _commands?.BindPreview(_preview);
        FocusedViewportChanged?.Invoke();
        SyncFocusedPreviewSelection();

        static void MoveControls(Control controls, Control target)
        {
            if (controls.GetParent() == null) target.AddChild(controls);
            else controls.Reparent(target, false);
        }
    }

    private PreviewTab? FindPreviewTab(LibrarySession library, string id) =>
        _previewTabsByStage.Values.FirstOrDefault(tab => tab.Library?.Identity.Id == library.Identity.Id && tab.Asset?.Id == id);

    private void OpenAssetInPreview(LibrarySession library, AssetItem asset, bool inNewTab = false)
    {
        if (_closing || _libraryLoading || asset.Trashed) return;
        if (!_workspace.IsPanelVisible("preview")) _workspace.ShowPanel("preview");
        var tab = FindPreviewTab(library, asset.Id);
        if (tab != null)
        {
            _previewTabs.Activate(tab.Stage);
            if (tab.Load != null || (tab.DisplayedLibrary == library && tab.DisplayedAsset?.Id == asset.Id)) return;
        }
        else if (inNewTab && (_activePreviewTab.Asset != null || _preview.Model != null))
        {
            _previewTabs.Activate(CreatePreviewTab().Stage);
        }
        _ = ReplaceActivePreview(library, asset);
    }

    internal Task PreviewAssetAsync(AssetItem asset, CancellationToken ct = default) =>
        PreviewAssetAsync(_libraries.Active!, asset, ct);

    private Task PreviewAssetAsync(LibrarySession library, AssetItem asset, CancellationToken ct = default)
    {
        var key = AssetKey(library, asset.Id);
        if (!_assetSelection.Contains(key)) _assetSelection.Select(key);
        var tab = FindPreviewTab(library, asset.Id);
        if (tab != null && tab != _activePreviewTab)
        {
            _previewTabs.Activate(tab.Stage);
            if (tab.Load is { } load) return load.Task.WaitAsync(ct);
            if (tab.DisplayedAsset?.Id == asset.Id) return Task.CompletedTask;
        }
        var pending = ReplaceActivePreview(library, asset, ct);
        PendingRetargetPreview = pending;
        return pending;
    }

    private Task ReplaceActivePreview(LibrarySession library, AssetItem asset, CancellationToken ct = default)
    {
        EndAnimationComparison();
        _retargetCancellation?.Cancel();
        StopRequest(ref _retargetSourceRequest);
        return StartPreviewLoad(_activePreviewTab, library, asset, ct);
    }

    private Task StartPreviewLoad(PreviewTab tab, LibrarySession library, AssetItem asset, CancellationToken ct = default)
    {
        tab.Load?.Cancellation.Cancel();
        var request = new PreviewLoad(library, asset, ct);
        tab.Load = request;
        tab.Asset = asset;
        tab.Library = library;
        tab.RetargetMessage = "";
        tab.CharacterStatus = "";
        _previewLoads.Add(request);
        foreach (var opened in _previewTabsByStage.Values) SetPreviewTabLabel(opened);
        tab.Stage.SetLoadStatus("正在准备 " + asset.Name + "…");
        if (tab == _activePreviewTab) SyncFocusedPreviewSelection();
        request.Task = LoadPreviewTabAsync(tab, request, _previewCharacterId);
        return request.Task;
    }

    private async Task LoadPreviewTabAsync(PreviewTab tab, PreviewLoad request, string characterId)
    {
        var stage = tab.Stage;
        var viewport = stage.Viewport;
        var library = request.Library;
        var asset = request.Asset;
        var api = library.Api;
        var ct = request.Cancellation.Token;
        bool resumePlayback = viewport.Playing;
        bool Current() => !_closing && tab.Load == request && !ct.IsCancellationRequested;
        try
        {
            // Draw the new tab and its loading state before creating its 3D scene.
            var tree = GetTree();
            for (int frame = 0; frame < 2; frame++)
            {
                await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                ct.ThrowIfCancellationRequested();
                if (!Current()) return;
            }
            using var activity = api.BeginActivity();
            var path = await api.Storage.AcquireLocalFileAsync(asset.VirtualPath, ct);
            if (!Current()) return;
            var target = _characterEntries.FirstOrDefault(entry => entry.Id == characterId && entry.Compatible);
            RetargetOptions SelectedOptions(CharacterEntry entry)
            {
                var saved = new RetargetOptions();
                try { saved = api.RetargetPresets.ForAsset(asset.Id) ?? saved; }
                catch (Exception error) when (error is IOException or ArgumentException or System.Text.Json.JsonException)
                {
                    tab.RetargetMessage = error.Message;
                }
                return (saved with
                {
                    TargetRig = entry.Family, Character = entry.Reference, SourceRig = "", SourceHash = "", TargetHash = ""
                }).Snapshot();
            }
            var options = target != null && asset.Type != "Model" ? SelectedOptions(target) : null;

            MotionPlayback.SourceData? motion = null;
            if (MotionJson.IsMotion(path))
            {
                using var preloadedMotion = await TakePreloadedSourceAsync(library, asset, path, ct);
                if (!Current()) return;
                motion = await Task.Run(() => MotionPlayback.ReadSource(path, verifySourceObjects: true, ct, preloadedMotion?.Motion), ct);
                if (!Current()) return;
            }
            var cacheKey = options == null ? null : await RetargetPreviewCache.KeyAsync(path, options, ct, motion);
            if (!Current()) return;
            if (_characterCache.Find(cacheKey) is { } cached) AttachCharacter(cached, null);
            else
            {
                using var prepared = motion == null ? await TakePreloadedSourceAsync(library, asset, path, ct) : null;
                if (!Current()) return;
                using var source = prepared?.Scene is not { } parsed
                    ? await PreviewSource.LoadAsync(path, ct, fullTracks: target != null, decodedMotion: motion)
                    : PreviewSource.FromParsed(path, parsed, fullTracks: target != null);
                if (!Current()) return;
                if (target != null && source.HasAnimation)
                {
                    try
                    {
                        RetargetBaker.RejectAdditive(path);
                        var readiness = await AssetReadiness.InspectPreviewAsync(source.Root, tree, ct);
                        if (!Current()) return;
                        var resolved = WorkspaceApi.ResolveRetargetRig(source.Root, options ?? SelectedOptions(target), readiness, out var rig);
                        stage.SetLoadStatus("正在应用角色 · " + target.Name + "…");
                        var result = await api.PreviewRetargetAsync(source, resolved, rig,
                            () => _previewTargets.InstantiateAsync(resolved, ct), ct);
                        if (!Current()) { result.Root.Free(); return; }
                        RetargetPreviewCache.Preview preview;
                        try { preview = _characterCache.Store(cacheKey, new(result, resolved, readiness, source.FrameRate)); }
                        catch { result.Root.Free(); throw; }
                        AttachCharacter(preview, source);
                    }
                    catch (NotSupportedException error) when (source.HasSkeletonWithoutMesh)
                    {
                        if (!Current()) return;
                        viewport.AttachSource(source, notify: false, switchingAsset: true);
                        tab.RetargetMessage = error.Message;
                        tab.CharacterStatus = "已显示原始骨架 · " + error.Message;
                    }
                }
                else viewport.AttachSource(source, notify: false, switchingAsset: true);
            }

            void AttachCharacter(RetargetPreviewCache.Preview preview, PreviewSource? source)
            {
                var result = preview.Result;
                try
                {
                    if (source != null) viewport.AttachRetarget(result, preview.Options, path, 1, source,
                        newSource: true, preparedReadiness: preview.Readiness);
                    else viewport.AttachCachedRetarget(preview, path);
                }
                catch { if (result.Root.GetParent() == null) result.Root.Free(); throw; }
                tab.CharacterStatus = "Ready · " + target!.Name;
            }

            tab.DisplayedAsset = asset;
            tab.DisplayedLibrary = library;
            var type = viewport.Clips.Length > 0 ? "Animation" : "Model";
            if (type == "Animation") RememberPreviewAsset(library, asset);
            bool typeChanged = type != asset.Type;
            if (typeChanged) { api.SetAssetType(asset.Id, type); asset.Type = type; }
            if (resumePlayback && !viewport.Playing) viewport.TogglePlay();
            stage.SetLoadStatus("");
            stage.SyncTransport();
            if (tab == _activePreviewTab)
            {
                SyncFocusedPreviewSelection();
                SetStatus("预览已就绪 · " + asset.Name);
            }
            if (typeChanged) Refresh();
        }
        catch (OperationCanceledException)
        {
            if (!_closing && tab.Load == request)
                stage.SetLoadStatus("预览加载已取消。", Retry);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!Current()) return;
            tab.RetargetMessage = error.Message;
            tab.CharacterStatus = "待修复 · " + error.Message;
            stage.SetLoadStatus((viewport.Model == null ? "无法加载预览。" : "切换失败，保留当前预览。") + error.Message, Retry);
            if (tab == _activePreviewTab)
            {
                SyncFocusedPreviewSelection();
                SetStatus("无法预览 " + asset.Name + " · " + error.Message);
            }
        }
        finally
        {
            if (tab.Load == request) tab.Load = null;
            _previewLoads.Remove(request);
            request.Dispose();
        }

        void Retry() => _ = StartPreviewLoad(tab, library, asset);
    }

    private void SetPreviewTabLabel(PreviewTab tab)
    {
        string title = tab.Asset?.Name ?? "";
        string tooltip = tab.Asset is { } asset && tab.Library is { } library
            ? $"{library.Identity.Name} · {asset.VirtualPath}" : "";
        bool duplicate = _previewTabsByStage.Values.Any(other => other != tab && other.Asset?.Name == title);
        _previewTabs.SetTabLabel(tab.Stage, duplicate && tab.Library != null ? $"{title} · {tab.Library.Identity.Name}" : title, tooltip);
    }

    private void SyncFocusedPreviewSelection()
    {
        if (_inspector == null || _retargetEnabled == null || _retargetInfo == null) return;
        var tab = _activePreviewTab;
        _selected = tab.Asset;
        _selectedLibrary = tab.Library;
        _selectedKey = _selected != null ? AssetKey(_selectedLibrary!, _selected.Id) : null;
        _previewAssetId = tab.DisplayedAsset != null ? AssetKey(tab.DisplayedLibrary!, tab.DisplayedAsset.Id) : "";
        bool loaded = _selectedKey != null && _selectedKey == _previewAssetId;
        _title.Text = _selected?.Name ?? "双击资产以打开预览";
        _stage.TitleLabel.Text = tab.DisplayedAsset?.Name ?? _title.Text;
        _tags.Text = _selected?.Tags ?? "";
        _exportSummary.Text = loaded ? "导出资产：" + _selected!.Name : "导出当前选中的资产。";
        _detail.Text = loaded
            ? $"{_selected!.SourceFormat} · {_preview.MeshCount} 网格 · {_preview.BoneCount} 骨骼 · {_preview.Clips.Length} 动画"
            : "格式、骨架与动画信息显示在这里。";
        _inspectorStamp = "";
        LoadAssetRetargetConfiguration();
        if (loaded && _preview.AppliedRetarget is { } options)
        {
            if (_characterEntries.Any(entry => entry.Compatible && entry.Id == (options.Character?.Id ?? options.TargetRig)))
                SetRetargetOptions(options);
            _retargetView.Select(_preview.RetargetViewMode);
            SyncRetargetSummary();
        }
        if (tab.RetargetMessage.Length > 0) _retargetInfo.Text = tab.RetargetMessage;
        SyncPreviewCharacterControl();
        if (loaded && _selectedLibrary?.Api == _api) _commands?.TrackSelection(_selected!.Id);
        else _commands?.ForgetSelection();
        UpdateTransport();
    }

    private void SyncConsoleSelection(AssetItem asset)
    {
        var tab = _activePreviewTab;
        tab.Load?.Cancellation.Cancel();
        tab.Load = null;
        tab.Library = tab.DisplayedLibrary = _libraries.Active;
        tab.Asset = tab.DisplayedAsset = asset;
        _assetSelection.Select(AssetKey(tab.Library!, asset.Id));
        tab.RetargetMessage = "";
        tab.CharacterStatus = "";
        tab.Stage.SetLoadStatus("");
        SetPreviewTabLabel(tab);
        SyncFocusedPreviewSelection();
        if (_previewCharacterId.Length > 0 && _preview.AppliedRetarget == null) PendingRetargetPreview = ApplyCharacterForSource();
        Refresh();
    }

    private Preview3DViewport PrepareConsoleRetargetPreview(AssetItem asset)
    {
        if (FindPreviewTab(_libraries.Active!, asset.Id) is { } tab) _previewTabs.Activate(tab.Stage);
        EndAnimationComparison();
        _activePreviewTab.Load?.Cancellation.Cancel();
        _retargetCancellation?.Cancel();
        StopRequest(ref _retargetSourceRequest);
        return _preview;
    }

    private void ClosePreviewTab(PreviewTab tab)
    {
        tab.Load?.Cancellation.Cancel();
        tab.Load = null;
        if (tab == _activePreviewTab)
        {
            EndAnimationComparison();
            _retargetCancellation?.Cancel();
            StopRequest(ref _retargetSourceRequest);
        }
        tab.Stage.Viewport.Clear();
        if (_previewTabs.Stages.Count > 1)
        {
            tab.Stage.Viewport.ReleaseWeaponProfile();
            _previewTabsByStage.Remove(tab.Stage);
            _previewTabs.RemoveStage(tab.Stage);
        }
        else
        {
            tab.Asset = tab.DisplayedAsset = null;
            tab.Library = tab.DisplayedLibrary = null;
            tab.CharacterStatus = tab.RetargetMessage = "";
            tab.Stage.SetLoadStatus("");
            tab.Stage.ResetPreviewSettings();
            SyncFocusedPreviewSelection();
        }
        foreach (var remaining in _previewTabsByStage.Values) SetPreviewTabLabel(remaining);
    }

    private void RefreshPreviewTabs()
    {
        foreach (var tab in _previewTabsByStage.Values.ToArray())
        {
            if (tab.Asset is not { } asset || tab.Library is not { } library) continue;
            if (_libraries.FindOpenLibrary(library.Identity.Id) != library ||
                library.Api.Database.Find(asset.Id) is not { Trashed: false } current)
            {
                ClosePreviewTab(tab);
                continue;
            }
            tab.Asset = current;
            if (tab.DisplayedLibrary == library && tab.DisplayedAsset?.Id == current.Id)
                tab.DisplayedAsset = current;
            SetPreviewTabLabel(tab);
        }
    }

    private Task CancelPreviewLoads(LibrarySession? library = null)
    {
        var pending = _previewLoads.Where(load => library == null || load.Library == library).ToArray();
        foreach (var load in pending) load.Cancellation.Cancel();
        return Task.WhenAll(pending.Select(load => load.Task));
    }
}
