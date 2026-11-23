using Animanager.Core.Storage;
using Animanager.UI.Shell;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private MenuButton _libraryTagManagement = null!;
    private AcceptDialog _libraryTagGroupEditor = null!;
    private ConfirmationDialog _deleteLibraryTagGroupDialog = null!;
    private Button _deleteLibraryTagGroup = null!;
    private LineEdit _libraryTagGroupName = null!, _libraryTagGroupSearch = null!;
    private Label _libraryTagGroupError = null!;
    private Tree _libraryTagGroupMembers = null!;
    private AssetTagGroup? _editingLibraryTagGroup;
    private readonly HashSet<string> _libraryTagGroupDraft = new(TagComparer);

    private MenuButton BuildLibraryTagManagementMenu()
    {
        _libraryTagManagement = new MenuButton { Name = "LibraryTagManagement", Text = "标签管理  ▾", Flat = false,
            FocusMode = FocusModeEnum.All, CustomMinimumSize = new(0, 32), MouseDefaultCursorShape = CursorShape.PointingHand };
        var popup = _libraryTagManagement.GetPopup();
        _libraryTagManagement.AboutToPopup += () =>
        {
            popup.Clear();
            popup.AddItem("新建库标签…");
            popup.AddItem("新建标签组…");
            if (_libraryTagGroups.Length > 0) popup.AddSeparator();
            foreach (var group in _libraryTagGroups.OrderBy(group => group.Name, TagComparer))
            {
                string label = $"编辑组「{group.Name}」…";
                popup.AddItem(Typography.Fit(Typography.Regular, label, Shell.Type.Body, 260));
                popup.SetItemMetadata(popup.ItemCount - 1, group.Id);
                popup.SetItemTooltip(popup.ItemCount - 1, label);
            }
        };
        popup.IndexPressed += index =>
        {
            if (index == 0) ShowLibraryTagCreator();
            else if (index == 1) ShowLibraryTagGroupEditor(null);
            else if (Array.Find(_libraryTagGroups, group => group.Id == popup.GetItemMetadata((int)index).AsString()) is { } group)
                ShowLibraryTagGroupEditor(group);
        };
        return _libraryTagManagement;
    }

    private void BuildLibraryTagGroupEditor()
    {
        _libraryTagGroupEditor = new AcceptDialog { Name = "LibraryTagGroupEditor", OkButtonText = "保存",
            DialogHideOnOk = false, Transient = true, Exclusive = true };
        _libraryTagGroupEditor.AddCancelButton("取消");
        _libraryTagGroupEditor.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        _libraryTagGroupEditor.Confirmed += SaveLibraryTagGroup;
        _deleteLibraryTagGroup = _libraryTagGroupEditor.AddButton("删除组…", false, "delete_group");
        _libraryTagGroupEditor.CustomAction += _ => ConfirmDeleteLibraryTagGroup();
        _libraryDialog.AddChild(_libraryTagGroupEditor);

        var column = Column(_libraryTagGroupEditor);
        column.AddChild(Label("组名", true, 12));
        _libraryTagGroupName = Input("例如 常用包");
        _libraryTagGroupName.Name = "LibraryTagGroupName";
        column.AddChild(_libraryTagGroupName);
        _libraryTagGroupSearch = Input("搜索可加入组的标签…");
        _libraryTagGroupSearch.Name = "LibraryTagGroupSearch";
        _libraryTagGroupSearch.TextChanged += _ => { CaptureLibraryTagGroupMembers(); RenderLibraryTagGroupMembers(); };
        column.AddChild(_libraryTagGroupSearch);
        _libraryTagGroupMembers = new Tree { Name = "LibraryTagGroupMembers", HideRoot = true, ScrollHorizontalEnabled = false,
            SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 108) };
        _libraryTagGroupMembers.SetColumnClipContent(0, true);
        column.AddChild(_libraryTagGroupMembers);
        _libraryTagGroupError = Label("", false, 12);
        _libraryTagGroupError.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _libraryTagGroupError.MouseFilter = MouseFilterEnum.Pass;
        _libraryTagGroupError.AddThemeColorOverride("font_color", Tokens.Danger);
        column.AddChild(_libraryTagGroupError);

        _deleteLibraryTagGroupDialog = new ConfirmationDialog { Title = "删除标签组", OkButtonText = "删除", CancelButtonText = "取消",
            Transient = true, Exclusive = true };
        _deleteLibraryTagGroupDialog.Confirmed += DeleteLibraryTagGroup;
        _libraryTagGroupEditor.AddChild(_deleteLibraryTagGroupDialog);
    }

    private void ShowLibraryTagGroupEditor(AssetTagGroup? group)
    {
        _editingLibraryTagGroup = group;
        _libraryTagGroupDraft.Clear();
        _libraryTagGroupDraft.UnionWith(group?.Tags ?? []);
        _libraryTagGroupEditor.Title = group == null ? "新建库标签组" : "编辑库标签组";
        _libraryTagGroupName.Text = group?.Name ?? "";
        _libraryTagGroupSearch.Text = "";
        _deleteLibraryTagGroup.Visible = group != null;
        _libraryTagGroupError.Hide();
        RenderLibraryTagGroupMembers();
        PopupFit(_libraryTagGroupEditor, new(440, 395));
        _libraryTagGroupName.GrabFocus();
        _libraryTagGroupName.SelectAll();
    }

    private void CaptureLibraryTagGroupMembers()
    {
        if (_libraryTagGroupMembers.GetRoot() is not { } root) return;
        foreach (var row in root.GetChildren())
        {
            if (row.GetMetadata(0).VariantType != Variant.Type.String) continue;
            string tag = row.GetMetadata(0).AsString();
            if (row.IsChecked(0)) _libraryTagGroupDraft.Add(tag);
            else _libraryTagGroupDraft.Remove(tag);
        }
    }

    private void RenderLibraryTagGroupMembers()
    {
        _libraryTagGroupMembers.Clear();
        var root = _libraryTagGroupMembers.CreateItem();
        string query = _libraryTagGroupSearch.Text.Trim();
        foreach (var tag in _libraryTagCatalog.Select(tag => tag.Name).Where(name => name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Order(TagComparer))
        {
            var row = root.CreateChild();
            row.SetCellMode(0, TreeItem.TreeCellMode.Check);
            row.SetEditable(0, true);
            row.SetText(0, tag);
            row.SetTooltipText(0, tag);
            row.SetMetadata(0, tag);
            row.SetChecked(0, _libraryTagGroupDraft.Contains(tag));
        }
        if (root.GetChildCount() != 0) return;
        var empty = root.CreateChild();
        empty.SetSelectable(0, false);
        empty.SetText(0, _libraryTagCatalog.Length == 0 ? "暂无库标签" : "没有匹配的标签");
    }

    private void SaveLibraryTagGroup()
    {
        CaptureLibraryTagGroupMembers();
        EditLibraryTagGroups(() => _libraries.SaveTagGroup(_editingLibraryTagGroup?.Id, _libraryTagGroupName.Text, _libraryTagGroupDraft));
    }

    private void ConfirmDeleteLibraryTagGroup()
    {
        if (_editingLibraryTagGroup is not { } group) return;
        _deleteLibraryTagGroupDialog.DialogText = $"删除标签组「{group.Name}」？";
        PopupFit(_deleteLibraryTagGroupDialog, new(390, 140));
    }

    private void DeleteLibraryTagGroup()
    {
        if (_editingLibraryTagGroup is { } group) EditLibraryTagGroups(() => _libraries.DeleteTagGroup(group.Id));
    }

    private void EditLibraryTagGroups(Action update)
    {
        try { update(); }
        catch (Exception error)
        {
            _libraryTagGroupError.Text = _libraryTagGroupError.TooltipText = error.Message;
            _libraryTagGroupError.Show();
            return;
        }
        _libraryTagGroupEditor.Hide();
        ShowLibraryDialog(LibrarySelection?.Id);
    }
}
