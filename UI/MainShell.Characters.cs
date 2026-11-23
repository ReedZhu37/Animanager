using Animanager.Core.Retarget;
using Animanager.UI.Components;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private CharacterLibrary _characters = null!;
    private CharacterEntry[] _characterEntries = [];
    private string _previewCharacterId = "ue5-mannequin";
    private OptionButton _characterChoice = null!;
    private Label _characterStatus = null!, _characterDetails = null!;
    private AcceptDialog _charactersDialog = null!;
    private FileDialog _characterFiles = null!;
    private ItemList _characterRows = null!;
    private LineEdit _characterName = null!;
    private Button _useCharacter = null!, _renameCharacter = null!, _archiveCharacter = null!;
    private Preview3DViewport _characterModel = null!;
    private bool _importingCharacter;
    private AssetThumbnailRenderer _characterThumbnails = null!;
    private Task _characterThumbnailTask = Task.CompletedTask;
    private int _characterListVersion;
    private readonly RetargetPreviewCache _characterCache = new();
    internal CharacterLibrary Characters => _characters;
    internal string PreviewCharacterId => _previewCharacterId;
    internal int CharacterCacheHits => _characterCache.Hits;

    private void InitializeCharacterLibrary()
    {
        bool isolated = OS.GetCmdlineUserArgs().Any(a => a.Contains("-test") || a.StartsWith("--retarget-probe=") || a.StartsWith("--retarget-demo="));
        _characters = new(isolated ? Path.Combine(Path.GetDirectoryName(_preferencesPath)!, "characters") : CharacterLibrary.DefaultRoot());
        if (isolated) _previewCharacterId = "";
        _characterEntries = _characters.List();
        try { var id = _characters.SelectedId; if (_characters.HasSelection && id.Length == 0 || _characterEntries.Any(c => c.Id == id && c.Compatible)) _previewCharacterId = id; }
        catch (Exception e) { GD.PushWarning("预览角色选择无法恢复：" + e.Message); }
    }
    private void BuildCharacterSelector(VBoxContainer parent)
    {
        var row = Row(parent); row.AddChild(Label("预览角色", true, 12));
        _characterChoice = new OptionButton { Name = "PreviewCharacter", FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_characterChoice); row.AddChild(Button("角色库…", ShowCharacters));
        _characterChoice.ItemSelected += index => { PendingRetargetPreview = SelectPreviewCharacterAsync(index == 0 ? "" : _characterEntries[(int)index - 1].Id); };
        _characterStatus = Label("", true, 12); _characterStatus.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _characterStatus.Name = "CharacterPreviewStatus"; parent.AddChild(_characterStatus);
        RefreshCharacterChoices();
    }
    private void RefreshCharacterChoices()
    {
        _characterEntries = _characters.List();
        if (_characterChoice != null)
        {
            _characterChoice.Clear(); _characterChoice.AddItem("原始角色");
            foreach (var entry in _characterEntries)
            {
                _characterChoice.AddItem(entry.Name + (entry.Compatible ? " · Ready" : " · 待修复"));
                int i = _characterChoice.ItemCount - 1; _characterChoice.SetItemDisabled(i, !entry.Compatible);
                _characterChoice.SetItemTooltip(i, entry.Compatible ? $"{entry.Bones} 骨骼 · {entry.Family}" : entry.Reason);
            }
            _characterChoice.Select(Math.Max(0, Array.FindIndex(_characterEntries, c => c.Id == _previewCharacterId) + 1));
            _characterStatus.Text = _previewCharacterId.Length == 0 ? "使用动画自带的角色" : "选择动画后自动应用 · " + _characterEntries.First(c => c.Id == _previewCharacterId).Name;
        }
        if (_retargetTarget != null)
        {
            string selectedId = _retargetTarget.ItemCount > 0 ? _retargetTarget.GetSelectedMetadata().AsString() : "ue5-mannequin";
            _retargetTarget.Clear();
            foreach (var entry in _characterEntries.Where(c => c.Compatible))
            {
                int index = _retargetTarget.ItemCount; _retargetTarget.AddItem($"{entry.Name} · {entry.Bones} 骨骼");
                _retargetTarget.SetItemMetadata(index, entry.Id);
                if (entry.Id == selectedId) _retargetTarget.Select(index);
            }
        }
    }
    private CharacterEntry RetargetCharacter
    {
        get
        {
            string selectedId = _retargetTarget.GetSelectedMetadata().AsString();
            return _characterEntries.First(c => c.Id == selectedId);
        }
    }
    private void SetRetargetCharacter(string id)
    {
        for (int i = 0; i < _retargetTarget.ItemCount; i++)
            if (_retargetTarget.GetItemMetadata(i).AsString() == id) { _retargetTarget.Select(i); return; }
        throw new IOException("此重定向配置的角色已移出角色库。");
    }
    internal async Task SelectPreviewCharacterAsync(string id)
    {
        try
        {
            _retargetCancellation?.Cancel();
            _characters.Select(id); _previewCharacterId = id; RefreshCharacterChoices();
            if (id.Length > 0) { SetRetargetCharacter(id); _retargetTargetHash = ""; }
            if (_selected == null || _selectedLibrary == null) return;
            if (_previewAssetId != _selectedKey) { await PreviewAssetAsync(_selectedLibrary, _selected); return; }
            if (id.Length == 0) { RestoreRetargetSource(); return; }
            await ApplyCharacterForSource();
        }
        catch (Exception e) { _characterStatus.Text = e.Message; }
    }
    private Task ApplyCharacterForSource()
    {
        if (_previewCharacterId.Length == 0) return Task.CompletedTask;
        SetRetargetCharacter(_previewCharacterId);
        _retargetTargetHash = ""; _retargetSourceHash = ""; _retargetSourceRig = "";
        _retargetEnabled.ButtonPressed = false;
        if (_preview.Clips.Length == 0)
        {
            _characterStatus.Text = "当前资产没有动画；正在显示原始模型。"; return Task.CompletedTask;
        }
        _retargetView.Select(1);
        return ApplyRetargetPreview();
    }
    private void BuildCharacterLibraryDialog()
    {
        _charactersDialog = new AcceptDialog { Name = "CharacterLibrary", Title = "角色库 · 所有动画库共用", OkButtonText = "关闭" }; AddChild(_charactersDialog);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _charactersDialog.AddChild(scroll);
        var col = Column(scroll);
        col.AddChild(Wrapped("导入带骨架与蒙皮的 FBX / GLB / glTF。通过 Ready-v1 检查后自动用于动画预览；支持 UE、Mixamo 等人体骨名。缺少映射或蒙皮损坏会列出修复原因。"));
        var row = Row(col); row.SizeFlagsVertical = SizeFlags.ExpandFill;
        _characterRows = new ItemList { Name = "CharacterRows", CustomMinimumSize = new(300, 270), FixedIconSize = new(80, 64), TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis, SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(_characterRows);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; row.AddChild(right);
        _characterModel = new Preview3DViewport { CustomMinimumSize = new(280, 270) }; right.AddChild(_characterModel);
        _characterDetails = Wrapped(""); _characterDetails.MaxLinesVisible = 4; right.AddChild(_characterDetails);
        _characterName = Input("角色名称"); _characterName.Name = "CharacterName"; col.AddChild(_characterName);
        var actions = new HFlowContainer(); col.AddChild(actions);
        var import = Button("导入角色…", () => _characterFiles.PopupCenteredRatio(.7f)); import.Name = "ImportCharacter"; actions.AddChild(import);
        _useCharacter = Button("用于动画预览", () =>
        {
            var entry = SelectedCharacterRow(); _charactersDialog.Hide(); PendingRetargetPreview = SelectPreviewCharacterAsync(entry.Id);
        }, true); _useCharacter.Name = "UseCharacter"; actions.AddChild(_useCharacter);
        _renameCharacter = Button("重命名", () => CharacterAction(() => { var entry = _characters.Rename(SelectedCharacterRow().Id, _characterName.Text); RefreshCharactersDialog(entry.Id); })); actions.AddChild(_renameCharacter);
        _archiveCharacter = Button("移出角色库", () => CharacterAction(() =>
        {
            string id = SelectedCharacterRow().Id; _characters.Archive(id);
            if (_retargetTarget.GetSelectedMetadata().AsString() == id) _retargetEnabled.ButtonPressed = false;
            if (_previewCharacterId == id) PendingRetargetPreview = SelectPreviewCharacterAsync("");
            RefreshCharactersDialog();
        })); _archiveCharacter.TooltipText = "隐藏此角色；保留模型文件，已有导出任务仍可使用。重新导入可恢复。"; actions.AddChild(_archiveCharacter);
        actions.AddChild(Button("打开库目录", () => { Directory.CreateDirectory(_characters.Root); OS.ShellOpen(_characters.Root); }));
        _characterRows.ItemSelected += _ => ShowCharacterDetails();
        _characterRows.ItemActivated += _ => { if (!_useCharacter.Disabled) _useCharacter.EmitSignal(BaseButton.SignalName.Pressed); };
        _characterFiles = Dialog(FileDialog.FileModeEnum.OpenFiles, "导入预览角色"); _characterFiles.Filters = ["*.glb, *.gltf, *.fbx ; 角色模型"];
        _characterFiles.FilesSelected += paths => _ = ImportCharacters(paths);
        _charactersDialog.FilesDropped += paths => _ = ImportCharacters(paths);
        _characterThumbnails = new AssetThumbnailRenderer(); AddChild(_characterThumbnails);
    }
    private CharacterEntry SelectedCharacterRow()
    {
        int index = _characterRows.GetSelectedItems().FirstOrDefault(-1);
        return index < 0 ? throw new InvalidOperationException("请选择角色。") : _characterEntries[index];
    }
    private void CharacterAction(Action action)
    {
        try { action(); } catch (Exception e) { _characterDetails.Text = e.Message; }
    }
    private void ShowCharacters()
    {
        RefreshCharactersDialog(_previewCharacterId); PopupFit(_charactersDialog, new(840, 590));
        _characterModel.FrameModel();
    }
    private void RefreshCharactersDialog(string selected = "")
    {
        RefreshCharacterChoices(); _characterRows.Clear();
        foreach (var entry in _characterEntries)
            _characterRows.AddItem(entry.Name + " · " + (entry.Compatible ? $"Ready · {entry.Bones} 骨骼" : "待修复"));
        _characterRows.Select(Math.Max(0, Array.FindIndex(_characterEntries, c => c.Id == selected)));
        ShowCharacterDetails();
        int version = ++_characterListVersion;
        _characterThumbnailTask = LoadCharacterThumbnails(_characterThumbnailTask, version, _characterEntries.ToArray());
    }
    private async Task LoadCharacterThumbnails(Task previous, int version, CharacterEntry[] entries)
    {
        try
        {
            await previous;
            if (DisplayServer.GetName() == "headless") return;
            for (int i = 0; i < entries.Length; i++)
            {
                if (_closing || version != _characterListVersion) return;
                var entry = entries[i]; string path = Path.Combine(_characters.Root, "cache", entry.Sha256 + ".png");
                try
                {
                    if (!File.Exists(path))
                    {
                        using var rendered = await _characterThumbnails.RenderAsync(entry.ModelPath, () => !_closing && version == _characterListVersion);
                        if (rendered == null) return;
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!); rendered.SavePng(path);
                    }
                    if (_closing || version != _characterListVersion) return;
                    using var loaded = Image.LoadFromFile(path); _characterRows.SetItemIcon(i, ImageTexture.CreateFromImage(loaded));
                }
                catch (Exception e) { GD.PushWarning("角色缩略图：" + e.Message); }
            }
        }
        catch (Exception e) { GD.PushWarning("角色缩略图：" + e.Message); }
    }
    private void ShowCharacterDetails() => CharacterAction(() =>
    {
        var entry = SelectedCharacterRow(); _characterName.Text = entry.Name;
        _useCharacter.Disabled = !entry.Compatible; _renameCharacter.Disabled = _archiveCharacter.Disabled = entry.Builtin;
        _characterDetails.Text = $"{entry.Bones} 骨骼 · {entry.Meshes} 网格\n" + (entry.Compatible ? "Ready · 人体映射 / 层级 / 蒙皮检查通过；动作效果待预览确认" : "待修复 · " + entry.Reason);
        _characterDetails.TooltipText = _characterDetails.Text;
        _characterModel.Clear(); _characterModel.AttachModel(_characters.Load(entry));
    });
    internal async Task ImportCharacters(string[] paths, bool showDialog = true)
    {
        if (_importingCharacter) return;
        _importingCharacter = true; string selected = ""; var errors = new List<string>();
        try
        {
            foreach (string path in paths)
            {
                SetStatus("正在导入角色 · " + Path.GetFileName(path));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                if (_closing) return;
                try { selected = _characters.Import(path).Id; } catch (Exception e) { errors.Add(Path.GetFileName(path) + "：" + e.Message); }
            }
            RefreshCharactersDialog(selected);
            if (showDialog) PopupFit(_charactersDialog, new(840, 590));
            if (selected.Length > 0 && _characters.Find(selected).Compatible)
            { PendingRetargetPreview = SelectPreviewCharacterAsync(selected); await PendingRetargetPreview; }
            if (errors.Count > 0) _characterDetails.Text = string.Join("\n", errors);
            SetStatus(errors.Count == 0 ? "角色已入库 · Ready 角色已自动用于动画预览" : "部分角色未导入，请查看角色库提示。");
        }
        finally { _importingCharacter = false; }
    }
}
