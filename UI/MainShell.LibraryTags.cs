using Animanager.Core.Storage;
using Animanager.UI.Controls;
using Animanager.UI.Shell;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private LibraryTag[] _libraryTagCatalog = [];
    private AssetTagGroup[] _libraryTagGroups = [];
    private string? _libraryTagFilter, _libraryTagGroupFilterId;
    private LineEdit _librarySearch = null!;
    private OptionButton _libraryTagGroupFilter = null!;
    private ScrollContainer _libraryTagFilterScroll = null!;
    private HFlowContainer _libraryTagFilterChips = null!;
    private Button _clearLibraryFilters = null!;

    private Button _assignLibraryTags = null!;
    private AcceptDialog _libraryTagCreator = null!, _libraryTagAssignment = null!;
    private Label _libraryTagCreationError = null!, _libraryTagTarget = null!, _libraryTagFeedback = null!;
    private LibraryTagLane _availableLibraryTags = null!, _assignedLibraryTags = null!;
    private VBoxContainer _availableLibraryTagContent = null!, _assignedLibraryTagContent = null!;
    private Tree _tagBoardLibraries = null!;
    private LineEdit _tagBoardSearch = null!;
    private LineEdit _newLibraryTagName = null!;
    private string? _taggedLibraryId;
    private AssetTagGroup? SelectedLibraryTagGroup => Array.Find(_libraryTagGroups, group => group.Id == _libraryTagGroupFilterId);
    private bool HasLibraryFilters => _libraryTagFilter != null || _libraryTagGroupFilterId != null ||
        !string.IsNullOrWhiteSpace(_librarySearch.Text);

    private void BuildLibraryFilters(VBoxContainer content)
    {
        var toolbar = Row(content);
        _librarySearch = Input("搜索库名称…");
        _librarySearch.Name = "LibrarySearch";
        toolbar.AddChild(_librarySearch);
        toolbar.AddChild(BuildLibraryTagManagementMenu());
        _assignLibraryTags = Button("拖动管理…", ShowLibraryTagAssignment);
        _assignLibraryTags.Name = "AssignLibraryTags";
        _assignLibraryTags.TooltipText = "将标签拖到资产库，集中管理多个库的标签";
        toolbar.AddChild(_assignLibraryTags);
        var filters = Row(content);
        _libraryTagGroupFilter = new OptionButton { Name = "LibraryTagGroupFilter", Visible = false,
            SizeFlagsVertical = SizeFlags.ShrinkBegin, TooltipText = "按标签组筛选" };
        _libraryTagGroupFilter.ItemSelected += index =>
        {
            _libraryTagGroupFilterId = index == 0 ? null : _libraryTagGroupFilter.GetItemMetadata((int)index).AsString();
            if (SelectedLibraryTagGroup is { } group && _libraryTagFilter is { } tag && !group.Tags.Contains(tag, TagComparer))
                _libraryTagFilter = null;
            RenderLibraryTagFilterChips();
            RefreshLibraryFilters();
        };
        filters.AddChild(_libraryTagGroupFilter);
        _libraryTagFilterScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, FollowFocus = true, CustomMinimumSize = new(0, 56), Visible = false };
        _libraryTagFilterChips = new HFlowContainer { Name = "LibraryTagFilterChips", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _libraryTagFilterScroll.AddChild(_libraryTagFilterChips);
        filters.AddChild(_libraryTagFilterScroll);
        _clearLibraryFilters = Button("清除筛选", () =>
        {
            _librarySearch.Text = "";
            _libraryTagFilter = _libraryTagGroupFilterId = null;
            SyncLibraryTagFilters();
            RefreshLibraryFilters();
        });
        _clearLibraryFilters.Name = "ClearLibraryFilters";
        _clearLibraryFilters.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        filters.AddChild(_clearLibraryFilters);
        _librarySearch.TextChanged += _ => RefreshLibraryFilters();
        _clearLibraryFilters.Hide();
    }

    private void SyncLibraryTagFilters()
    {
        var group = SelectedLibraryTagGroup;
        if (group == null) _libraryTagGroupFilterId = null;
        if (_libraryTagFilter is { } tag && (!_libraryTagCatalog.Any(item => TagComparer.Equals(item.Name, tag)) ||
            (group != null && !group.Tags.Contains(tag, TagComparer)))) _libraryTagFilter = null;

        _libraryTagGroupFilter.Clear();
        _libraryTagGroupFilter.AddItem("全部标签");
        _libraryTagGroupFilter.Select(0);
        foreach (var option in _libraryTagGroups.OrderBy(item => item.Name, TagComparer))
        {
            int index = _libraryTagGroupFilter.ItemCount;
            _libraryTagGroupFilter.AddItem(Typography.Fit(Typography.Regular, option.Name, Shell.Type.Body, 125));
            _libraryTagGroupFilter.SetItemMetadata(index, option.Id);
            _libraryTagGroupFilter.SetItemTooltip(index, option.Name);
            if (option.Id == _libraryTagGroupFilterId) _libraryTagGroupFilter.Select(index);
        }
        _libraryTagGroupFilter.Visible = _libraryTagGroups.Length > 0;
        RenderLibraryTagFilterChips();
        SyncLibraryFilterButtons();
    }

    private void RenderLibraryTagFilterChips()
    {
        foreach (var child in _libraryTagFilterChips.GetChildren()) { _libraryTagFilterChips.RemoveChild(child); child.QueueFree(); }
        var scope = SelectedLibraryTagGroup?.Tags ?? _libraryTagCatalog.Select(tag => tag.Name).ToArray();
        foreach (var name in scope.Order(TagComparer))
        {
            var chip = Button(Typography.Fit(Typography.Regular, name, Shell.Type.Caption, 144), () =>
            {
                _libraryTagFilter = TagComparer.Equals(_libraryTagFilter, name) ? null : name;
                RefreshLibraryFilters();
            });
            chip.ThemeTypeVariation = "Chip";
            chip.ToggleMode = true;
            chip.CustomMinimumSize = new(0, 24);
            chip.TooltipText = name;
            _libraryTagFilterChips.AddChild(chip);
        }
        _libraryTagFilterScroll.Visible = scope.Length > 0;
        _libraryTagFilterScroll.ScrollVertical = 0;
    }

    private void SyncLibraryFilterButtons()
    {
        foreach (var chip in _libraryTagFilterChips.GetChildren().OfType<Button>())
            chip.SetPressedNoSignal(TagComparer.Equals(chip.TooltipText, _libraryTagFilter));
        _clearLibraryFilters.Visible = HasLibraryFilters;
    }

    private void RefreshLibraryFilters()
    {
        SyncLibraryFilterButtons();
        RenderLibraryRows(LibrarySelection?.Id);
    }

    private LibraryEntry[] FilterLibraryEntries()
    {
        string query = _librarySearch.Text.Trim();
        var groupTags = SelectedLibraryTagGroup?.Tags.ToHashSet(TagComparer);
        return _entries.Where(entry =>
        {
            if (!entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return false;
            var tags = entry.Tags ?? [];
            if (_libraryTagFilter is { } name) return tags.Any(tag => TagComparer.Equals(tag.Name, name));
            return groupTags == null || tags.Any(tag => groupTags.Contains(tag.Name));
        }).ToArray();
    }

    private void BuildLibraryTagDialogs()
    {
        _libraryTagCreator = new AcceptDialog { Name = "LibraryTagCreator", Title = "新建库标签", OkButtonText = "创建",
            DialogHideOnOk = false, Transient = true, Exclusive = true };
        _libraryTagCreator.AddCancelButton("取消");
        _libraryTagCreator.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        _libraryTagCreator.Confirmed += CreateLibraryTag;
        _libraryDialog.AddChild(_libraryTagCreator);

        var form = Column(_libraryTagCreator);
        form.AddChild(Label("名称", true, 12));
        _newLibraryTagName = Input("例如 双太刀");
        _newLibraryTagName.Name = "NewLibraryTagName";
        form.AddChild(_newLibraryTagName);
        _libraryTagCreator.RegisterTextEnter(_newLibraryTagName);
        _libraryTagCreationError = Wrapped("", false);
        _libraryTagCreationError.AddThemeColorOverride("font_color", Tokens.Danger);
        form.AddChild(_libraryTagCreationError);
        BuildLibraryTagGroupEditor();

        _libraryTagAssignment = new AcceptDialog { Name = "LibraryTagAssignment", Title = "库标签拖动管理", OkButtonText = "完成",
            Transient = true, Exclusive = true };
        _libraryTagAssignment.VisibilityChanged += () => { if (!_libraryTagAssignment.Visible) _taggedLibraryId = null; };
        _libraryDialog.AddChild(_libraryTagAssignment);
        var content = Column(_libraryTagAssignment);
        var board = Column(content);
        board.AddThemeConstantOverride("separation", Tokens.Space.Md);
        BuildTagBoardLibraryList(board);

        var tagPanel = Column(board);
        tagPanel.SizeFlagsStretchRatio = 1.65f;
        _libraryTagTarget = Label("");
        _libraryTagTarget.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        tagPanel.AddChild(_libraryTagTarget);
        var columns = Row(tagPanel);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        (_availableLibraryTags, _availableLibraryTagContent) = BuildLibraryTagLane(columns, "AvailableLibraryTags", "可用标签");
        (_assignedLibraryTags, _assignedLibraryTagContent) = BuildLibraryTagLane(columns, "AssignedLibraryTags", "已分配标签");
        _availableLibraryTags.OtherLane = _assignedLibraryTags;
        _assignedLibraryTags.OtherLane = _availableLibraryTags;
        _availableLibraryTags.TagDropped += tag => MoveLibraryTag(tag, add: false);
        _assignedLibraryTags.TagDropped += tag => MoveLibraryTag(tag, add: true);
        _libraryTagFeedback = Label("", true, 12);
        _libraryTagFeedback.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _libraryTagFeedback.MouseFilter = MouseFilterEnum.Pass;
        _libraryTagFeedback.CustomMinimumSize = new(0, 20);
        content.AddChild(_libraryTagFeedback);
    }

    private void BuildTagBoardLibraryList(BoxContainer board)
    {
        var panel = Column(board);
        _tagBoardSearch = Input("搜索资产库…");
        _tagBoardSearch.Name = "TagBoardLibrarySearch";
        _tagBoardSearch.TextChanged += _ => RenderTagBoardLibraries();
        panel.AddChild(_tagBoardSearch);
        _tagBoardLibraries = new Tree { Name = "TagBoardLibraries", Columns = 2, ColumnTitlesVisible = true,
            HideRoot = true, HideFolding = true, SelectMode = Tree.SelectModeEnum.Row, ScrollHorizontalEnabled = false,
            SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 52) };
        _tagBoardLibraries.SetColumnTitle(0, "资产库 · 拖入标签");
        _tagBoardLibraries.SetColumnTitle(1, "已分配标签");
        _tagBoardLibraries.SetColumnClipContent(0, true);
        _tagBoardLibraries.SetColumnClipContent(1, true);
        _tagBoardLibraries.ItemSelected += () =>
        {
            if (TagBoardEntry(_tagBoardLibraries.GetSelected()) is { } entry) SelectTagBoardLibrary(entry);
        };
        _tagBoardLibraries.SetDragForwarding(default, Callable.From<Vector2, Variant, bool>((position, data) =>
        {
            bool allowed = TagBoardDrop(position, data) != null;
            _tagBoardLibraries.DropModeFlags = (int)(allowed ? Tree.DropModeFlagsEnum.OnItem : Tree.DropModeFlagsEnum.Disabled);
            return allowed;
        }), Callable.From<Vector2, Variant>((position, data) =>
        {
            if (TagBoardDrop(position, data) is { } drop)
                Callable.From(() => ApplyLibraryTag(drop.Id, drop.Tag, add: true)).CallDeferred();
        }));
        panel.AddChild(_tagBoardLibraries);
    }

    private LibraryEntry? TagBoardEntry(TreeItem? row)
    {
        var id = row?.GetMetadata(0).AsString();
        return id == null ? null : Array.Find(_entries, entry => entry.Id == id);
    }

    private (string Id, LibraryTag Tag)? TagBoardDrop(Vector2 position, Variant data)
    {
        if (data.VariantType != Variant.Type.Object || data.AsGodotObject() is not LibraryTagChip chip ||
            (chip.Lane != _availableLibraryTags && chip.Lane != _assignedLibraryTags)) return null;
        var entry = TagBoardEntry(_tagBoardLibraries.GetItemAtPosition(position));
        return entry != null && !(entry.Tags ?? []).Contains(chip.Tag, LibraryTag.Comparer) ? (entry.Id, chip.Tag) : null;
    }

    private void RenderTagBoardLibraries()
    {
        _tagBoardLibraries.Clear();
        var root = _tagBoardLibraries.CreateItem();
        var query = _tagBoardSearch.Text.Trim();
        foreach (var entry in _entries.Where(entry => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var row = root.CreateChild();
            row.CustomMinimumHeight = Tokens.Size.Row;
            row.SetText(0, entry.Name);
            row.SetTooltipText(0, entry.Name);
            row.SetMetadata(0, entry.Id);
            UpdateTagBoardRow(row, entry);
            if (entry.Id == _taggedLibraryId) row.Select(0);
        }
        if (_tagBoardLibraries.GetSelected() != null)
            _tagBoardLibraries.CallDeferred(Tree.MethodName.EnsureCursorIsVisible);
        else Callable.From(() =>
        {
            if (_tagBoardLibraries.GetSelected() == null && _tagBoardLibraries.GetRoot()?.GetFirstChild() is { } first)
                _tagBoardLibraries.ScrollToItem(first);
        }).CallDeferred();
    }

    private static void UpdateTagBoardRow(TreeItem row, LibraryEntry entry)
    {
        string tags = string.Join(" · ", (entry.Tags ?? []).Select(tag => tag.Name));
        row.SetText(1, tags.Length == 0 ? "—" : tags);
        row.SetTooltipText(1, tags);
        row.SetCustomColor(1, Tokens.Text2);
    }

    private void SelectTagBoardLibrary(LibraryEntry entry)
    {
        _taggedLibraryId = entry.Id;
        _libraryTagTarget.Text = $"当前库 · {entry.Name}";
        _libraryTagTarget.TooltipText = entry.Name;
        PopulateLibraryTagLanes(entry.Tags ?? []);
    }

    private static (LibraryTagLane Lane, VBoxContainer Groups) BuildLibraryTagLane(HBoxContainer columns, string name, string title)
    {
        var lane = new LibraryTagLane { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddChild(lane);
        var column = Column(lane);
        column.MouseFilter = MouseFilterEnum.Pass;
        column.AddChild(Label(title));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new(0, 52), FollowFocus = true };
        scroll.SetDragForwarding(default, Callable.From<Vector2, Variant, bool>(lane._CanDropData),
            Callable.From<Vector2, Variant>(lane._DropData));
        column.AddChild(scroll);
        var groups = Column(scroll);
        groups.MouseFilter = MouseFilterEnum.Pass;
        return (lane, groups);
    }

    private void ShowLibraryTagCreator()
    {
        _newLibraryTagName.Text = "";
        _libraryTagCreationError.Hide();
        PopupFit(_libraryTagCreator, new(390, 150));
        _newLibraryTagName.GrabFocus();
    }

    private void CreateLibraryTag()
    {
        try { _libraries.CreateTag(_newLibraryTagName.Text); }
        catch (Exception error)
        {
            _libraryTagCreationError.Text = error.Message;
            _libraryTagCreationError.Show();
            return;
        }
        _libraryTagCreator.Hide();
        ShowLibraryDialog(LibrarySelection?.Id);
        SetStatus($"已创建库标签「{_newLibraryTagName.Text.Trim()}」");
    }

    private void ShowLibraryTagAssignment()
    {
        if (_libraryLoading || (LibrarySelection ?? _entries.FirstOrDefault()) is not { } entry) return;
        _tagBoardSearch.Text = "";
        SelectTagBoardLibrary(entry);
        RenderTagBoardLibraries();
        _libraryTagFeedback.Text = _libraryTagFeedback.TooltipText = "拖到库上添加，拖回可用标签区移除。拖放后自动保存。";
        _libraryTagFeedback.RemoveThemeColorOverride("font_color");
        _libraryTagAssignment.MinSize = new(Math.Min((int)(600 * _display.EffectiveScale), Math.Max(160, GetWindow().Size.X - 40)), 0);
        PopupFit(_libraryTagAssignment, new(920, 530));
    }

    private void PopulateLibraryTagLanes(LibraryTag[] assigned)
    {
        FillLibraryTagLane(_availableLibraryTags, _availableLibraryTagContent,
            _libraryTagCatalog.Except(assigned, LibraryTag.Comparer), assigned: false,
            _libraryTagCatalog.Length == 0 ? "先从「标签管理」创建库标签" : "所有可用标签都已分配");
        FillLibraryTagLane(_assignedLibraryTags, _assignedLibraryTagContent, assigned, assigned: true, "将左栏的标签拖到这里");
    }

    private static void FillLibraryTagLane(LibraryTagLane lane, VBoxContainer content, IEnumerable<LibraryTag> tags, bool assigned, string emptyText)
    {
        foreach (var child in content.GetChildren()) { content.RemoveChild(child); child.QueueFree(); }
        var chips = new HFlowContainer { MouseFilter = MouseFilterEnum.Pass };
        content.AddChild(chips);
        foreach (var tag in tags.OrderBy(tag => tag.Name, TagComparer))
            chips.AddChild(new LibraryTagChip(tag, lane, assigned));
        if (chips.GetChildCount() == 0) content.AddChild(Wrapped(emptyText));
    }

    private void MoveLibraryTag(LibraryTag tag, bool add)
    {
        if (_taggedLibraryId is { } id) Callable.From(() => ApplyLibraryTag(id, tag, add)).CallDeferred();
    }

    private void ApplyLibraryTag(string id, LibraryTag tag, bool add)
    {
        if (_closing) return;
        LibraryEntry entry;
        try { entry = _libraries.ChangeTag(id, tag, add); }
        catch (Exception error)
        {
            if (_libraryTagAssignment.Visible)
            {
                _libraryTagFeedback.AddThemeColorOverride("font_color", Tokens.Danger);
                _libraryTagFeedback.Text = _libraryTagFeedback.TooltipText = error.Message;
            }
            else ShowError("库标签保存失败", error.Message);
            return;
        }
        if (_libraryDialog.Visible) ShowLibraryDialog(LibrarySelection?.Id);
        if (!_libraryTagAssignment.Visible) return;
        var row = _tagBoardLibraries.GetRoot().GetChildren().FirstOrDefault(row => row.GetMetadata(0).AsString() == id);
        if (row != null) UpdateTagBoardRow(row, entry);
        if (_taggedLibraryId == id) PopulateLibraryTagLanes(entry.Tags ?? []);
        _libraryTagFeedback.RemoveThemeColorOverride("font_color");
        _libraryTagFeedback.Text = _libraryTagFeedback.TooltipText = add
            ? $"已为「{entry.Name}」添加「{tag.Name}」" : $"已从「{entry.Name}」移除「{tag.Name}」";
    }
}
