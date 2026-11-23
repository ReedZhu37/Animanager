using Animanager.Core.Preview;
using Animanager.UI.Components;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private void BuildWeaponProfiles(VBoxContainer overview, VBoxContainer settings)
    {
        var library = new WeaponProfileLibrary(Path.Combine(Path.GetDirectoryName(_preferencesPath)!, "Weapons"));
        WeaponProfile active = WeaponProfile.Reference;
        WeaponProfile[] entries = [];
        var overviewHeader = Row(overview);
        var overviewLabel = Label("武器 Profile", true, 13);
        overviewLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        overviewHeader.AddChild(overviewLabel);
        var overviewChoice = new OptionButton { Name = "OverviewWeaponProfile", FitToLongestItem = false };
        overview.AddChild(overviewChoice);
        var enabled = new CheckButton { Name = "OverviewWeaponEnabled", Text = "启用武器预览" };
        overview.AddChild(enabled);
        var heatmap = new CheckButton
        {
            Name = "WeaponTrailHeatmap", Text = "速度热力模式",
            TooltipText = "按武器尖端速度着色。蓝色慢，黄色居中，红色快。当前对比共用同一色标。"
        };
        heatmap.Toggled += _preview.SetWeaponHeatmap;
        overview.AddChild(heatmap);
        var coverage = Wrapped("");
        coverage.Name = "OverviewWeaponStatus";
        overview.AddChild(coverage);

        var dialog = new AcceptDialog { Name = "WeaponProfiles", Title = "武器 Profile", OkButtonText = "关闭" };
        AddChild(dialog);
        var form = new VBoxContainer();
        dialog.AddChild(form);
        var install = Dialog(FileDialog.FileModeEnum.OpenFile, "添加预览武器模型");
        install.Name = "InstallWeaponFile";
        install.Filters = ["*.glb, *.gltf, *.fbx ; 武器模型"];

        var selection = Row(form);
        var profiles = new OptionButton { Name = "WeaponProfileChoice", FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        selection.AddChild(profiles);
        var name = Input("Profile 名称"); name.Name = "WeaponProfileName"; form.AddChild(name);
        var weaponList = new ItemList { Name = "WeaponItems", CustomMinimumSize = new(0, 78) };
        form.AddChild(weaponList);
        var weaponsActions = new HFlowContainer();
        form.AddChild(weaponsActions);

        var pages = new TabContainer { Name = "WeaponProfilePages", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 230) };
        form.AddChild(pages);
        VBoxContainer Page(string title)
        {
            var scroll = new ScrollContainer { Name = title, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            pages.AddChild(scroll);
            return Column(scroll);
        }
        var weaponFields = Page("挂接与拖尾");
        var weaponName = Input("武器名称"); weaponName.Name = "WeaponItemName"; weaponFields.AddChild(weaponName);
        var socket = new OptionButton { Name = "WeaponSocket", FitToLongestItem = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        socket.GetPopup().AllowSearch = true;
        weaponFields.AddChild(Label("挂接点"));
        weaponFields.AddChild(socket);

        var fields = new GridContainer { Columns = 4 };
        weaponFields.AddChild(fields);
        foreach (string axis in new[] { "", "X", "Y", "Z" }) fields.AddChild(Label(axis, true, 12));
        var mountPosition = VectorField(fields, "挂接位置 (m)", "WeaponMountPosition", -100, 100, .005);
        var mountRotation = VectorField(fields, "挂接旋转 (°)", "WeaponMountRotation", -360, 360, 1);
        var boxPosition = VectorField(fields, "盒子中心 (m)", "WeaponBoxCenter", -100, 100, .005);
        var boxSize = VectorField(fields, "盒子尺寸 (m)", "WeaponBoxSize", .001, 100, .001);
        var boxRotation = VectorField(fields, "盒子旋转 (°)", "WeaponBoxRotation", -360, 360, 1);
        var durationRow = Row(weaponFields);
        durationRow.AddChild(Label("拖尾时长 (秒)", true, 13));
        var trailSeconds = new SpinBox { Name = "WeaponTrailSeconds", MinValue = PreviewWeapon.MinTrailSeconds,
            MaxValue = PreviewWeapon.MaxTrailSeconds, Step = .01, CustomMinimumSize = new(98, 30) };
        durationRow.AddChild(trailSeconds);

        var meshFields = Page("网格预处理");
        meshFields.AddChild(Wrapped("在武器局部坐标中调整网格。拖尾盒可在「挂接与拖尾」中单独调整。"));
        var meshGrid = new GridContainer { Columns = 4 };
        meshFields.AddChild(meshGrid);
        foreach (string axis in new[] { "", "X", "Y", "Z" }) meshGrid.AddChild(Label(axis, true, 12));
        var meshPosition = VectorField(meshGrid, "网格偏移 (m)", "WeaponMeshPosition", -100, 100, .001);
        var meshRotation = VectorField(meshGrid, "网格旋转 (°)", "WeaponMeshRotation", -360, 360, 1);
        var meshActions = new HFlowContainer();
        meshFields.AddChild(meshActions);
        meshActions.AddChild(Label("统一缩放", true, 13));
        var meshScale = new SpinBox { Name = "WeaponMeshScale", MinValue = .001, MaxValue = 1000, Step = .001, Value = 1, CustomMinimumSize = new(98, 30) };
        meshActions.AddChild(meshScale);
        var meshPreview = new Preview3DViewport
        {
            Name = "WeaponMeshPreview", SizeFlagsVertical = SizeFlags.ExpandFill
        };
        meshFields.AddChild(meshPreview);
        meshPreview.CustomMinimumSize = new(0, 250);
        meshPreview.SetClay(false);
        var actions = Row(form);
        var showBox = new CheckButton { Name = "ShowWeaponTrailBox", Text = "显示拖尾盒" };
        showBox.Toggled += _preview.SetWeaponBoxVisible;
        actions.AddChild(showBox);
        var status = Wrapped("");
        form.AddChild(status);

        WeaponProfile editing = active;
        List<PreviewWeapon> draft = [];
        var sockets = new List<WeaponSocket?>();
        int weaponIndex = -1;
        Node3D? meshModel = null;

        SpinBox[] VectorField(GridContainer grid, string label, string fieldName, double min, double max, double step)
        {
            grid.AddChild(Label(label, true, 13));
            return new[] { "X", "Y", "Z" }.Select(axis =>
            {
                var input = new SpinBox { Name = fieldName + axis, MinValue = min, MaxValue = max, Step = step,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new(78, 30) };
                grid.AddChild(input);
                return input;
            }).ToArray();
        }
        static WeaponVector Value(SpinBox[] row) => new((float)row[0].Value, (float)row[1].Value, (float)row[2].Value);
        static void Set(SpinBox[] row, WeaponVector value)
        {
            row[0].SetValueNoSignal(value.X); row[1].SetValueNoSignal(value.Y); row[2].SetValueNoSignal(value.Z);
        }
        WeaponMeshSettings ReadMesh() => new()
        {
            Position = Value(meshPosition), Rotation = Value(meshRotation), Scale = (float)meshScale.Value
        };
        void SetMesh(WeaponMeshSettings mesh)
        {
            Set(meshPosition, mesh.Position);
            Set(meshRotation, mesh.Rotation);
            meshScale.SetValueNoSignal(mesh.Scale);
        }
        void UpdateMesh()
        {
            if (meshModel == null) return;
            meshModel.Transform = ReadMesh().Transform;
            meshPreview.Redraw();
        }
        void ShowMesh()
        {
            meshModel = null;
            if (meshPreview.Model != null) meshPreview.Clear();
            if (!dialog.Visible || pages.CurrentTab != 1 || weaponIndex < 0) return;
            Handle(() =>
            {
                var weapon = draft[weaponIndex];
                meshModel = Preview3DViewport.CreateWeaponMesh(weapon with { Mesh = ReadMesh() }, library.ModelPath(weapon));
                var root = new Node3D();
                root.AddChild(meshModel);
                meshPreview.AttachModel(root);
            });
        }
        foreach (var field in meshPosition.Concat(meshRotation).Append(meshScale)) field.ValueChanged += _ => UpdateMesh();
        meshActions.AddChild(Button("重置网格", () =>
        {
            SetMesh(WeaponMeshSettings.Default);
            UpdateMesh();
            meshPreview.FrameModel();
        }));
        meshActions.AddChild(Button("聚焦网格", meshPreview.FrameModel));
        pages.TabChanged += _ => ShowMesh();
        dialog.VisibilityChanged += () => { if (!dialog.Visible) ShowMesh(); };

        void LoadWeapon(int index)
        {
            weaponIndex = index;
            pages.Visible = index >= 0;
            if (index < 0) { ShowMesh(); return; }
            var weapon = draft[index];
            weaponName.Text = weapon.Name;
            socket.Clear();
            sockets.Clear();
            socket.AddItem("自动 · 源武器轨道或右手掌"); sockets.Add(null);
            foreach (var choice in _preview.WeaponSockets())
            {
                socket.AddItem(choice.Label); sockets.Add(choice.Socket);
            }
            int mount = sockets.IndexOf(weapon.Socket);
            if (mount < 0) mount = sockets.IndexOf(weapon.Socket?.ResolveLegacyTrack());
            if (mount < 0 && weapon.Socket is { } saved)
            {
                var matches = Enumerable.Range(1, sockets.Count - 1).Where(i => saved.Bone != null && sockets[i]?.Bone == saved.Bone).ToArray();
                if (matches.Length == 1) mount = matches[0];
                else
                {
                    mount = sockets.Count;
                    string unavailable = saved.ResolveLegacyTrack() == WeaponSocket.PrimaryTrack ? "独立武器轨道" : saved.Bone ?? saved.NodePath;
                    socket.AddItem($"{unavailable} · 当前视图不可用"); sockets.Add(saved);
                }
            }
            socket.Select(Math.Max(0, mount));
            Set(mountPosition, weapon.Position); Set(mountRotation, weapon.Rotation);
            Set(boxPosition, weapon.Trail.Center); Set(boxSize, weapon.Trail.Size); Set(boxRotation, weapon.Trail.Rotation);
            trailSeconds.SetValueNoSignal(weapon.TrailSeconds);
            SetMesh(weapon.Mesh);
            ShowMesh();
        }

        void StoreWeapon()
        {
            if (weaponIndex < 0) return;
            draft[weaponIndex] = draft[weaponIndex] with
            {
                Name = weaponName.Text.Trim(), Socket = sockets[socket.Selected],
                Position = Value(mountPosition), Rotation = Value(mountRotation),
                Mesh = ReadMesh(),
                Trail = new(Value(boxPosition), Value(boxSize), Value(boxRotation)), TrailSeconds = trailSeconds.Value
            };
            weaponList.SetItemText(weaponIndex, draft[weaponIndex].Name);
        }

        void RefreshWeapons(int index)
        {
            weaponList.Clear();
            foreach (var weapon in draft) weaponList.AddItem(weapon.Name);
            if (index >= 0) weaponList.Select(index);
            LoadWeapon(index);
        }
        void Edit(WeaponProfile profile)
        {
            editing = profile;
            draft = profile.Weapons.ToList();
            name.Text = profile.Name;
            status.Text = "";
            RefreshWeapons(draft.Count == 0 ? -1 : 0);
        }
        void AddWeapon(PreviewWeapon weapon)
        {
            StoreWeapon();
            draft.Add(weapon);
            RefreshWeapons(draft.Count - 1);
        }
        void MoveWeapon(int delta)
        {
            int destination = weaponIndex + delta;
            if (weaponIndex < 0 || destination < 0 || destination >= draft.Count) return;
            StoreWeapon();
            (draft[weaponIndex], draft[destination]) = (draft[destination], draft[weaponIndex]);
            RefreshWeapons(destination);
        }
        void UpdateOverview()
        {
            enabled.SetPressedNoSignal(_preview.WeaponPreviewEnabled);
            heatmap.SetPressedNoSignal(_preview.WeaponHeatmapEnabled);
            coverage.Text = _preview.CanPreviewWeapons && _preview.AvailableWeaponCount < active.Weapons.Length
                ? $"{_preview.AvailableWeaponCount}/{active.Weapons.Length} 件可挂接 · 请检查挂点" : "";
            coverage.Visible = coverage.Text.Length > 0;
        }
        void ReloadOverview()
        {
            entries = library.List();
            overviewChoice.Clear();
            foreach (var profile in entries) overviewChoice.AddItem($"{profile.Name} · {profile.Weapons.Length} 件");
            overviewChoice.Select(Array.FindIndex(entries, profile => profile.Id == active.Id));
            UpdateOverview();
        }
        void Apply(WeaponProfile profile, bool show)
        {
            _preview.ApplyWeaponProfile(profile, library.ModelPath, show);
            library.Use(profile);
            active = profile;
            ReloadOverview();
        }
        void Handle(Action action)
        {
            try { action(); }
            catch (Exception error) { status.Text = error.Message; }
        }
        void OpenEditor()
        {
            _stage.SettingsDialog.Hide();
            pages.CurrentTab = 0;
            Handle(() =>
            {
                profiles.Clear();
                foreach (var profile in entries) profiles.AddItem(profile.Name);
                profiles.Select(Array.FindIndex(entries, profile => profile.Id == active.Id));
                Edit(active);
            });
            PopupFit(dialog, new(660, 720));
        }

        _preview.WeaponPreviewChanged += UpdateOverview;
        enabled.Toggled += _preview.SetWeaponPreviewEnabled;
        overviewChoice.ItemSelected += index =>
        {
            try { Apply(entries[(int)index], show: false); }
            catch (Exception error)
            {
                overviewChoice.Select(Array.FindIndex(entries, profile => profile.Id == active.Id));
                ShowError("无法切换武器 Profile", error.Message);
            }
        };
        var edit = Button("编辑…", OpenEditor);
        edit.Name = "EditOverviewWeaponProfile";
        overviewHeader.AddChild(edit);
        var shortcut = Button("武器 Profile…", OpenEditor);
        shortcut.Name = "OpenWeaponProfiles";
        settings.AddChild(shortcut);

        selection.AddChild(Button("新建 Profile", () =>
        {
            profiles.Select(-1);
            Edit(new WeaponProfile(Guid.NewGuid().ToString("N"), "新武器 Profile"));
        }));
        profiles.ItemSelected += index => Edit(entries[(int)index]);
        weaponList.ItemSelected += index => { StoreWeapon(); LoadWeapon((int)index); };
        weaponsActions.AddChild(Button("添加参考刀", () => AddWeapon(new("参考刀")
        {
            Socket = draft.Count == 1 ? WeaponSocket.LeftPalm : null
        })));
        weaponsActions.AddChild(Button("添加模型…", () => install.PopupCenteredRatio(.7f)));
        weaponsActions.AddChild(Button("上移", () => MoveWeapon(-1)));
        weaponsActions.AddChild(Button("下移", () => MoveWeapon(1)));
        weaponsActions.AddChild(Button("移除", () =>
        {
            if (weaponIndex < 0) return;
            int removed = weaponIndex;
            draft.RemoveAt(removed);
            RefreshWeapons(Math.Min(removed, draft.Count - 1));
        }));
        install.FileSelected += path => Handle(() => AddWeapon(library.Install(path)));
        var save = Button("保存并预览", () => Handle(() =>
        {
            StoreWeapon();
            Apply(editing with { Name = name.Text.Trim(), Weapons = draft.ToArray() }, show: true);
            dialog.Hide();
        }), primary: true);
        save.Name = "SaveWeaponProfile";
        actions.AddChild(save);

        try
        {
            active = library.Active();
            ReloadOverview();
            _preview.ApplyWeaponProfile(active, library.ModelPath, enable: false);
        }
        catch (Exception error) { GD.PushWarning("无法恢复武器 Profile：" + error.Message); }
    }
}
