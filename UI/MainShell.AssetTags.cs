using Animanager.Core;
using Animanager.Core.Storage;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private AcceptDialog _assetTagDialog = null!;
    private LineEdit _assetTagInput = null!;
    private ItemList _assetTagSuggestions = null!;
    private Label _assetTagScope = null!, _assetTagError = null!;
    private (LibrarySession Library, string[] Ids)[] _assetTagTargets = [];
    private Dictionary<string, (string Name, int Usage)> _assetTagChoices = new(TagComparer);
    private (string Tag, Dictionary<string, string[]> Libraries)? _lastAssetTagDrop;

    private void DropAssetsOnTag(string tag, string[] keys)
    {
        var groups = DraggedAssetGroups(keys);
        if (groups.Length == 0) return;

        var additions = new Dictionary<string, string[]>();
        try
        {
            foreach (var (library, ids) in groups)
            {
                var added = library.Api.ChangeAssetTag(ids, tag, add: true);
                if (added.Length > 0) additions.Add(library.Identity.Id, added);
            }
        }
        finally
        {
            if (additions.Count > 0)
            {
                _lastAssetTagDrop = (tag, additions);
                SyncAssetTagDropUndo();
                Refresh();
            }
        }

        int count = additions.Values.Sum(ids => ids.Length);
        SetStatus(count > 0 ? $"已为 {count} 个资产添加「{tag}」" : $"所拖动的资产已有「{tag}」");
    }

    private void UndoAssetTagDrop()
    {
        if (_closing || _libraryLoading || _lastAssetTagDrop is not { } undo) return;

        int count = 0;
        try
        {
            foreach (var (libraryId, ids) in undo.Libraries.ToArray())
            {
                if (_libraries.FindOpenLibrary(libraryId) is not { } library) continue;
                var live = library.Api.Database.ExistingIds(ids, trash: false);
                if (live.Length > 0) count += library.Api.ChangeAssetTag(live, undo.Tag, add: false).Length;
                undo.Libraries.Remove(libraryId);
            }
        }
        finally
        {
            if (undo.Libraries.Count == 0) _lastAssetTagDrop = null;
            SyncAssetTagDropUndo();
            if (count > 0) Refresh();
        }

        SetStatus(undo.Libraries.Count > 0 ? $"已从 {count} 个资产撤销「{undo.Tag}」 · 请打开其他来源库后继续撤销"
            : $"已撤销打标 · 从 {count} 个资产移除「{undo.Tag}」");
    }

    private void SyncAssetTagDropUndo()
    {
        if (_lastAssetTagDrop is { } undo)
            _workspace.Status.SetAction("撤销打标", $"撤销为 {undo.Libraries.Values.Sum(ids => ids.Length)} 个资产添加「{undo.Tag}」",
                () => LibraryAction(UndoAssetTagDrop));
        else _workspace.Status.SetAction("", "", null);
    }

    private void BuildAssetTagDialog()
    {
        _assetTagDialog = new AcceptDialog
        {
            Name = "AssetTagEditor", Title = "添加标签", OkButtonText = "添加标签", DialogHideOnOk = false
        };
        _assetTagDialog.AddCancelButton("取消");
        _assetTagDialog.GetOkButton().ThemeTypeVariation = "PrimaryButton";
        _assetTagDialog.Confirmed += ApplySuggestedAssetTags;
        _assetTagDialog.VisibilityChanged += () =>
        {
            if (!_assetTagDialog.Visible) _assetTagTargets = [];
        };
        AddChild(_assetTagDialog);

        var column = Column(_assetTagDialog);
        _assetTagScope = Wrapped("");
        _assetTagScope.MaxLinesVisible = 2;
        _assetTagScope.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _assetTagScope.MouseFilter = MouseFilterEnum.Pass;
        column.AddChild(_assetTagScope);
        _assetTagInput = Input("搜索已有标签，或输入新标签");
        _assetTagInput.Name = "AssetTagEditorInput";
        _assetTagInput.TooltipText = "多个标签用空格或逗号分隔";
        _assetTagInput.TextChanged += _ => RefreshAssetTagSuggestions();
        column.AddChild(_assetTagInput);
        _assetTagDialog.RegisterTextEnter(_assetTagInput);

        column.AddChild(Label("已有标签 · 已打开库中的使用量", true, 12));
        _assetTagSuggestions = new ItemList
        {
            Name = "AssetTagSuggestions", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new(0, 108)
        };
        _assetTagSuggestions.ItemSelected += index =>
        {
            var tag = _assetTagSuggestions.GetItemMetadata((int)index).AsString();
            Callable.From(() => ChooseAssetTagSuggestion(tag)).CallDeferred();
        };
        column.AddChild(_assetTagSuggestions);
        _assetTagError = Wrapped("", false);
        _assetTagError.AddThemeColorOverride("font_color", Shell.Tokens.Danger);
        column.AddChild(_assetTagError);
    }

    private void ShowAssetTagChange((LibrarySession Library, string[] Ids)[] groups, string scope, bool add)
    {
        if (!add)
        {
            ShowEntry("移除标签", scope + "\n输入标签名称（多个标签用空格或逗号分隔）", "", null, "", false,
                (tags, _) => ApplyAssetEdit(groups, (api, ids) => api.ChangeTags(ids, tags, add: false)));
            return;
        }

        RefreshTagCatalog();
        var counts = LoadedLibraries().SelectMany(library => library.Api.Database.Snapshot().Assets)
            .Where(asset => !asset.Trashed)
            .SelectMany(asset => WorkspaceApi.ParseTags(asset.Tags).Distinct(TagComparer))
            .GroupBy(tag => tag, TagComparer).ToDictionary(group => group.Key, group => group.Count(), TagComparer);
        _assetTagChoices = _appTagSettings.Tags.Concat(counts.Keys).Distinct(TagComparer)
            .ToDictionary(tag => tag, tag => (tag, counts.GetValueOrDefault(tag)), TagComparer);
        _assetTagTargets = groups;
        _assetTagScope.Text = scope;
        _assetTagScope.TooltipText = scope;
        _assetTagInput.Text = "";
        RefreshAssetTagSuggestions();
        PopupFit(_assetTagDialog, new(460, 360));
        _assetTagInput.GrabFocus();
    }

    private static int LastTagStart(string text) => text.LastIndexOfAny([' ', ',', '，', '\t', '\r', '\n']) + 1;

    private void RefreshAssetTagSuggestions()
    {
        string text = _assetTagInput.Text;
        int start = LastTagStart(text);
        string query = text[start..];
        var previous = WorkspaceApi.ParseTags(text[..start]).ToHashSet(TagComparer);
        var matches = _assetTagChoices.Values
            .Where(tag => !previous.Contains(tag.Name) && tag.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(tag => TagComparer.Equals(tag.Name, query))
            .ThenByDescending(tag => tag.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(tag => tag.Usage).ThenBy(tag => tag.Name, TagComparer).Take(60);
        _assetTagSuggestions.Clear();
        foreach (var (name, usage) in matches)
        {
            int index = _assetTagSuggestions.AddItem($"{name} · {usage:N0} 个资产");
            _assetTagSuggestions.SetItemMetadata(index, name);
        }
        if (_assetTagSuggestions.ItemCount == 0)
            _assetTagSuggestions.AddItem(query.Length == 0 ? "暂无更多已有标签" : "没有匹配的已有标签", selectable: false);

        var names = WorkspaceApi.ParseTags(text).ToArray();
        var confirm = _assetTagDialog.GetOkButton();
        confirm.Disabled = names.Length == 0;
        confirm.Text = names.Any(name => !_assetTagChoices.ContainsKey(name)) ? "新建并添加" : "添加标签";
        _assetTagError.Hide();
    }

    private void ChooseAssetTagSuggestion(string tag)
    {
        if (!_assetTagDialog.Visible || tag.Length == 0) return;
        var text = _assetTagInput.Text;
        _assetTagInput.Text = text[..LastTagStart(text)] + tag + ", ";
        _assetTagInput.CaretColumn = _assetTagInput.Text.Length;
        RefreshAssetTagSuggestions();
        _assetTagInput.GrabFocus();
    }

    private void ApplySuggestedAssetTags()
    {
        if (!_assetTagDialog.Visible || _assetTagTargets.Length == 0) return;
        var names = WorkspaceApi.ParseTags(_assetTagInput.Text)
            .Select(name => _assetTagChoices.TryGetValue(name, out var known) ? known.Name : name)
            .Distinct(TagComparer).ToArray();
        if (names.Length == 0) return;

        try
        {
            if (_libraryLoading || _assetTagTargets.Any(target => _libraries.FindOpenLibrary(target.Library.Identity.Id) != target.Library))
                throw new InvalidOperationException("资产来源库已变更，请重新选择要添加标签的资产。");
            if (_assetTagTargets.Any(target => target.Library.Api.Database.ExistingIds(target.Ids, trash: false).Length != target.Ids.Length))
                throw new InvalidOperationException("部分选中资产已不可用，请重新选择。");
            string tags = string.Join(" ", names);
            ApplyAssetEdit(_assetTagTargets, (api, ids) => api.ChangeTags(ids, tags, add: true));
            _assetTagDialog.Hide();
            SetStatus("标签已添加");
        }
        catch (Exception error)
        {
            _assetTagError.Text = error.Message;
            _assetTagError.Show();
        }
    }
}
