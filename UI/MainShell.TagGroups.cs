using Animanager.Core;
using Animanager.Core.Storage;
using Godot;
using System.Text.Json;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private static readonly StringComparer TagComparer = StringComparer.OrdinalIgnoreCase;
    internal AppTagCatalog AppTags { get; private set; } = null!;
    private AppTagSettings _appTagSettings = AppTagSettings.Empty;
    private (string Category, string? Library, string LibraryRevisions, int BrowseRevision, string SettingsRevision,
        string CollectionRevision)? _tagCatalog;
    private AssetTagGroup[] _tagGroups = [];
    private (string GroupId, string Group, (string Name, int Count)[] Tags)[] _tagSections = [];
    private MenuButton _tagGroupMenu = null!;
    private AcceptDialog _tagGroupDialog = null!;
    private LineEdit _tagGroupName = null!, _tagGroupSearch = null!;
    private Tree _tagGroupMembers = null!;
    private Label _tagGroupCount = null!, _tagGroupError = null!;
    private Button _deleteTagGroup = null!;
    private AssetTagGroup? _editingTagGroup;
    private (string Name, string OtherGroups)[] _tagGroupOptions = [];
    private readonly HashSet<string> _tagGroupDraft = new(TagComparer);

    private async Task InitializeAppTagsAsync(string settingsDirectory)
    {
        AppTags = new(Path.Combine(settingsDirectory, "tags.json"));
        try { _appTagSettings = await Task.Run(() => AppTags.ImportRegisteredLibraries(_libraries.Entries())); }
        catch (Exception error) when (IsAppSettingsError(error))
        {
            GD.PushWarning("无法迁移应用标签：" + error.Message);
        }
    }

    private static bool IsAppSettingsError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;

    private void RefreshTagCatalog()
    {
        bool trash = _category == "Trash";
        var libraries = LoadedLibraries();
        var collection = _collections.FirstOrDefault(collection => collection.Id == _category);
        var scoped = ScopedLibrary;
        bool canSave = true;
        try { _appTagSettings = AppTags.Read(); }
        catch (Exception error) when (IsAppSettingsError(error))
        {
            GD.PushWarning("无法读取应用标签：" + error.Message);
            canSave = false;
        }
        var libraryRevisions = string.Join("/", libraries.Select(library => library.Api.Database.CatalogRevision));
        var catalog = (Category: _category, Library: scoped?.Identity.Id, libraryRevisions,
            _browseLibraryRevision, _appTagSettings.Revision, _collectionRevision);
        if (_tagCatalog == catalog) return;

        var snapshots = libraries.ToDictionary(library => library, library => library.Api.Database.Snapshot());
        if (canSave)
        {
            try { _appTagSettings = AppTags.Observe(_appTagSettings, snapshots.Select(entry => (entry.Key.Identity.Id, entry.Value))); }
            catch (Exception error) when (IsAppSettingsError(error))
            {
                GD.PushWarning("无法保存应用标签：" + error.Message);
                canSave = false;
            }
        }
        _tagGroups = _appTagSettings.Groups.OrderBy(group => group.Name, TagComparer).ToArray();
        var membership = collection?.Members.ToHashSet();
        var tagCounts = libraries.Where(library => collection != null || scoped == null || library == scoped)
            .SelectMany(library => snapshots[library].Assets.Where(asset => asset.Trashed == trash &&
                (membership == null || membership.Contains(new(library.Identity.Id, asset.Id)))))
            .SelectMany(asset => WorkspaceApi.ParseTags(asset.Tags).Distinct(TagComparer))
            .GroupBy(tag => tag, TagComparer).ToDictionary(group => group.Key, group => group.Count(), TagComparer);
        foreach (var tag in _appTagSettings.Tags) tagCounts.TryAdd(tag, 0);

        var all = tagCounts.Select(tag => (Name: tag.Key, Count: tag.Value))
            .OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Name, TagComparer).ToArray();
        var groups = _tagGroups.Select(group => (group.Id, group.Name, group.Tags
            .Select(tag => (Name: tag, Count: tagCounts.GetValueOrDefault(tag)))
            .OrderByDescending(tag => tag.Count).ThenBy(tag => tag.Name, TagComparer).ToArray()));
        _tagSections = [(AppTagCatalog.AllGroupId, AppTagCatalog.AllGroupName, all), .. groups];

        _sideBar.SetTagSections(_tagSections);
        _tagCatalog = canSave ? (catalog.Category, catalog.Library, libraryRevisions, _browseLibraryRevision,
            _appTagSettings.Revision, _collectionRevision) : null;
    }

    private void BrowseByTagGroup(string id)
    {
        var tags = _tagSections.FirstOrDefault(section => section.GroupId == id).Tags;
        if (tags is not { Length: > 0 }) return;
        _selectedTags.Clear();
        _selectedTags.UnionWith(tags.Select(tag => tag.Name));
        _tagMatchMode.Select(1);
        _workspace.ShowPanel("assets");
        RefreshAfterFilterChange();
    }

    private MenuButton BuildTagGroupMenu()
    {
        _tagGroupMenu = new MenuButton
        {
            Name = "AssetTagGroupMenu", Text = "标签管理 ▾", TooltipText = "管理整个应用共用的标签与标签组",
            Flat = false, CustomMinimumSize = new(0, 32), FocusMode = FocusModeEnum.All
        };
        var popup = _tagGroupMenu.GetPopup();
        _tagGroupMenu.AboutToPopup += PopulateAppTagMenu;
        popup.IndexPressed += index =>
        {
            if (index == 0) ShowTagGroupEditor(null);
            else if (index == popup.ItemCount - 1) ShowAddAppTags();
            else ShowTagGroupEditor(popup.GetItemMetadata((int)index).AsString());
        };
        return _tagGroupMenu;
    }

    private void PopulateAppTagMenu()
    {
        RefreshTagCatalog();
        var popup = _tagGroupMenu.GetPopup();
        popup.Clear();
        popup.AddItem("新建标签组…");
        if (_tagGroups.Length > 0) popup.AddSeparator();
        foreach (var group in _tagGroups)
        {
            string label = $"编辑 {group.Name}…";
            popup.AddItem(Shell.Typography.Fit(Shell.Typography.Regular, label, Shell.Type.Body, 270));
            popup.SetItemMetadata(popup.ItemCount - 1, group.Id);
            popup.SetItemTooltip(popup.ItemCount - 1, label);
        }
        popup.AddSeparator();
        popup.AddItem("新建标签…");
    }

    private void ShowAppTagMenu()
    {
        PopulateAppTagMenu();
        PopupMenuAtPointer(_tagGroupMenu.GetPopup());
    }

    private void ShowAddAppTags() => ShowEntry("新建标签", "标签名称（多个标签用空格或逗号分隔）", "", null, "", false, (text, _) =>
    {
        AppTags.AddTags(WorkspaceApi.ParseTags(text));
        Refresh();
        SetStatus("标签已保存，可在「全部」中找到");
    });

    private void BuildTagGroupDialog()
    {
        _tagGroupDialog = new AcceptDialog { Name = "AssetTagGroupEditor", OkButtonText = "保存", DialogHideOnOk = false };
        _tagGroupDialog.AddCancelButton("取消");
        _deleteTagGroup = _tagGroupDialog.AddButton("删除组…", true, "delete_group");
        _tagGroupDialog.CustomAction += _ => ConfirmDeleteTagGroup();
        _tagGroupDialog.Confirmed += SaveTagGroup;
        AddChild(_tagGroupDialog);

        var column = Column(_tagGroupDialog);
        column.AddChild(Label("组名", false, 13));
        _tagGroupName = Input("例如 Name");
        _tagGroupName.Name = "AssetTagGroupName";
        column.AddChild(_tagGroupName);

        column.AddChild(Wrapped("勾选要放进此组的标签。每个标签可以加入多个组。"));
        _tagGroupSearch = Input("搜索应用标签…");
        _tagGroupSearch.TextChanged += _ => PopulateTagGroupMembers();
        column.AddChild(_tagGroupSearch);
        _tagGroupMembers = new Tree
        {
            Name = "AssetTagGroupMembers", HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new(0, 96)
        };
        _tagGroupMembers.ItemEdited += () =>
        {
            if (_tagGroupMembers.GetEdited() is not { } item) return;
            string tag = item.GetMetadata(0).AsString();
            if (item.IsChecked(0)) _tagGroupDraft.Add(tag);
            else _tagGroupDraft.Remove(tag);
            SyncTagGroupCount();
        };
        column.AddChild(_tagGroupMembers);
        _tagGroupCount = Label("", true, 12);
        column.AddChild(_tagGroupCount);
        _tagGroupError = Wrapped("", false);
        _tagGroupError.AddThemeColorOverride("font_color", Shell.Tokens.Danger);
        column.AddChild(_tagGroupError);
    }

    private void ShowTagGroupEditor(string? id)
    {
        RefreshTagCatalog();
        _editingTagGroup = _tagGroups.FirstOrDefault(group => group.Id == id);
        if (id != null && _editingTagGroup == null) { Refresh(); return; }

        _tagGroupDraft.Clear();
        _tagGroupDraft.UnionWith(_editingTagGroup?.Tags ?? []);
        var otherGroups = _tagGroups.Where(group => group.Id != id)
            .SelectMany(group => group.Tags.Select(tag => (Tag: tag, Group: group.Name)))
            .ToLookup(entry => entry.Tag, entry => entry.Group, TagComparer);
        _tagGroupOptions = _appTagSettings.Tags.Order(TagComparer)
            .Select(tag => (tag, string.Join("、", otherGroups[tag]))).ToArray();
        _tagGroupDialog.Title = id == null ? "新建应用标签组" : "编辑应用标签组";
        _tagGroupName.Text = _editingTagGroup?.Name ?? "";
        _tagGroupSearch.Text = "";
        _tagGroupError.Hide();
        _deleteTagGroup.Visible = id != null;
        PopulateTagGroupMembers();
        PopupFit(_tagGroupDialog, new(490, 460));
        _tagGroupName.GrabFocus();
        _tagGroupName.SelectAll();
    }

    private void PopulateTagGroupMembers()
    {
        _tagGroupMembers.Clear();
        var root = _tagGroupMembers.CreateItem();
        var query = _tagGroupSearch.Text.Trim();
        foreach (var (name, otherGroups) in _tagGroupOptions)
        {
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var item = _tagGroupMembers.CreateItem(root);
            item.SetCellMode(0, TreeItem.TreeCellMode.Check);
            item.SetEditable(0, true);
            item.SetMetadata(0, name);
            item.SetText(0, otherGroups.Length == 0 ? name : $"{name} · {otherGroups}");
            item.SetChecked(0, _tagGroupDraft.Contains(name));
        }
        SyncTagGroupCount();
    }

    private void SyncTagGroupCount() => _tagGroupCount.Text = $"已选 {_tagGroupDraft.Count} 个标签";

    private void SaveTagGroup()
    {
        try
        {
            AppTags.SaveGroup(_editingTagGroup?.Id, _tagGroupName.Text, _tagGroupDraft);
            _tagGroupDialog.Hide();
            Refresh();
            SetStatus("标签组已保存 · " + _tagGroupName.Text.Trim());
        }
        catch (Exception error) { _tagGroupError.Text = error.Message; _tagGroupError.Show(); }
    }

    private void ConfirmDeleteTagGroup()
    {
        if (_editingTagGroup is not { } group) return;
        _tagGroupDialog.Hide();
        ShowApproval("删除标签组", $"删除「{group.Name}」？组里的标签仍可在「全部」中找到。", () =>
        {
            AppTags.DeleteGroup(group.Id);
            Refresh();
            SetStatus("已删除标签组 · " + group.Name);
        });
    }
}
