using Godot;
using Animanager.Core;
using Animanager.Core.Browse;
using Animanager.Core.Jobs;
using Animanager.Core.Console;
using Animanager.Core.Models;
using Animanager.Core.Motion;
using Animanager.Core.Storage;
using Animanager.UI;
using Animanager.UI.Components;
using Animanager.UI.Controls;
using Animanager.UI.Shell;
using Animanager.UI.Workspace;
using System.Text.Json;

namespace Animanager.Tests;

public static class InterfaceTests
{
    public static async Task Run(MainShell owner,Preview3DViewport preview,WorkspaceApi api,ConsoleCommands commands)
    {
        var root=Path.Combine(api.Storage.Root,"interface-results");Directory.CreateDirectory(root);var checks=new List<string>();
        void Check(bool ok,string title){if(!ok)throw new Exception(title);checks.Add(title);GD.Print("PASS "+title);}
        async Task Frames(int count=20){for(int i=0;i<count;i++)await owner.ToSignal(owner.GetTree(),SceneTree.SignalName.ProcessFrame);}
        var host=owner.Workspace;var window=owner.GetWindow();
        void KeyPress(Key key, bool shift = false, bool ctrl = false)
        {
            using var press = new InputEventKey { Keycode = key, Pressed = true, ShiftPressed = shift, CtrlPressed = ctrl,
                Unicode = key == Key.Space && !ctrl ? ' ' : 0u };
            using var release = new InputEventKey { Keycode = key, ShiftPressed = shift, CtrlPressed = ctrl };
            window.PushInput(press, true);
            window.PushInput(release, true);
        }
        try
        {
            var layout=new WorkspaceLayout();var assetGroup=layout.GroupFor("assets")!;
            Check(layout.Move("preview",assetGroup.Id,DockEdge.Center),"panels can move into a tab group");
            Check(layout.GroupFor("preview")==layout.GroupFor("assets")&&layout.Root.AllPanels().Distinct().Count()==5,"grouping preserves exactly one instance of every visible panel");
            Check(layout.Move("preview",assetGroup.Id,DockEdge.Bottom)&&layout.GroupFor("preview")!=layout.GroupFor("assets"),"tab can undock into a vertical split");
            layout.Hide("preview");Check(layout.GroupFor("preview")==null&&layout.Hidden.Contains("preview"),"hidden panel is removed from layout without deleting content");
            layout.Show("preview");Check(layout.GroupFor("preview")!=null&&!layout.Hidden.Contains("preview"),"hidden panel can be restored");
            foreach(var panel in layout.Root.AllPanels().ToArray())layout.Hide(panel);
            Check(layout.Root.AllPanels().Count()==1&&!layout.Hide(layout.Root.AllPanels().First()),"the last remaining panel cannot be hidden");
            layout.Root=DockNode.Split("h",float.NaN,DockNode.Group("assets","unknown"),DockNode.Group("assets","preview"));layout.Normalize();
            Check(float.IsFinite(layout.Root.Ratio)&&layout.Root.AllPanels().SequenceEqual(new[]{"assets","preview"}),"invalid ratios, duplicate and unknown panels normalize safely");
            var preferences=new WorkspacePreferences{Layout=layout,Scale=1.5,SavedLayouts=new(){{"中文布局",layout.Clone()}}};var saved=Path.Combine(root,"workspace.json");preferences.Save(saved);
            var reloaded=WorkspacePreferences.Load(saved,out var error);
            Check(error.Length==0&&reloaded.Scale==1.5&&reloaded.SavedLayouts.ContainsKey("中文布局"),"named layout and scale survive a filesystem round trip");
            preferences.AssetView="List";preferences.SortField=Animanager.Core.Browse.AssetSortField.Fps;preferences.SortDescending=false;preferences.Save(saved);
            reloaded=WorkspacePreferences.Load(saved,out error);
            Check(reloaded.AssetView=="List"&&reloaded.SortField==Animanager.Core.Browse.AssetSortField.Fps&&!reloaded.SortDescending,
                "asset view and sort direction survive restart alongside legacy layout data");
            var appPreferences=Path.Combine(root,"app-settings","workspace.json");
            var migrated=WorkspacePreferences.LoadForApp(appPreferences,saved,out error);
            Check(error.Length==0&&migrated.SavedLayouts.ContainsKey("中文布局")&&File.Exists(appPreferences),"existing library layout is imported into app preferences");
            new WorkspacePreferences{Scale=2}.Save(saved);
            Check(WorkspacePreferences.LoadForApp(appPreferences,saved,out error).Scale==1.5&&error.Length==0,"app layout takes precedence over a different library's legacy preferences");
            new WorkspacePreferences{AssetView="Unknown",SortField=(Animanager.Core.Browse.AssetSortField)99}.Save(saved);
            reloaded=WorkspacePreferences.Load(saved,out error);
            Check(reloaded.AssetView=="Grid"&&reloaded.SortField==Animanager.Core.Browse.AssetSortField.Imported,"unknown asset preferences fall back to supported controls");
            var chronological = new WorkspacePreferences { SortField = AssetSortField.Imported, SortDescending = false };
            chronological.Save(saved);
            reloaded = WorkspacePreferences.Load(saved, out error);
            Check(reloaded.SortField == AssetSortField.LibraryMix && !reloaded.SortDescending, "older import-time preferences pick mixed browsing and preserve the single-library sort direction");
            chronological.AssetSortChosen = true;
            chronological.Save(saved);
            Check(WorkspacePreferences.Load(saved,out error).SortField == AssetSortField.Imported, "an explicit chronological sort remains selected on restart");
            File.WriteAllText(saved, "{\"Version\":1}");
            var cameraPreferences = WorkspacePreferences.Load(saved, out error);
            Check(error.Length == 0 && !cameraPreferences.ResetCameraOnAnimationChange, "older settings preserve the camera by default");
            cameraPreferences.ResetCameraOnAnimationChange = true;
            cameraPreferences.Save(saved);
            Check(WorkspacePreferences.Load(saved, out error).ResetCameraOnAnimationChange, "the camera reset preference survives a restart");
            File.WriteAllText(saved,"{broken");var fallback=WorkspacePreferences.Load(saved,out error);
            Check(error.Length>0&&fallback.Layout.GroupFor("assets")!=null,"damaged configuration recovers to usable defaults");
            var clamped=DisplayScaleController.ClampRect(new(new(4000,2000),new(2000,1200)),new(new(0,0),new(1280,720)));
            Check(clamped.Position==Vector2I.Zero&&clamped.Size==new Vector2I(1280,720),"window restored from disconnected monitor fits the available display");

            await api.ImportAsync([ProjectSettings.GlobalizePath("res://BuiltinAssets/Samples/StudioBot_Walk.glb")]);
            var console=owner.GetNode("/root/LimboConsole");console.Call("execute_command","preview load StudioBot_Walk");await commands.PendingTask;await Frames();
            var model=preview.Model!;var instance=model.GetInstanceId();preview.Seek(.3);
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);
            Check(!host.Compact&&host.IsPanelVisible("assets")&&host.IsPanelVisible("preview"),"desktop shell shows browsing and preview together");
            Check(host.Panel("assets").GetGlobalRect().Position.Y > host.Panel("preview").GetGlobalRect().End.Y,
                "default layout places the animation card browser beneath the central viewport");
            var buttonStyle=owner.GetThemeStylebox("normal","Button");
            var optionStyle=owner.GetThemeStylebox("normal","OptionButton");
            Check(buttonStyle.ContentMarginTop>=6&&buttonStyle.ContentMarginLeft>=12&&optionStyle.ContentMarginBottom>=6,
                "text buttons and dropdowns publish consistent content padding");
            var pluginDialog = owner.GetNode<AcceptDialog>("AnimationPluginSettings");
            var moreMenu = owner.GetChildren().OfType<PopupMenu>()
                .Single(menu => Enumerable.Range(0, menu.ItemCount).Any(i => menu.GetItemText(i) == "插件…"));
            var pluginsItem = Enumerable.Range(0, moreMenu.ItemCount).Single(i => moreMenu.GetItemText(i) == "插件…");
            foreach (var (scale, workspaceSize) in new[] { (1.5, new Vector2I(2560, 1480)), (2.0, new Vector2I(720, 400)) })
            {
                owner.DisplayScale.SetScale(scale);
                window.Size = workspaceSize;
                await Frames();
                moreMenu.EmitSignal(PopupMenu.SignalName.IdPressed, moreMenu.GetItemId(pluginsItem));
                await Frames();
                var close = pluginDialog.GetOkButton();
                var closeBounds = close.GetGlobalRect();
                var expectedHeight = Math.Min((int)(245 * scale), window.Size.Y - 60);
                Check(pluginDialog.Visible && pluginDialog.Size.X <= window.Size.X - 40 &&
                    pluginDialog.Size.Y == expectedHeight &&
                    closeBounds.Position.Y >= 0 && closeBounds.End.Y <= pluginDialog.Size.Y / scale,
                    $"plugin settings stay compact and keep Close reachable at {scale:P0} ({pluginDialog.Size})");
                if (scale == 1.5 && OperatingSystem.IsWindows())
                {
                    var body = pluginDialog.GetChildren().OfType<ScrollContainer>().Single();
                    var install = Preview3DViewport.Descendants(body).OfType<Button>().Single(button => button.Text == "安装接收脚本…");
                    var bodyBounds = body.GetGlobalRect();
                    var installBounds = install.GetGlobalRect();
                    Check(bodyBounds.Encloses(installBounds),
                        $"plugin settings show the Cascadeur setup at 150% ({bodyBounds}, install {installBounds})");
                }
                close.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                Check(!pluginDialog.Visible, $"the Close button dismisses plugin settings at {scale:P0}");
            }
            var libraryDialog=owner.GetNode<AcceptDialog>("LibraryDialog");
            var closeLibraries = (Button)libraryDialog.FindChild("CloseLibraryDialog", true, false)!;
            var libraryRows = (Tree)libraryDialog.FindChild("LibraryRows", true, false)!;
            var assignLibraryTags = (Button)libraryDialog.FindChild("AssignLibraryTags", true, false)!;
            var libraryTagManagement = (MenuButton)libraryDialog.FindChild("LibraryTagManagement", true, false)!;
            var libraryTagAssignment = libraryDialog.GetNode<AcceptDialog>("LibraryTagAssignment");
            var libraryTagCreator = libraryDialog.GetNode<AcceptDialog>("LibraryTagCreator");
            var libraryFooter = (HBoxContainer)libraryDialog.FindChild("LibraryFooter", true, false)!;
            var directoryActions = (MenuButton)libraryFooter.FindChild("LibraryDirectoryActions", true, false)!;
            var maintenanceActions = (MenuButton)libraryFooter.FindChild("LibraryMaintenanceActions", true, false)!;
            foreach(var scale in new[]{1d,2d})
            {
                owner.DisplayScale.SetScale(scale);window.Size=new(1024,700);await Frames();
                owner.FindChild("LibraryButton",true,false).EmitSignal(BaseButton.SignalName.Pressed);await Frames();
                var closeBounds = closeLibraries.GetGlobalRect();
                Check(libraryDialog.Visible && libraryDialog.Size.X <= window.Size.X && libraryDialog.Size.Y <= window.Size.Y &&
                    closeBounds.End.X <= libraryDialog.Size.X / scale && closeBounds.End.Y <= libraryDialog.Size.Y / scale &&
                    libraryRows.GetGlobalRect().End.Y < libraryFooter.GlobalPosition.Y &&
                    directoryActions.GetGlobalRect().End.X <= maintenanceActions.GlobalPosition.X &&
                    maintenanceActions.GetGlobalRect().End.X < closeBounds.Position.X,
                    $"library table and footer actions fit within the window at {scale:0}x scale");
                assignLibraryTags.EmitSignal(BaseButton.SignalName.Pressed);
                await Frames();
                var tagDoneBounds = libraryTagAssignment.GetOkButton().GetGlobalRect();
                Check(libraryTagAssignment.Visible && libraryTagAssignment.Size.X <= window.Size.X && libraryTagAssignment.Size.Y <= window.Size.Y &&
                    tagDoneBounds.End.X <= libraryTagAssignment.Size.X / scale && tagDoneBounds.End.Y <= libraryTagAssignment.Size.Y / scale,
                    $"library tag assignment and done button fit within the window at {scale:0}x scale " +
                    $"(window {window.Size}, dialog {libraryTagAssignment.Size}, done {tagDoneBounds})");
                libraryTagAssignment.Hide();
                closeLibraries.EmitSignal(BaseButton.SignalName.Pressed);await Frames();
                Check(!libraryDialog.Visible, $"the footer closes the library dialog at {scale:0}x scale");
            }
            owner.FindChild("LibraryButton", true, false).EmitSignal(BaseButton.SignalName.Pressed);
            var directoryMenu = directoryActions.GetPopup();
            var createLibraryIndex = Enumerable.Range(0, directoryMenu.ItemCount).Single(i => directoryMenu.GetItemText(i) == "新建库…");
            directoryMenu.EmitSignal(PopupMenu.SignalName.IndexPressed, createLibraryIndex);
            await Frames();
            var createLibraryDialog = owner.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog.Title == "新建空库");
            Check(createLibraryDialog.Visible && !libraryDialog.Visible, "the directory menu opens the existing new-library form");
            createLibraryDialog.Hide();

            var activeLibrary = owner.Libraries.Active!;
            var originalEntry = owner.Libraries.Entries().Single(entry => entry.Id == activeLibrary.Identity.Id);
            owner.FindChild("LibraryButton", true, false).EmitSignal(BaseButton.SignalName.Pressed);
            var renameLibraryIndex = Enumerable.Range(0, directoryMenu.ItemCount).Single(i => directoryMenu.GetItemText(i) == "重命名所选库…");
            directoryMenu.EmitSignal(PopupMenu.SignalName.IndexPressed, renameLibraryIndex);
            var renameLibraryDialog = owner.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog.Title == "重命名资产库");
            var libraryNameField = Preview3DViewport.Descendants(renameLibraryDialog).OfType<LineEdit>().Single(field => field.Visible);
            Check(renameLibraryDialog.Visible && libraryNameField.Text == originalEntry.Name, "the library rename form starts with the current display name");
            const string renamedLibrary = "动作测试库";
            libraryNameField.Text = renamedLibrary;
            renameLibraryDialog.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse; await Frames();
            Check(libraryDialog.Visible && libraryRows.GetSelected().GetText(0) == renamedLibrary &&
                LibraryManager.ReadIdentity(activeLibrary.Root) == activeLibrary.Identity && activeLibrary.Identity.Name == renamedLibrary &&
                owner.Libraries.Entries().Single(entry => entry.Id == originalEntry.Id) == (originalEntry with { Name = renamedLibrary }) &&
                owner.Libraries.Active == activeLibrary && preview.Model!.GetInstanceId() == instance,
                "renaming the working library updates its row and stored name without reopening its session or preview");
            libraryDialog.Hide();
            owner.DisplayScale.SetScale(1.25);
            window.Size = new(650, 550);
            await Frames();
            if (DisplayServer.GetName() == "headless") window.NotifyMouseEntered();
            var dualPack = owner.Libraries.Create(api.Storage.Root + "-tag-dual", "筛选双刀测试库");
            var katanaPack = owner.Libraries.Create(api.Storage.Root + "-tag-katana", "筛选太刀测试库");
            var initialTagCatalog = owner.Libraries.BrowseCatalog();
            Check(initialTagCatalog.Tags.Length == 0 && initialTagCatalog.Groups.Length == 0,
                "The library tag catalog starts empty with no predefined groups");
            foreach (var name in new[] { "太刀", "双刀", "高质量" }) owner.Libraries.CreateTag(name);
            owner.Libraries.ChangeTag(katanaPack.Id, new("太刀"), add: true);
            owner.FindChild("LibraryButton", true, false).EmitSignal(BaseButton.SignalName.Pressed);
            var librarySearch = (LineEdit)libraryDialog.FindChild("LibrarySearch", true, false)!;
            var clearLibraryFilters = (Button)libraryDialog.FindChild("ClearLibraryFilters", true, false)!;
            void SearchLibraries(string query)
            {
                librarySearch.Text = query;
                librarySearch.EmitSignal(LineEdit.SignalName.TextChanged, query);
            }
            string[] VisibleLibraryIds() => libraryRows.GetRoot().GetChildren().Select(item => item.GetMetadata(0).AsString()).ToArray();
            var filterChips = (HFlowContainer)libraryDialog.FindChild("LibraryTagFilterChips", true, false)!;
            var groupFilter = (OptionButton)libraryDialog.FindChild("LibraryTagGroupFilter", true, false)!;
            Button[] LibraryTagChips() => filterChips.GetChildren().OfType<Button>().ToArray();
            void ClickTag(string tag) => LibraryTagChips().Single(chip => chip.TooltipText == tag).EmitSignal(BaseButton.SignalName.Pressed);
            void OpenLibraryTagMenu(string label)
            {
                libraryTagManagement.EmitSignal(MenuButton.SignalName.AboutToPopup);
                var popup = libraryTagManagement.GetPopup();
                var index = Enumerable.Range(0, popup.ItemCount).Single(i => popup.GetItemText(i) == label);
                popup.EmitSignal(PopupMenu.SignalName.IndexPressed, index);
            }
            SearchLibraries("不存在的库");
            Check(!assignLibraryTags.Disabled && !libraryTagManagement.Disabled && !groupFilter.Visible,
                "The tag manager and standalone creator are available without a selected library");
            OpenLibraryTagMenu("新建库标签…");
            var tagName = (LineEdit)libraryTagCreator.FindChild("NewLibraryTagName", true, false)!;
            tagName.Text = " 双太刀 ";
            libraryTagCreator.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            Check(!libraryTagCreator.Visible && owner.Libraries.BrowseCatalog().Tags.Contains(new("双太刀")) &&
                owner.Libraries.Entries().Single(entry => entry.Id == dualPack.Id).Tags == null,
                "Creating a library tag in its own dialog does not assign it to a library");
            OpenLibraryTagMenu("新建标签组…");
            var groupEditor = libraryDialog.GetNode<AcceptDialog>("LibraryTagGroupEditor");
            var groupName = (LineEdit)groupEditor.FindChild("LibraryTagGroupName", true, false)!;
            var memberSearch = (LineEdit)groupEditor.FindChild("LibraryTagGroupSearch", true, false)!;
            var members = (Tree)groupEditor.FindChild("LibraryTagGroupMembers", true, false)!;
            groupName.Text = "刀剑包";
            members.GetRoot().GetChildren().Single(row => row.GetText(0) == "双刀").SetChecked(0, true);
            memberSearch.Text = "太刀";
            memberSearch.EmitSignal(LineEdit.SignalName.TextChanged, memberSearch.Text);
            members.GetRoot().GetChildren().Single(row => row.GetText(0) == "太刀").SetChecked(0, true);
            groupEditor.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            var userGroup = owner.Libraries.BrowseCatalog().Groups.Single();
            Check(!groupEditor.Visible && userGroup.Name == "刀剑包" && userGroup.Tags.ToHashSet().SetEquals(["双刀", "太刀"]),
                "A user-defined tag group preserves selected members when searching its tag list");
            SearchLibraries(dualPack.Name);
            var dualRow = libraryRows.GetRoot().GetChildren().Single();
            dualRow.Select(0);
            libraryRows.EmitSignal(Tree.SignalName.ItemSelected);
            assignLibraryTags.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames();
            if (DisplayServer.GetName() == "headless" && !libraryTagAssignment.IsEmbedded()) libraryTagAssignment.NotifyMouseEntered();
            var available = (LibraryTagLane)libraryTagAssignment.FindChild("AvailableLibraryTags", true, false)!;
            var assigned = (LibraryTagLane)libraryTagAssignment.FindChild("AssignedLibraryTags", true, false)!;
            LibraryTagChip TagChip(LibraryTagLane lane, string name) => Preview3DViewport.Descendants(lane).OfType<LibraryTagChip>()
                .Single(chip => chip.Tag.Name == name);
            async Task DragLibraryTag(LibraryTagLane from, string name, Control to, Vector2? destination = null)
            {
                var chip = TagChip(from, name);
                var scroll = Preview3DViewport.Descendants(from).OfType<ScrollContainer>().Single();
                scroll.EnsureControlVisible(chip);
                await Frames(2);
                var start = chip.GetGlobalRect().GetCenter();
                var beginAt = start + new Vector2(0, 16);
                var end = destination ?? to.GetGlobalRect().GetCenter();
                // The embedder resolves the drop target for embedded dialogs.
                var receiver = libraryTagAssignment.IsEmbedded() ? window : libraryTagAssignment;
                var transform = receiver.GetScreenTransform().AffineInverse() * libraryTagAssignment.GetScreenTransform();
                var inputTransform = receiver.GetFinalTransform() * transform;
                var windowId = receiver.GetWindowId();
                InputEventMouseMotion Motion(Vector2 position, Vector2 previous, MouseButtonMask mask) => new()
                {
                    WindowId = windowId, Position = inputTransform * position, GlobalPosition = inputTransform * position,
                    Relative = (inputTransform * position) - (inputTransform * previous), ButtonMask = mask
                };
                InputEventMouseButton Click(Vector2 position, bool pressed) => new()
                {
                    WindowId = windowId, Position = inputTransform * position, GlobalPosition = inputTransform * position,
                    ButtonIndex = MouseButton.Left, ButtonMask = pressed ? MouseButtonMask.Left : 0, Pressed = pressed
                };
                void Send(InputEventMouse mouse)
                {
                    if (DisplayServer.GetName() != "headless") receiver.WarpMouse(receiver.GetFinalTransform().AffineInverse() * mouse.Position);
                    Input.ParseInputEvent(mouse);
                    Input.FlushBufferedEvents();
                }
                using var move = Motion(start, start, 0);
                using var press = Click(start, true);
                using var begin = Motion(beginAt, start, MouseButtonMask.Left);
                using var travel = Motion(end, beginAt, MouseButtonMask.Left);
                using var release = Click(end, false);
                Send(move);
                Send(press);
                Send(begin);
                await Frames(2);
                if (!libraryTagAssignment.GuiIsDragging()) throw new InvalidOperationException(
                    $"Cannot start dragging library tag {name} at {start}. Focus: {libraryTagAssignment.GuiGetFocusOwner()?.Name}. " +
                    $"Dialog: {libraryTagAssignment.Size}, receiver: {receiver.Name}, transform: {transform}, lane: {from.GetGlobalRect()}.");
                Send(travel);
                await Frames(2);
                var hovered = libraryTagAssignment.GuiGetHoveredControl();
                Send(release);
                await Frames(4);
                if (!libraryTagAssignment.GuiIsDragSuccessful()) throw new InvalidOperationException(
                    $"Cannot drop library tag {name} at {end}. Hovered: {hovered?.GetPath()}. Actual mouse: {to.GetLocalMousePosition()}, " +
                    $"receiver: {receiver.Name}, transform: {transform}.");
            }
            await DragLibraryTag(available, "双太刀", assigned);
            Check(TagChip(assigned, "双太刀").Visible && owner.Libraries.Entries().Single(entry => entry.Id == dualPack.Id).Tags!
                .Contains(new("双太刀")), "Dragging a custom tag to the assigned lane saves it on the selected closed library");
            await DragLibraryTag(assigned, "双太刀", available);
            Check(TagChip(available, "双太刀").Visible && owner.Libraries.Entries().Single(entry => entry.Id == dualPack.Id).Tags == null,
                "Dragging an assigned tag back removes it while keeping it available for reuse");
            await DragLibraryTag(available, "双刀", assigned);
            await DragLibraryTag(available, "高质量", assigned);
            var boardLibraries = (Tree)libraryTagAssignment.FindChild("TagBoardLibraries", true, false)!;
            var boardSearch = (LineEdit)libraryTagAssignment.FindChild("TagBoardLibrarySearch", true, false)!;
            Check(boardLibraries.GetSelected().GetMetadata(0).AsString() == dualPack.Id &&
                boardLibraries.GetRoot().GetChildren().Any(row => row.GetMetadata(0).AsString() == katanaPack.Id),
                "The drag manager starts on the chosen library and lists other registered libraries even when the main list is filtered");
            boardSearch.Text = katanaPack.Name;
            boardSearch.EmitSignal(LineEdit.SignalName.TextChanged, boardSearch.Text);
            await Frames();
            var katanaBoardRow = boardLibraries.GetRoot().GetFirstChild();
            float rowX = boardLibraries.GetItemAreaRect(katanaBoardRow, 0).GetCenter().X;
            var rowHits = Enumerable.Range(0, (int)boardLibraries.Size.Y)
                .Where(y => boardLibraries.GetItemAtPosition(new Vector2(rowX, y)) == katanaBoardRow).ToArray();
            Check(rowHits.Length > 0, "Library search leaves the matching row visible and available as a drop target");
            var katanaDropPoint = boardLibraries.GetGlobalRect().Position + new Vector2(rowX, rowHits[rowHits.Length / 2]);
            await DragLibraryTag(assigned, "双刀", boardLibraries, katanaDropPoint);
            Check(katanaBoardRow.GetMetadata(0).AsString() == katanaPack.Id && katanaBoardRow.GetText(1).Contains("双刀") &&
                TagChip(assigned, "双刀").Visible, "Dropping an assigned tag directly onto another library copies it to the intended row");
            katanaBoardRow.Select(0);
            boardLibraries.EmitSignal(Tree.SignalName.ItemSelected);
            await Frames();
            Check(libraryTagAssignment.Visible && TagChip(assigned, "太刀").Visible,
                "Selecting another library switches the tag lanes in the same window");
            await DragLibraryTag(assigned, "双刀", available);
            Check(!katanaBoardRow.GetText(1).Contains("双刀") && owner.Libraries.Entries().Single(entry => entry.Id == dualPack.Id).Tags!
                .Contains(new("双刀")), "Removing the copied tag only changes the currently selected library");
            libraryTagAssignment.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            var savedRow = libraryRows.GetSelected();
            Check(!libraryTagAssignment.Visible && libraryDialog.Visible && savedRow.GetMetadata(0).AsString() == dualPack.Id &&
                savedRow.GetText(4).Contains("双刀") && savedRow.GetText(4).Contains("高质量") &&
                owner.Libraries.FindOpenLibrary(dualPack.Id) == null,
                "The filtered library row displays the tags assigned through drag and drop");

            clearLibraryFilters.EmitSignal(BaseButton.SignalName.Pressed);
            ClickTag("高质量");
            Check(VisibleLibraryIds().SequenceEqual([dualPack.Id]), "A visible tag chip immediately filters registered libraries");
            ClickTag("高质量");
            Check(VisibleLibraryIds().Contains(katanaPack.Id), "Clicking the selected tag again clears the tag filter");
            groupFilter.Select(1);
            groupFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 1);
            Check(VisibleLibraryIds().ToHashSet().SetEquals([dualPack.Id, katanaPack.Id]) &&
                LibraryTagChips().Select(chip => chip.TooltipText).ToHashSet().SetEquals(["双刀", "太刀"]),
                "Selecting a user-defined group shows its tags and libraries carrying any of its members");
            ClickTag("双刀");
            Check(VisibleLibraryIds().SequenceEqual([dualPack.Id]), "Clicking a tag in the selected group filters to that tag");
            SearchLibraries(katanaPack.Name);
            Check(VisibleLibraryIds().Length == 0 && libraryRows.GetSelected() == null,
                "Library name search combines with tag filters and clears hidden selections");
            SearchLibraries("");
            OpenLibraryTagMenu("编辑组「刀剑包」…");
            groupName.Text = "近战包";
            members.GetRoot().GetChildren().Single(row => row.GetText(0) == "太刀").SetChecked(0, false);
            groupEditor.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            Check(groupFilter.GetItemText(groupFilter.Selected) == "近战包" && LibraryTagChips().Select(chip => chip.TooltipText).SequenceEqual(["双刀"]),
                "Editing the active group updates its name and visible members without reselecting it");
            OpenLibraryTagMenu("编辑组「近战包」…");
            groupEditor.EmitSignal(AcceptDialog.SignalName.CustomAction, "delete_group");
            var deleteLibraryTagGroup = groupEditor.GetChildren().OfType<ConfirmationDialog>().Single();
            Check(deleteLibraryTagGroup.Visible, "Deleting a user-defined tag group asks for confirmation");
            deleteLibraryTagGroup.GetOkButton().EmitSignal(BaseButton.SignalName.Pressed);
            Check(!groupFilter.Visible && !groupEditor.Visible && VisibleLibraryIds().SequenceEqual([dualPack.Id]) &&
                LibraryTagChips().Any(chip => chip.TooltipText == "太刀"), "Deleting the active group restores all tag choices and retains the clicked tag filter");
            clearLibraryFilters.EmitSignal(BaseButton.SignalName.Pressed);
            libraryDialog.Hide();
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);

            var dock=host.Docking;
            var inputViewport=owner.GetViewport();
            async Task Drag(Vector2 from,Vector2 to,bool cancel=false,Action? duringDrag=null)
            {
                if(DisplayServer.GetName()!="headless")
                {window.GrabFocus();await Frames(4);Input.WarpMouse(from*window.ContentScaleFactor);await Frames(4);Input.FlushBufferedEvents();}
                using var move=new InputEventMouseMotion{Position=from,GlobalPosition=from};Input.ParseInputEvent(move);Input.FlushBufferedEvents();
                using var press=new InputEventMouseButton{Position=from,GlobalPosition=from,ButtonIndex=MouseButton.Left,Pressed=true};Input.ParseInputEvent(press);Input.FlushBufferedEvents();
                await Frames(2);
                using var begin=new InputEventMouseMotion{Position=from+new Vector2(18,0),GlobalPosition=from+new Vector2(18,0),Relative=new(18,0),ButtonMask=MouseButtonMask.Left};Input.ParseInputEvent(begin);Input.FlushBufferedEvents();
                await Frames(2);
                if(DisplayServer.GetName()!="headless")
                {Input.WarpMouse(to*window.ContentScaleFactor);await Frames(2);Input.FlushBufferedEvents();}
                using var travel=new InputEventMouseMotion{Position=to,GlobalPosition=to,Relative=to-from-new Vector2(18,0),ButtonMask=MouseButtonMask.Left};Input.ParseInputEvent(travel);Input.FlushBufferedEvents();
                await Frames(2);
                duringDrag?.Invoke();
                if(cancel)
                {
                    using var escape=new InputEventKey{Keycode=Key.Escape,Pressed=true};Input.ParseInputEvent(escape);Input.FlushBufferedEvents();
                    using var escapeUp=new InputEventKey{Keycode=Key.Escape,Pressed=false};Input.ParseInputEvent(escapeUp);Input.FlushBufferedEvents();
                }
                using var release=new InputEventMouseButton{Position=to,GlobalPosition=to,ButtonIndex=MouseButton.Left,Pressed=false};Input.ParseInputEvent(release);Input.FlushBufferedEvents();
                await Frames(20);
            }
            var sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            var targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            await Drag(sourceTab.GetGlobalRect().GetCenter(),targetGroup.GetGlobalRect().GetCenter());
            Check(dock.Layout.GroupFor("assets")==dock.Layout.GroupFor("preview"),"dragging the asset tab into the viewport center joins its tab group");
            sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            var targetRect=targetGroup.GetGlobalRect();
            await Drag(sourceTab.GetGlobalRect().GetCenter(),new(targetRect.GetCenter().X,targetRect.End.Y-18));
            Check(dock.Layout.GroupFor("assets")!=dock.Layout.GroupFor("preview")&&host.IsPanelVisible("preview")&&host.IsPanelVisible("assets"),
                "dragging to a panel edge creates a separate dock region");
            Check(preview.Model!.GetInstanceId()==instance&&Math.Abs(preview.Playhead-.3)<.01,"dragging and regrouping preserve the loaded model and playhead");
            host.RestorePanels();await Frames();
            var splitter=Preview3DViewport.Descendants(dock).OfType<RatioSplit>().Single(s=>s.Data==dock.Layout.Root);
            var topPane=splitter.GetChild<Control>(0);float oldHeight=topPane.Size.Y;
            var handle=splitter.GlobalPosition+new Vector2(splitter.Size.X*.5f,topPane.Size.Y+4);
            await Drag(handle,handle-new Vector2(0,40));
            Check(topPane.Size.Y<oldHeight-20&&Math.Abs(dock.Layout.Root.Ratio-.56f)>.02,
                "dragging the horizontal splitter resizes the browser and stores the new ratio");
            var verticalSplitter=Preview3DViewport.Descendants(dock).OfType<RatioSplit>().Single(s=>s.Data==dock.Layout.Root.First);
            var leftPane=verticalSplitter.GetChild<Control>(0);float oldWidth=leftPane.Size.X;
            var verticalHandle=verticalSplitter.GlobalPosition+new Vector2(leftPane.Size.X+4,verticalSplitter.Size.Y*.5f);
            await Drag(verticalHandle,verticalHandle+new Vector2(50,0));
            Check(leftPane.Size.X>oldWidth+30,"dragging a vertical splitter changes the side panel width");
            var dockFile=Path.Combine(root,"component-docking.json");new WorkspacePreferences{DockLayout=dock.Layout.Clone()}.Save(dockFile);
            var savedDock=WorkspacePreferences.Load(dockFile,out error).DockLayout!;
            Check(error.Length==0&&JsonSerializer.Serialize(savedDock.Root)==JsonSerializer.Serialize(dock.Layout.Root),"dock positions and split ratios survive a preference round trip");
            var desktopTree=JsonSerializer.Serialize(dock.Layout.Root);
            sourceTab=dock.Groups.SelectMany(g=>g.Tabs).Single(t=>t.PanelId=="assets");
            targetGroup=dock.Groups.Single(g=>g.Data.Panels.Contains("preview"));
            await Drag(sourceTab.GetGlobalRect().GetCenter(),targetGroup.GetGlobalRect().GetCenter(),cancel:true);
            Check(!dock.DragActive&&JsonSerializer.Serialize(dock.Layout.Root)==desktopTree,"Escape cancels docking without changing the layout");
            window.Size=new(720,400);await Frames(30);
            Check(dock.Compact&&JsonSerializer.Serialize(dock.Layout.Root)==desktopTree,"compact navigation preserves the desktop docking tree");
            window.Size=new(1440,900);host.RestorePanels();await Frames(30);
            host.ShowPanel("bones");await Frames();
            Check(host.ActiveTool=="bones"&&host.IsPanelVisible("bones"),"bone tools open in the right tab");
            host.ShowPanel("export");host.ShowPanel("retarget");await Frames();
            Check(host.ActiveDrawer==""&&host.IsPanelVisible("retarget"),"opening retarget tools closes the export drawer that would cover them");
            Check(preview.Model!.GetInstanceId()==instance&&Math.Abs(preview.Playhead-.3)<.01,"tool switching preserves model and animation position");
            host.FocusPanel("preview");await Frames();Check(host.IsPanelVisible("preview")&&!host.IsPanelVisible("assets"),"preview focus uses the workspace");
            host.RestorePanels();await Frames();Check(host.IsPanelVisible("assets"),"restore returns to browsing");
            host.HidePanel("preview");await Frames();Check(!host.IsPanelVisible("preview")&&preview.View.RenderTargetUpdateMode==SubViewport.UpdateMode.Disabled,"hidden preview stops requesting redraws");
            host.ShowPanel("preview");await Frames();Check(preview.Model!.GetInstanceId()==instance,"restoring preview reuses its scene");
            window.Size=new(720,400);await Frames(30);Check(host.Compact,"720 x 400 uses compact navigation");
            var openLibrariesMenu = (MenuButton)host.Panel("assets").FindChild("OpenLibrariesMenu", true, false)!;
            foreach(var id in new[]{"assets","preview","inspector","bones","retarget","export","jobs","filters"})
            {
                host.CloseDrawer();host.ShowPanel(id);await Frames();
                Check(host.IsPanelVisible(id)&&host.Panel(id).Size.X>100,"small window reaches panel: "+id);
                if (id == "assets") Check(openLibrariesMenu.IsVisibleInTree() && openLibrariesMenu.GetPopup().GetItemText(0) == renamedLibrary,
                    "compact asset browser keeps the open-library menu accessible");
            }
            host.RestorePanels();window.Size=new(1440,900);await Frames(30);Check(!host.Compact,"expanding restores desktop panels");
            foreach(var scale in new[]{1.25,1.5,1.75,2.0})
            {
                owner.DisplayScale.SetScale(scale);await Frames(30);
                Check(Math.Abs(window.ContentScaleFactor-scale)<.001&&owner.Size.X<=window.Size.X/scale+2,"UI scale is applied once at "+scale);
                Check(host.Size.X>300&&host.Panel("assets").Size.X>100,"scaled layout keeps reachable panel bounds at "+scale);
            }
            var search=(LineEdit)owner.FindChild("AssetSearch",true,false)!;
            host.ShowPanel("assets");await Frames();search.GrabFocus();commands.Open();await Frames(45);
            Check(console.Call("is_open").AsBool(),"Limbo Console remains available at 200% scale");
            console.Call("close_console");await Frames(45);Check(search.HasFocus(),"closing Console restores the original input focus");
            search.Text="中文名称 空格";search.CaretColumn=search.Text.Length;
            KeyPress(Key.Space);
            Check(!preview.Playing && search.Text == "中文名称 空格 ", "Space types in the search field without playing the preview");
            owner.DisplayScale.SetScale(1);window.Size=new(1440,900);host.RestorePanels();await Frames(30);

            // Real containers and input signals exercise view layout, independent scroll memory and range selection.
            var browserProbe=new Animanager.UI.Shell.AssetBrowser{Position=new(100,100),Size=new(500,300)};
            owner.AddChild(browserProbe);
            browserProbe.SetRows(Enumerable.Range(0,100).Select(i=>new Animanager.UI.Shell.AssetCardModel{Id="probe-"+i,Name="Asset "+i}));
            await Frames();browserProbe.Scroll.ScrollVertical=700;await Frames();
            browserProbe.Selection.Select("probe-3");
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();browserProbe.Scroll.ScrollVertical=1100;await Frames();
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.Grid);await Frames();
            Check(browserProbe.Scroll.ScrollVertical==700&&browserProbe.Selection.Anchor=="probe-3","returning to grid restores its scroll and selection anchor");
            browserProbe.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();
            Check(browserProbe.Scroll.ScrollVertical==1100,"returning to list restores its independent scroll position");
            browserProbe.List.GrabFocus();
            KeyPress(Key.Down);
            KeyPress(Key.Down, shift: true);
            Check(browserProbe.Selection.Order().SequenceEqual(new[]{"probe-4","probe-5"}),"keyboard arrows and Shift extend the shared selection");
            var opened = new List<string>();
            browserProbe.Activated += activation =>
            {
                if (activation.OpenRequested) opened.Add(browserProbe.Row(activation.Index)!.Id);
            };
            browserProbe.SetRows(browserProbe.Rows.Reverse());
            KeyPress(Key.KpEnter);
            Check(opened.SequenceEqual(["probe-5"]) && browserProbe.Selection.Count == 2,
                "keypad Enter opens the current row after a reorder, preserving the range selection");
            browserProbe.SetMode(AssetBrowser.ViewMode.Grid); await Frames();
            Check(browserProbe.Grid.HasFocus(), "switching focused asset views keeps keyboard focus on the results");
            KeyPress(Key.Up);
            Check(browserProbe.Selection.Anchor == $"probe-{5 + browserProbe.Grid.Columns}", "grid Up moves by one row");
            KeyPress(Key.Down);
            KeyPress(Key.Left);
            Check(browserProbe.Selection.Anchor == "probe-6", "grid Left moves by one card");
            KeyPress(Key.Right);
            Check(browserProbe.Selection.Ids.SequenceEqual(["probe-5"]) && opened.Count == 1,
                "grid Down and Right move the selection without opening a card");
            KeyPress(Key.Space, ctrl: true);
            Check(browserProbe.Selection.IsEmpty && !preview.Playing, "Control+Space toggles the selected asset without playing the preview");
            browserProbe.QueueFree();await Frames();

            search.Text="";
            await api.ImportAsync([RuntimePaths.Bundled("BuiltinAssets/Samples")]);
            var rig = new AssetItem { Name="StudioBot_Rig", FileHash="legacy-static-rig", SourceFormat=".glb",
                VirtualPath="assets/legacy-static-rig/StudioBot_Rig.glb" };
            await api.Storage.PutFileAsync(RuntimePaths.Bundled("BuiltinAssets/Samples/StudioBot_Rig.glb"),rig.VirtualPath);
            api.Database.Insert(rig);
            commands.Load("StudioBot_Walk");await commands.PendingTask;
            host.ShowPanel("assets");await Frames(30);
            var rows=(Animanager.UI.Shell.AssetBrowser)host.Panel("assets").FindChild("AssetBrowser",true,false)!;
            rows.SetMode(Animanager.UI.Shell.AssetBrowser.ViewMode.List);await Frames();
            preview.Seek(.3);
            var original = preview.Model;
            var walk = api.Find("StudioBot_Walk");
            var run = api.Find("StudioBot_Run");
            var idle = api.Find("StudioBot_Idle");
            await owner.PendingBrowse;
            var locatePreview = preview.LocateAssetButton;
            var previewLocateMenu = (PopupMenu)preview.FindChild("PreviewLocateMenu", true, false)!;
            search.Text = idle.Name;
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            await owner.PendingBrowse;
            rows.Selection.Select(idle.Id);
            locatePreview.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(locatePreview.IsVisibleInTree() && !previewLocateMenu.Visible && search.Text == idle.Name &&
                rows.IndexOf(walk.Id) < 0 && owner.SelectedAssetIds.SequenceEqual([idle.Id]) && preview.Model == original,
                "locating a filtered-out preview keeps the search, selection and shown model");
            search.Text = walk.Name;
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            await owner.PendingBrowse;
            host.FocusPanel("preview");
            await Frames();
            locatePreview.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(host.IsPanelVisible("assets") && owner.SelectedAssetIds.SequenceEqual([walk.Id]) &&
                rows.VisibleIndices().Contains(rows.IndexOf(walk.Id)) && search.Text == walk.Name &&
                preview.Model == original && Math.Abs(preview.Playhead - .3) < .01,
                "the locate button reveals the open animation in filtered results while preserving its playhead");
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            await owner.PendingBrowse;
            rows.SetMode(AssetBrowser.ViewMode.Grid); await Frames();
            var page = rows.Rows;
            var stage = (PreviewStage)preview.GetParent();
            var resetCamera = stage.ResetCameraToggle;
            var settings = (Button)preview.FindChild("ViewportSettings", true, false)!;
            settings.EmitSignal(BaseButton.SignalName.Pressed);
            Check(stage.SettingsDialog.Visible && resetCamera.IsVisibleInTree() && !resetCamera.ButtonPressed,
                "viewport settings show the camera reset toggle turned off by default");
            stage.SettingsDialog.Hide();
            void AdjustCamera()
            {
                preview.SnapAxis(0);
                preview.Navigate("orbit", new(21, -15));
                preview.FocusPoint(new(2, 1, 3));
                preview.Zoom(.5f);
            }
            (Vector3 Target, float Distance, Basis Basis, bool Orthographic) Camera() =>
                (preview.CameraTarget, preview.CameraDistance, preview.CameraBasis, preview.Orthographic);
            bool originalProjection = preview.Orthographic;
            AdjustCamera();
            var adjustedCamera = Camera();
            void ClickCard(string id, bool doubleClick = false)
            {
                int index = rows.IndexOf(id);
                if (index < 0) throw new InvalidOperationException("Preview fixture is missing from the grid: " + id);
                using var click = new InputEventMouseButton
                {
                    Position = rows.RowRect(index).GetCenter(), ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = doubleClick
                };
                rows.ActiveView._GuiInput(click);
                click.Pressed = false;
                rows.ActiveView._GuiInput(click);
            }
            var beforeClick = owner.PendingAssetPreview;
            ClickCard(run.Id);
            Check(owner.SelectedAssetIds.SequenceEqual([run.Id]) && owner.PendingAssetPreview == beforeClick && preview.Model == original,
                "a single grid click selects without opening a preview");
            ClickCard(run.Id, doubleClick: true);
            var pendingRun = owner.PendingAssetPreview;
            Check(owner.SelectedAssetIds.SequenceEqual(new[] { run.Id }) && !pendingRun.IsCompleted && preview.Model == original,
                "a double-click starts the preview while the previous scene remains visible");
            ClickCard(idle.Id, doubleClick: true);
            await Task.WhenAll(pendingRun, owner.PendingAssetPreview);
            Check(commands.SelectedId == idle.Id && preview.CurrentClip == "StudioBot_Idle" && ReferenceEquals(rows.Rows, page),
                "rapid openings show the latest animation without replacing the browser rows");
            Check(Camera() == adjustedCamera, "switching animation cards preserves the viewing angle, pan, zoom and projection");
            resetCamera.ButtonPressed = true;
            Check(Camera() == adjustedCamera, "enabling camera reset leaves the current view in place");
            ClickCard(run.Id, doubleClick: true);
            await owner.PendingAssetPreview;
            Check(preview.CurrentClip == "StudioBot_Run" && Camera() != adjustedCamera && preview.CameraTarget != adjustedCamera.Target,
                "switching animation cards reframes the preview when camera reset is enabled");

            rows.SetMode(AssetBrowser.ViewMode.List); await Frames();
            int idleIndex = rows.IndexOf(idle.Id);
            rows.SelectIndex(idleIndex > 0 ? idleIndex - 1 : 1);
            rows.List.GrabFocus();
            KeyPress(idleIndex > 0 ? Key.Down : Key.Up);
            Check(owner.SelectedAssetIds.SequenceEqual([idle.Id]) && preview.CurrentClip == "StudioBot_Run",
                "an arrow key selects the next list item without replacing the preview");
            KeyPress(Key.Enter);
            await owner.PendingAssetPreview;
            Check(preview.CurrentClip == "StudioBot_Idle" && rows.List.HasFocus(), "Enter opens the selected animation and keeps browsing focus");
            KeyPress(Key.Space);
            Check(preview.TransportPlaying && owner.SelectedAssetIds.SequenceEqual([idle.Id]), "Space plays the animation while the asset list has focus");
            KeyPress(Key.Space);
            Check(!preview.TransportPlaying, "pressing Space again pauses the animation");
            rows.SetMode(AssetBrowser.ViewMode.Grid); await Frames();

            var multiClip = new Node3D();
            multiClip.AddChild(new MeshInstance3D { Mesh = new BoxMesh() });
            var multiPlayer = new AnimationPlayer();
            var clips = new AnimationLibrary();
            clips.AddAnimation("First", new Animation());
            clips.AddAnimation("Second", new Animation());
            multiPlayer.AddAnimationLibrary("", clips);
            multiClip.AddChild(multiPlayer);
            preview.AttachModel(multiClip);
            AdjustCamera();
            adjustedCamera = Camera();
            preview.SelectClip("Second");
            Check(preview.CurrentClip == "Second" && preview.CameraTarget != adjustedCamera.Target,
                "switching clips in one asset also reframes with camera reset enabled");
            resetCamera.ButtonPressed = false;
            AdjustCamera();
            adjustedCamera = Camera();
            preview.SelectClip("First");
            Check(preview.CurrentClip == "First" && Camera() == adjustedCamera, "switching clips preserves the camera with camera reset off");
            preview.FrameModel();
            Check(preview.CameraTarget != adjustedCamera.Target, "manual focus still reframes with camera reset off");
            if (preview.Orthographic != originalProjection) preview.ToggleProjection();
            await owner.PreviewAssetAsync(walk);
            preview.Seek(.3);
            original = preview.Model;
            var readyPreview = owner.PendingAssetPreview;
            ClickCard(walk.Id, doubleClick: true);
            Check(owner.PendingAssetPreview == readyPreview && preview.Model == original && Math.Abs(preview.Playhead - .3) < .01,
                "double-clicking the open card preserves its loaded scene and playhead");
            var exitCompare = (Button)preview.FindChild("ExitAnimationComparison", true, false)!;
            var openComparison = (Button)preview.FindChild("OpenComparisonOrder", true, false)!;
            var orderPanel = (Control)preview.FindChild("ComparisonOrder", true, false)!;
            var orderRows = (VBoxContainer)orderPanel.FindChild("ComparisonOrderList", true, false)!;
            void RemoveComparisonRow(int index) => orderRows.GetChild(index).FindChild("RemoveComparisonRow", true, false)!
                .EmitSignal(BaseButton.SignalName.Pressed);
            async Task DragComparisonRow(int source, int target, bool after)
            {
                await Frames();
                var from = orderRows.GetChild<Control>(source).GetGlobalRect();
                var to = orderRows.GetChild<Control>(target).GetGlobalRect();
                await Drag(new(from.Position.X + 32, from.GetCenter().Y),
                    new(to.Position.X + 32, to.Position.Y + to.Size.Y * (after ? .75f : .25f)));
            }
            var dropHint = (Control)preview.FindChild("ComparisonDropHint", true, false)!;
            var context=(PopupMenu)owner.FindChild("AssetContextMenu",true,false)!;

            ((Button)owner.FindChild("EditOverviewWeaponProfile", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
            var weaponDialog = (AcceptDialog)owner.FindChild("WeaponProfiles", true, false)!;
            var weaponPages = (TabContainer)weaponDialog.FindChild("WeaponProfilePages", true, false)!;
            weaponPages.CurrentTab = 1;
            var weaponMesh = (Preview3DViewport)weaponDialog.FindChild("WeaponMeshPreview", true, false)!;
            var weaponScale = (SpinBox)weaponDialog.FindChild("WeaponMeshScale", true, false)!;
            weaponScale.Value = 1.5;
            Check(weaponDialog.Visible && weaponMesh.Model?.GetChild<Node3D>(0).Scale.IsEqualApprox(Vector3.One * 1.5f) == true,
                "the weapon mesh page previews its preprocessing before saving");
            ((Button)weaponDialog.FindChild("SaveWeaponProfile", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
            ((Button)owner.FindChild("EditOverviewWeaponProfile", true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
            weaponPages.CurrentTab = 1;
            Check(Math.Abs(weaponScale.Value - 1.5) < .001, "the weapon profile retains the saved mesh scale");
            weaponDialog.Hide();
            preview.SetWeaponPreviewEnabled(false);
            var viewportHeat = (Button)preview.FindChild("ViewportWeaponHeatmap", true, false)!;
            viewportHeat.ButtonPressed = true;
            Check(viewportHeat.Visible && preview.WeaponPreviewEnabled &&
                ((CheckButton)owner.FindChild("WeaponTrailHeatmap", true, false)!).ButtonPressed,
                "the viewport heatmap enables the trails and synchronizes the overview");

            async Task DragBrowserAsset(string id, Control target, bool cancel = false, Action? duringDrag = null)
            {
                int index = rows.IndexOf(id);
                rows.ScrollToIndex(index);
                await Frames();
                Vector2 from = rows.ActiveView.GetGlobalTransformWithCanvas() * rows.RowRect(index).GetCenter();
                await Drag(from, target.GetGlobalRect().GetCenter(), cancel, duringDrag);
            }

            async Task DragAsset(string id, bool expectHint = true, bool cancel = false, Control? alternateTarget = null,
                bool sameAnimation = false)
            {
                var hint = alternateTarget ?? dropHint;
                var target = alternateTarget ?? preview;
                await DragBrowserAsset(id, target, cancel, () =>
                {
                    Check(hint.IsVisibleInTree() == expectHint, $"drag shows the expected comparison hint for {rows.Row(rows.IndexOf(id))!.Name}");
                    if (expectHint)
                    {
                        var message = hint.GetChild<Label>(0);
                        Check(message.GetThemeColor("font_color") == (sameAnimation ? Tokens.Danger : Tokens.Accent) &&
                            (message.Text == "不能比较同一个动画") == sameAnimation,
                            "the comparison hint distinguishes the current animation from a valid comparison");
                    }
                });
                Check(!hint.Visible, "the comparison hint disappears when the drag ends");
                await owner.PendingAnimationComparison;
            }

            await DragAsset(walk.Id, sameAnimation: true);
            Check(preview.AssetComparison == null, "the open asset cannot be dropped into its own comparison");
            await DragAsset(run.Id);
            var comparison = preview.AssetComparison ?? throw new Exception("no asset comparison was shown");
            Check(comparison.Count == 2 && preview.Model == original && comparison.IsAncestorOf(original) && !comparison.Playing &&
                Math.Abs(comparison.Playhead - .3) < .01 && exitCompare.IsVisibleInTree(),
                "dropping an asset compares it with the open preview and keeps its paused playhead");
            MeshInstance3D[] Trails() => Preview3DViewport.Descendants(comparison).OfType<MeshInstance3D>()
                .Where(mesh => mesh.Name == "WeaponTrail" && mesh.IsVisibleInTree()).ToArray();
            for (int frame = 0; frame < 240 && Trails().Count(mesh => mesh.Mesh.GetSurfaceCount() > 0) < 2; frame++)
                await Frames(1);
            var ribbons = Trails();
            Check(ribbons.Length == 2 && ribbons.All(mesh => mesh.Mesh.GetSurfaceCount() == 1 &&
                ((ShaderMaterial)mesh.MaterialOverride).GetShaderParameter("heatmap").AsBool() &&
                mesh.Mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array().Max(uv => uv.Y) > 0),
                "both comparison columns retain speed-colored weapon trails");
            Check(ribbons.Select(mesh => ((ShaderMaterial)mesh.MaterialOverride).GetShaderParameter("speed_ceiling").AsSingle())
                .Distinct().Count() == 1 && preview.FindChild("WeaponHeatLegend", true, false) is Control { Visible: true },
                "the heatmap shares a labeled speed scale across comparison columns");
            preview.SetClay(!preview.ClayEnabled);
            preview.SetClay(!preview.ClayEnabled);
            Check(ribbons.All(mesh => mesh.MaterialOverride is ShaderMaterial),
                "changing the character material preserves the comparison trail shader");
            var names = (CheckButton)preview.FindChild("ShowComparisonNames", true, false)!;
            names.ButtonPressed = false;
            Check(comparison.Labels.All(label => !label.Visible), "comparison names can be hidden");
            names.ButtonPressed = true;
            openComparison.EmitSignal(BaseButton.SignalName.Pressed);
            var primaryRibbon = (MeshInstance3D)comparison.Entries[0].Slot.FindChild("WeaponTrail", true, false)!;
            float startX = primaryRibbon.GlobalPosition.X;
            await DragComparisonRow(0, 1, after: true);
            Check(comparison.Entries[1].Root == original && primaryRibbon.GlobalPosition.X > startX &&
                Math.Abs(comparison.Playhead - .3) < .01 && orderRows.GetChildCount() == 2,
                "dragging can move the primary column with its weapon trail and shared playhead");
            await DragComparisonRow(1, 0, after: false);
            orderPanel.Hide();
            preview.SetWeaponPreviewEnabled(false);
            await DragAsset(run.Id, expectHint: false);
            Check(comparison.Count == 2, "dropping the same asset again does not duplicate it");
            await DragAsset(idle.Id, cancel: true);
            Check(comparison.Count == 2, "canceling a drag leaves the active comparison intact");

            rows.SetMode(AssetBrowser.ViewMode.List);
            await Frames();
            await DragAsset(idle.Id);
            var players = Preview3DViewport.Descendants(comparison).OfType<AnimationPlayer>().ToArray();
            Check(players.Length == 3 && comparison.ComparedAssets.Count() == 2 &&
                comparison.Labels.Select(label => label.GlobalPosition.X).Distinct().Count() == 3 &&
                comparison.Labels.All(label => preview.ProjectLabel(label.GlobalPosition) != null),
                "list drags append to the comparison and keep all three assets visible");
            openComparison.EmitSignal(BaseButton.SignalName.Pressed);
            await DragComparisonRow(2, 0, after: false);
            Check(comparison.Entries.Select(entry => entry.Key).SequenceEqual([idle.Id, null, run.Id]),
                "dragging across several rows inserts at the drop point and preserves the other rows' order");
            orderPanel.Hide();
            ClickCard(rig.Id);
            Check(owner.SelectedAssetIds.SequenceEqual([rig.Id]) && preview.AssetComparison == comparison && preview.Model == original,
                "a single list click changes selection and leaves comparison open");
            stage.Timeline.Value = .95;
            Check(!comparison.Playing && Math.Abs(stage.Timeline.MaxValue - 3.2) < .01 &&
                Math.Abs(players.Single(p => p.HasAnimation("StudioBot_Walk")).CurrentAnimationPosition - .95) < .01 &&
                Math.Abs(players.Single(p => p.HasAnimation("StudioBot_Idle")).CurrentAnimationPosition - .95) < .01 &&
                Math.Abs(players.Single(p => p.HasAnimation("StudioBot_Run")).CurrentAnimationPosition - .7) < .01,
                "seeking uses one clock and holds the shorter clip at its final pose");
            stage.PlayButton.EmitSignal(BaseButton.SignalName.Pressed);
            preview._Process(2.4);
            Check(players.All(p => Math.Abs(p.CurrentAnimationPosition - .15) < .01),
                "looping restarts all clips on the shared timeline");

            openComparison.EmitSignal(BaseButton.SignalName.Pressed);
            RemoveComparisonRow(2);
            Check(preview.AssetComparison == comparison && comparison.Count == 2 && comparison.Playing &&
                Math.Abs(comparison.Playhead - .15) < .01 && orderRows.GetChildCount() == 2 &&
                comparison.ComparedAssets.Single().Name == idle.Name && api.Database.Find(run.Id) != null,
                "removing a row in the comparison panel preserves the playback and the library asset");
            RemoveComparisonRow(0);
            Check(preview.AssetComparison == null && preview.Model == original && original!.Visible &&
                Math.Abs(preview.Playhead - .3) < .01 && !exitCompare.IsVisibleInTree() && !orderPanel.Visible,
                "removing the last comparison asset restores the original preview and playhead");

            await DragAsset(rig.Id);
            Check(preview.AssetComparison?.Count == 2, "the comparison also accepts a static model");
            ClickCard(walk.Id, doubleClick: true);
            Check(preview.AssetComparison != null, "double-clicking the open asset keeps the existing comparison");
            ClickCard(run.Id, doubleClick: true);
            await owner.PendingAssetPreview;
            Check(preview.AssetComparison == null && preview.CurrentClip == "StudioBot_Run",
                "double-clicking a different row opens it and closes the previous comparison");
            window.Size = new(720, 400);
            await Frames(30);
            host.ShowPanel("assets");
            await Frames();
            var alternateTarget = (Control)owner.FindChild("HiddenPreviewComparisonDropHint", true, false)!;
            Check(host.Compact && !host.IsPanelVisible("preview"), "compact browsing has the main preview hidden");
            await DragAsset(idle.Id, alternateTarget: alternateTarget);
            Check(host.IsPanelVisible("preview") && preview.AssetComparison?.Count == 2,
                "dropping onto the compact comparison hint opens the preview and compares the assets");
            exitCompare.EmitSignal(BaseButton.SignalName.Pressed);
            Check(preview.AssetComparison == null && preview.CurrentClip == "StudioBot_Run",
                "exit comparison restores the open asset");
            window.Size = new(1440, 900);
            host.RestorePanels();
            rows.Scroll.ScrollVertical = 0;
            await Frames(30);
            rows.Selection.SetAll([run.Id, walk.Id, idle.Id]);
            await DragAsset(run.Id);
            Check(preview.AssetComparison?.Count == 3 && !preview.AssetComparison.Contains(run.Id) &&
                owner.SelectedAssetIds.ToHashSet().SetEquals([run.Id, walk.Id, idle.Id]),
                "dragging multiple selected animations from the primary row compares only the other assets");
            exitCompare.EmitSignal(BaseButton.SignalName.Pressed);
            rows.Selection.Select(run.Id);
            var approval=(ConfirmationDialog)owner.FindChild("OperationApproval",true,false)!;
            var viewport=owner.GetViewport();
            async Task ClickRow(int index,MouseButton button,bool ctrl=false,Vector2? at=null)
            {
                var point=rows.ActiveView.GetGlobalTransformWithCanvas()*(at ?? rows.RowRect(index).GetCenter());
                if(DisplayServer.GetName()!="headless")
                {
                    Input.WarpMouse(point*window.ContentScaleFactor);await Frames(4);Input.FlushBufferedEvents();
                }
                using var motion=new InputEventMouseMotion{Position=point,GlobalPosition=point,CtrlPressed=ctrl};
                using var click=new InputEventMouseButton{Position=point,GlobalPosition=point,ButtonIndex=button,Pressed=true,CtrlPressed=ctrl};
                using var release=(InputEventMouseButton)click.Duplicate();release.Pressed=false;
                viewport.PushInput(motion,true);viewport.PushInput(click,true);viewport.PushInput(release,true);
                await Frames(8);
            }

            await ClickRow(0,MouseButton.Left);
            await ClickRow(1,MouseButton.Left,ctrl:true);
            var selected=api.Search().Items.Take(2).Select(a=>a.Id).ToHashSet();
            await ClickRow(1,MouseButton.Right);
            Check(context.Visible&&context.GetItemText(0).Contains("移入回收站")&&selected.SetEquals(owner.SelectedAssetIds),
                "right-click on a selected row shows Delete and keeps the entire selection");
            context.Hide();context.EmitSignal(PopupMenu.SignalName.IdPressed,context.GetItemId(0));await Frames(8);
            Check(approval.Visible&&api.Search(category:"Trash").Total==0,"Delete asks for confirmation before changing assets");
            approval.Hide();approval.EmitSignal(AcceptDialog.SignalName.Confirmed);await Frames(8);
            Check(api.Search(category:"Trash").Items.Select(a=>a.Id).ToHashSet().SetEquals(selected),"right-click Delete moves exactly the selected assets to Trash");

            var trash=(Button)host.Panel("navigation").FindChild("CategoryTrash",true,false)!;
            trash.EmitSignal(BaseButton.SignalName.Pressed);await Frames(8);
            await ClickRow(0,MouseButton.Right);
            Check(context.Visible&&context.GetItemText(0).Contains("恢复")&&owner.SelectedAssetIds.Length==1,"right-click on an unselected Trash row selects it and offers Restore");
            var restoredId=owner.SelectedAssetIds.Single();
            context.Hide();context.EmitSignal(PopupMenu.SignalName.IdPressed,context.GetItemId(0));
            approval.Hide();approval.EmitSignal(AcceptDialog.SignalName.Confirmed);await Frames(8);
            Check(api.Database.Find(restoredId)!=null&&api.Search(category:"Trash").Total==1,"right-click Restore applies to the selected Trash row");

            var separateRoot=Path.Combine(Path.GetDirectoryName(api.Storage.Root)!,"interface-switch-"+Guid.NewGuid().ToString("N"));
            var separate=owner.Libraries.Create(separateRoot,"Mounted animations");
            var working = owner.Libraries.Active!;
            var local = api.Search(category:"Animation").Items.First();
            api.SetTags(local.Id, "CrossLibraryCheck WorkingTag");
            var remoteClip = local.Name == "StudioBot_Run" ? "StudioBot_Idle" : "StudioBot_Run";
            const string relative = "assets/remote.glb";
            var remoteFile = Path.Combine(separateRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(remoteFile)!);
            File.Copy(RuntimePaths.Bundled($"BuiltinAssets/Samples/{remoteClip}.glb"), remoteFile);
            var remote = local with { Name = "Mounted StudioBot", VirtualPath = relative, FileHash = MotionJson.Hash(File.ReadAllBytes(remoteFile)),
                Size = new FileInfo(remoteFile).Length, Tags = "CrossLibraryCheck MountedTag" };
            Check(new AssetDatabase(Path.Combine(separateRoot, AssetDatabase.IndexName)).Insert(remote), "second library contains a different clip with the same asset id");

            var all = (Button)host.Panel("navigation").FindChild("CategoryAll", true, false)!;
            all.EmitSignal(BaseButton.SignalName.Pressed);
            host.ShowPanel("assets"); rows.SetMode(AssetBrowser.ViewMode.Grid); await owner.PendingBrowse;
            var browseToggle = (Button)owner.FindChild("ToggleBrowseLibrary", true, false)!;
            void SelectBrowseLibrary()
            {
                if (!libraryDialog.Visible) owner.FindChild("LibraryButton", true, false).EmitSignal(BaseButton.SignalName.Pressed);
                var row = libraryRows.GetRoot().GetChildren().Single(item => item.GetMetadata(0).AsString() == separate.Id);
                row.Select(0);
                libraryRows.EmitSignal(Tree.SignalName.ItemSelected);
            }
            SelectBrowseLibrary();
            Check(libraryRows.GetSelected().GetText(1) == "已登记" && libraryRows.GetSelected().GetTooltipText(2) == separateRoot,
                "the library table shows registered status and keeps the full path on hover");
            var loadStatus = (Label)libraryDialog.FindChild("LibraryLoadStatus", true, false)!;
            int countBeforeMount = rows.Count;
            browseToggle.EmitSignal(BaseButton.SignalName.Pressed);
            Check(loadStatus.Visible && browseToggle.Disabled && owner.Libraries.Active == working &&
                owner.Libraries.FindBrowseLibrary(separate.Id) == null && rows.Count == countBeforeMount,
                "mounting a library leaves the current sessions and browser visible while preparing the new source");
            await owner.PendingLibraryOperation;
            Check(browseToggle.Text == "移出浏览" && libraryRows.GetSelected().GetText(1) == "已打开" &&
                owner.Libraries.Active == working && rows.Count == api.Search().Total + 1 && !loadStatus.Visible,
                "adding a library through Library makes both libraries appear in the same browser");
            var mounted = owner.Libraries.FindBrowseLibrary(separate.Id)!;
            var mixed = new AssetSort(AssetSortField.LibraryMix);
            var head = await WorkspaceApi.BrowseLibrariesAsync([working, mounted], "", "All", new(), mixed, 0, 2, default);
            var tail = await WorkspaceApi.BrowseLibrariesAsync([working, mounted], "", "All", new(), mixed, 1, 2, default);
            Check(head.Items.Select(row => row.Library).SequenceEqual([working, mounted]) &&
                tail.Items.Length > 0 && tail.Items.All(row => row.Library == working) &&
                head.Items.Concat(tail.Items).DistinctBy(row => (row.Library, row.Asset.Id)).Count() == head.Items.Length + tail.Items.Length,
                "mixed ordering shows each source before paging and keeps all remaining rows without duplicates");
            libraryDialog.Hide();
            var openLibrariesList = (VBoxContainer)host.Panel("navigation").FindChild("OpenLibrariesList", true, false)!;
            var openLibrariesPopup = openLibrariesMenu.GetPopup();
            IEnumerable<NavRow> OpenLibraryRows() => Preview3DViewport.Descendants(openLibrariesList).OfType<NavRow>();
            Check(openLibrariesMenu.Text.Contains("2") && openLibrariesPopup.GetItemText(0) == working.Identity.Name &&
                openLibrariesPopup.GetItemTooltip(0).StartsWith(working.Identity.Name + "\n") &&
                openLibrariesPopup.GetItemText(1) == separate.Name && OpenLibraryRows().Select(row => row.LabelText)
                    .SequenceEqual([working.Identity.Name, separate.Name]),
                "both open libraries appear identically in the sidebar and asset menu");
            openLibrariesPopup.EmitSignal(PopupMenu.SignalName.IdPressed, openLibrariesPopup.GetItemId(1));
            Check(libraryDialog.Visible && libraryRows.GetSelected().GetMetadata(0).AsString() == separate.Id,
                "choosing a library from the asset menu opens management on that library");
            libraryDialog.Hide();
            search.Text = "CrossLibraryCheck";
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            await owner.PendingBrowse;
            var remoteKey = rows.Rows.Single(row => row.LibraryName == separate.Name).Id;
            Check(rows.Count == 2 && rows.Row(rows.IndexOf(local.Id))?.LibraryName == working.Identity.Name && remoteKey != local.Id,
                "one search finds both source-labeled rows and keeps duplicate asset ids distinct");

            var sort = new AssetSort(AssetSortField.Name, false);
            var filter = AssetAttributeFilter.Parse("", ".1..", "");
            var tagUnion = new AssetTagFilter(["WorkingTag", "MountedTag"], matchAll: false);
            var first = await WorkspaceApi.BrowseLibrariesAsync([working, mounted], search.Text, "Animation", filter, sort, 0, 1, default, tagUnion);
            var next = await WorkspaceApi.BrowseLibrariesAsync([working, mounted], search.Text, "Animation", filter, sort, 1, 1, default, tagUnion);
            Check(first.Total == 2 && first.Items.Single().Library == mounted && next.Items.Single().Library == working,
                "tag union combines with search, category and attributes across libraries before sorting and pagination");
            var tagIntersection = new AssetTagFilter(["CrossLibraryCheck", "MountedTag"], matchAll: true);
            var mountedMatch = await WorkspaceApi.BrowseLibrariesAsync([working, mounted], search.Text, "Animation", filter, sort, 0, 1, default, tagIntersection);
            var workingMatches = await api.GetMatchingAssetsAsync(search.Text, "Animation", filter, default, tagUnion);
            Check(mountedMatch.Total == 1 && mountedMatch.Items.Single().Library == mounted &&
                workingMatches.Select(asset => asset.Id).SequenceEqual([local.Id]),
                "intersection evaluates each asset independently and the per-library query returns its own matches");

            ClickCard(remoteKey, doubleClick: true); await owner.PendingAssetPreview;
            Check(preview.CurrentClip == remoteClip && commands.SelectedId == null,
                "activating a mounted asset reads from its own library even if its id exists in the working library");
            var sidebar = (SideBar)host.Panel("navigation");
            var favoritesRow = Preview3DViewport.Descendants(sidebar).OfType<NavRow>().Single(row => row.LabelText == "我的收藏");
            await DragBrowserAsset(remoteKey, favoritesRow, cancel: true,
                duringDrag: () => Check(favoritesRow.GetChildren().OfType<Panel>().Any(panel => panel.Visible), "the favorites row highlights during a drag"));
            Check(!mounted.Api.Find(remote.Id).Favorite, "canceling a favorites drag leaves the asset unstarred");
            await DragBrowserAsset(remoteKey, favoritesRow);
            await owner.PendingBrowse;
            Check(mounted.Api.Find(remote.Id).Favorite && !api.Find(local.Id).Favorite,
                "dragging to favorites affects the source library even when another library has the same asset ID");
            var favoriteRemote = (Inspector)host.Panel("inspector");
            favoriteRemote.FavoriteButton.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(!mounted.Api.Find(remote.Id).Favorite, "the inspector can remove a favorite added by dragging");
            sidebar.SetCollapsed(true);
            await Frames();
            var favoritesRail = (Control)sidebar.FindChild("CategoryFavorites", true, false)!;
            await DragBrowserAsset(remoteKey, favoritesRail);
            await owner.PendingBrowse;
            Check(mounted.Api.Find(remote.Id).Favorite && !api.Find(local.Id).Favorite, "dragging to the collapsed favorites icon also stars the asset");
            sidebar.SetCollapsed(false);
            await Frames();
            await owner.PreviewAssetAsync(local);
            rows.Selection.SetAll([local.Id, remoteKey]);
            var sourceScope = (OptionButton)owner.FindChild("AssetLibraryScope", true, false)!;
            var sortControl = (OptionButton)owner.FindChild("AssetSort", true, false)!;
            void Choose(OptionButton option, int index)
            {
                option.Select(index);
                option.EmitSignal(OptionButton.SignalName.ItemSelected, index);
            }
            var remoteIndex = Enumerable.Range(1, sourceScope.ItemCount - 1).Single(i => sourceScope.GetItemTooltip(i).StartsWith(separate.Name));
            Choose(sourceScope, remoteIndex);
            await owner.PendingBrowse;
            Check(rows.Rows.Single().Id == remoteKey && new[] { local.Id, remoteKey }.ToHashSet().SetEquals(owner.SelectedAssetIds),
                "filtering to one source preserves browser selection from the other source");

            var collections = owner.AppCollections;
            var collection = collections.Create("Interface collection");
            var localLink = new CollectionAssetLink(working.Identity.Id, local.Id);
            collections.SetMembership(collection.Id, [localLink], true);
            Choose(sortControl, (int)AssetSortField.Imported);
            await owner.PendingBrowse;
            var collectionRow = Preview3DViewport.Descendants(sidebar).OfType<NavRow>().Single(row => row.LabelText == collection.Name);
            collectionRow.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(sidebar.ActiveCategory == collection.Id && rows.Rows.Single().Id == local.Id,
                "selecting a collection shows its working-library members");
            collectionRow.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(sidebar.ActiveCategory == "All" && rows.Rows.Single().Id == remoteKey &&
                sourceScope.Selected == remoteIndex && sortControl.Selected == (int)AssetSortField.Imported,
                "reselecting a collection clears its filter and restores the existing source, search and sort");

            NavRow CollectionRow(string id) => (NavRow)sidebar.FindChild("CollectionRow" + id, true, false)!;
            void CollectionAction(string id, CollectionMenuAction action)
            {
                var button = (MenuButton)sidebar.FindChild("CollectionActions" + id, true, false)!;
                button.ShowPopup();
                var popup = button.GetPopup();
                Check(popup.Visible && !popup.IsItemDisabled(popup.GetItemIndex((int)action)), $"collection menu offers {action}");
                popup.Hide();
                popup.EmitSignal(PopupMenu.SignalName.IdPressed, (int)action);
            }
            void SubmitCollectionName(string title, string name)
            {
                var dialog = owner.GetChildren().OfType<ConfirmationDialog>().Single(item => item.Visible && item.Title == title);
                Preview3DViewport.Descendants(dialog).OfType<LineEdit>().First().Text = name;
                dialog.Hide();
                dialog.EmitSignal(AcceptDialog.SignalName.Confirmed);
            }
            async Task DragToCollection(string assetKey, string collectionId, bool cancel = false)
            {
                rows.Selection.Select(assetKey);
                await DragBrowserAsset(assetKey, CollectionRow(collectionId), cancel);
                await owner.PendingBrowse;
            }

            CollectionAction(collection.Id, CollectionMenuAction.CreateChild);
            SubmitCollectionName("新建子集合", "Interface child");
            await owner.PendingBrowse;
            var child = collections.Read().Collections.Single(c => c.ParentId == collection.Id);
            var disclosure = (Button)sidebar.FindChild("CollectionDisclosure" + collection.Id, true, false)!;
            disclosure.EmitSignal(BaseButton.SignalName.Pressed);
            Check(!CollectionRow(child.Id).IsVisibleInTree(), "folding a parent hides its child collection row");
            disclosure.EmitSignal(BaseButton.SignalName.Pressed);
            Check(CollectionRow(child.Id).IsVisibleInTree(), "expanding a parent reveals its child collection row");
            var link = new CollectionAssetLink(separate.Id, remote.Id);
            await DragToCollection(remoteKey, child.Id, cancel: true);
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).Members.Length == 0,
                "canceling a cross-library drag does not save a collection reference");
            await DragToCollection(remoteKey, child.Id);
            await DragToCollection(remoteKey, child.Id);
            var linkedChild = collections.Read().Collections.Single(c => c.Id == child.Id);
            Check(linkedChild.Members.SequenceEqual([link]),
                "cross-library drag stores one source-qualified reference without enrolling the local asset with the same ID");
            Choose(sourceScope, 0);
            await owner.PendingBrowse;
            await DragToCollection(local.Id, child.Id, cancel: true);
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).Members.SequenceEqual([link]),
                "canceling a card drag leaves collection membership unchanged");
            await DragToCollection(local.Id, child.Id);
            await DragToCollection(local.Id, child.Id);
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).Members.Count(member => member == localLink) == 1,
                "dragging the same card to a child twice stores one membership");

            CollectionAction(child.Id, CollectionMenuAction.Rename);
            SubmitCollectionName("编辑集合", "Renamed child");
            await owner.PendingBrowse;
            var persistedChild = collections.Read().Collections.Single(c => c.Id == child.Id);
            Check(persistedChild is { Name: "Renamed child" } && persistedChild.ParentId == collection.Id &&
                persistedChild.Members.ToHashSet().SetEquals([localLink, link]),
                "app settings preserve a renamed child's parent and both source-qualified members");
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(rows.Rows.Select(row => row.Id).ToHashSet().SetEquals([local.Id, remoteKey]),
                "opening a collection shows local and cross-library members with the same asset ID as separate rows");
            Choose(sortControl, (int)AssetSortField.Duration);
            await owner.PendingBrowse;
            Check(rows.Rows.Select(row => row.Id).ToHashSet().SetEquals([local.Id, remoteKey]),
                "duration ordering includes only the collection's members across both libraries");

            CollectionAction(collection.Id, CollectionMenuAction.Export);
            var export = host.Panel("export");
            var exportCollection = (OptionButton)export.FindChild("ExportCollectionChoice", true, false)!;
            Check(exportCollection.Visible && exportCollection.GetSelectedMetadata().AsString() == collection.Id,
                "the collection menu opens export with the complete collection selected");
            var exportFormat = (OptionButton)export.FindChild("ExportFormat", true, false)!;
            Choose(exportFormat, exportFormat.GetItemIndex((int)JobKind.ExportGlb));
            string collectionOutput = Path.Combine(root, "collection-export");
            ((LineEdit)export.FindChild("ExportFolder", true, false)!).Text = collectionOutput;
            var queueCollection = (Button)export.FindChild("QueueExport", true, false)!;
            queueCollection.EmitSignal(BaseButton.SignalName.Pressed);
            for (int frame = 0; frame < 300 && queueCollection.Disabled; frame++) await Frames(1);
            Check(Directory.Exists(collectionOutput) && Directory.GetFiles(collectionOutput, "*.glb").Length == 2,
                "export includes the child collection and deduplicates shared members across open libraries");
            host.HidePanel("export");

            const string removeFromCurrentCollection = "从当前集合移出…";
            rows.Selection.Clear();
            rows.ScrollToIndex(rows.IndexOf(remoteKey));
            await Frames();
            await ClickRow(rows.IndexOf(remoteKey), MouseButton.Right);
            Check(context.Visible && context.GetItemText(0) == removeFromCurrentCollection && !context.IsItemDisabled(0) &&
                owner.SelectedAssetIds.SequenceEqual([remoteKey]),
                "right-clicking a collection member offers removal from the current collection");
            context.Hide();
            context.EmitSignal(PopupMenu.SignalName.IdPressed, context.GetItemId(0));
            Check(approval.Visible && collections.Read().Collections.Single(c => c.Id == child.Id).Members.Contains(link),
                "removal waits for confirmation");
            approval.Hide();
            approval.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await owner.PendingBrowse;
            Check(rows.Rows.Single().Id == local.Id && owner.SelectedAssetIds.Length == 0 &&
                mounted.Api.Find(remote.Id).Name == remote.Name && File.Exists(remoteFile) &&
                collections.Read().Collections.Single(c => c.Id == child.Id).Members.SequenceEqual([localLink]),
                "removing a linked member clears its selection and leaves the source animation and the local member intact");
            CollectionAction(child.Id, CollectionMenuAction.Close);
            await owner.PendingBrowse;
            Check(sidebar.ActiveCategory == "All" && rows.Count == 2, "closing a collection restores the browser view");
            rows.Selection.SetAll([local.Id, remoteKey]);
            await DragBrowserAsset(remoteKey, favoritesRow);
            await owner.PendingBrowse;
            Check(mounted.Api.Find(remote.Id).Favorite && api.Find(local.Id).Favorite && rows.Selection.Count == 2,
                "dragging a multiple selection to favorites stars assets in both source libraries");
            await DragBrowserAsset(remoteKey, CollectionRow(child.Id));
            await owner.PendingBrowse;
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).Members.ToHashSet().SetEquals([localLink, link]),
                "one drag adds the entire selection to a collection");

            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            var organize = Preview3DViewport.Descendants(host.Panel("assets")).OfType<MenuButton>().Single(button => button.Text == "整理 ▾");
            organize.ShowPopup();
            var assetActions = organize.GetPopup();
            Check(assetActions.GetItemText(0) == removeFromCurrentCollection && !assetActions.IsItemDisabled(0),
                "the organize menu offers current collection removal for a multi-selection");
            assetActions.Hide();
            assetActions.EmitSignal(PopupMenu.SignalName.IdPressed, assetActions.GetItemId(0));
            approval.Hide();
            approval.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await owner.PendingBrowse;
            var afterRemoval = collections.Read().Collections;
            Check(rows.Count == 0 && owner.SelectedAssetIds.Length == 0 &&
                afterRemoval.Single(c => c.Id == child.Id).Members.Length == 0 &&
                afterRemoval.Single(c => c.Id == collection.Id).Members.SequenceEqual([localLink]),
                "removing selected members from both sources empties only the open collection");
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            rows.Selection.SetAll([local.Id, remoteKey]);
            await DragBrowserAsset(remoteKey, CollectionRow(child.Id));
            await owner.PendingBrowse;

            Vector2 CollectionIcon(string id) => Preview3DViewport.Descendants(CollectionRow(id)).OfType<IconView>()
                .Single().GetGlobalRect().GetCenter();
            var rootDrop = (Control)sidebar.FindChild("CollectionRootDrop", true, false)!;
            var beforeCycle = collections.Read();
            await Drag(CollectionIcon(collection.Id), CollectionRow(child.Id).GetGlobalRect().GetCenter());
            Check(collections.Read().Revision == beforeCycle.Revision, "dragging a parent onto its child cannot create a cycle");
            await Drag(CollectionIcon(child.Id), rootDrop.GetGlobalRect().GetCenter(), cancel: true);
            Check(collections.Read().Revision == beforeCycle.Revision, "canceling a collection move leaves the hierarchy intact");
            await Drag(CollectionIcon(child.Id), rootDrop.GetGlobalRect().GetCenter());
            var movedChild = collections.Read().Collections.Single(c => c.Id == child.Id);
            Check(movedChild.ParentId == null && movedChild.Members.ToHashSet().SetEquals([localLink, link]),
                "dragging a collection icon onto the section heading promotes it and preserves its assets");

            Vector2 CollectionEdge(string id, bool after)
            {
                var bounds = CollectionRow(id).GetGlobalRect();
                return new(bounds.GetCenter().X, after ? bounds.End.Y - 3 : bounds.Position.Y + 3);
            }
            string[] CollectionOrder(string? parentId) => collections.Read().OrderedCollections()
                .Where(c => c.ParentId == parentId).Select(c => c.Id).ToArray();

            var beforeReorder = collections.Read();
            await Drag(CollectionRow(child.Id).GetGlobalRect().GetCenter(), CollectionEdge(collection.Id, after: false),
                cancel: true, duringDrag: () =>
                {
                    var marker = CollectionRow(collection.Id).GetChildren().OfType<Panel>().Single();
                    Check(marker.Visible && marker.GetThemeStylebox("panel") is StyleBoxFlat { BorderWidthTop: 2, BorderWidthBottom: 0 },
                        "the upper edge shows an insertion line for a collection drag");
                });
            Check(collections.Read().Revision == beforeReorder.Revision, "canceling an edge drop leaves the stored order intact");
            await Drag(CollectionRow(child.Id).GetGlobalRect().GetCenter(), CollectionEdge(collection.Id, after: false));
            Check(CollectionOrder(null).SequenceEqual([child.Id, collection.Id]) &&
                CollectionRow(child.Id).GlobalPosition.Y < CollectionRow(collection.Id).GlobalPosition.Y,
                "dropping above a row saves the root order and immediately updates the sidebar");
            await Drag(CollectionIcon(child.Id), CollectionEdge(collection.Id, after: true));
            Check(CollectionOrder(null).SequenceEqual([collection.Id, child.Id]), "dropping below a row saves the reverse root order");
            await Drag(CollectionIcon(child.Id), CollectionRow(collection.Id).GetGlobalRect().GetCenter());
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).ParentId == collection.Id &&
                CollectionRow(child.Id).IsVisibleInTree() && sidebar.ActiveCategory == "All",
                "dropping a collection icon on another collection nests it without opening it");

            var sibling = collections.Create("Other child", collection.Id);
            Choose(sortControl, (int)AssetSortField.Duration);
            await owner.PendingBrowse;
            await Frames();
            var sidebarScroll = sidebar.GetChildren().OfType<ScrollContainer>().Single();
            int sidebarScrollOffset = sidebarScroll.ScrollVertical;
            sidebarScroll.EnsureControlVisible(CollectionRow(sibling.Id));
            await Frames();
            await Drag(CollectionRow(child.Id).GetGlobalRect().GetCenter(), CollectionEdge(sibling.Id, after: true),
                duringDrag: () =>
                {
                    var marker = CollectionRow(sibling.Id).GetChildren().OfType<Panel>().Single();
                    Check(marker.Visible && marker.GetThemeStylebox("panel") is StyleBoxFlat { BorderWidthTop: 0, BorderWidthBottom: 2 },
                        "the lower edge shows an insertion line for a nested collection drag");
                });
            Check(CollectionOrder(collection.Id).SequenceEqual([sibling.Id, child.Id]), "nested collections save their order within their parent");
            Check(CollectionRow(sibling.Id).GlobalPosition.Y < CollectionRow(child.Id).GlobalPosition.Y,
                "the sidebar displays the updated nested order");

            CollectionAction(collection.Id, CollectionMenuAction.Copy);
            CollectionAction(collection.Id, CollectionMenuAction.Copy);
            var copied = collections.Read().OrderedCollections();
            var firstCopy = copied.Single(c => c.Name == collection.Name + "（副本）");
            var secondCopy = copied.Single(c => c.Name == collection.Name + "（副本 2）");
            Check(CollectionOrder(null).SequenceEqual([collection.Id, secondCopy.Id, firstCopy.Id]),
                "repeated copies receive distinct names and are inserted next to the source");
            foreach (var copy in new[] { firstCopy, secondCopy })
            {
                var copiedChildren = copied.Where(c => c.ParentId == copy.Id).ToArray();
                Check(copy.Members.SequenceEqual([localLink]) && copiedChildren.Select(c => c.Name).SequenceEqual([sibling.Name, "Renamed child"]) &&
                    copiedChildren[1].Id != child.Id && copiedChildren[1].Members.ToHashSet().SetEquals([localLink, link]),
                    "the copy preserves child order and asset references with distinct collection IDs");
                foreach (var copiedChild in copiedChildren) collections.Delete(copiedChild.Id);
                collections.Delete(copy.Id);
            }
            collections.Delete(sibling.Id);
            sidebarScroll.ScrollVertical = sidebarScrollOffset;

            CollectionAction(collection.Id, CollectionMenuAction.Delete);
            Check(approval.Visible && collections.Read().Collections.Any(c => c.Id == collection.Id), "deleting a collection waits for confirmation");
            approval.Hide();
            approval.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await owner.PendingBrowse;
            Check(collections.Read().Collections.Single(c => c.Id == child.Id).ParentId == null &&
                !collections.Read().Collections.Any(c => c.Id == collection.Id) &&
                collections.Read().Collections.Single(c => c.Id == child.Id).Members.Contains(localLink),
                "deleting the parent promotes its child and preserves the child membership");
            Choose(sourceScope, remoteIndex);
            await owner.PendingBrowse;

            var assetPanel = host.Panel("assets");
            var browserSearch = (LineEdit)assetPanel.FindChild("AssetBrowserSearch", true, false)!;
            var lastPage = (Button)assetPanel.FindChild("AssetPageLast", true, false)!;
            owner.DisplayScale.SetScale(2);
            window.Size = new(1024, 700);
            host.ShowPanel("assets");
            await Frames(30);
            var panelBounds = assetPanel.GetGlobalRect();
            Check(new Control[] { browserSearch, sourceScope, openLibrariesMenu, sortControl, lastPage }
                    .All(control => control.IsVisibleInTree() && panelBounds.Encloses(control.GetGlobalRect())) &&
                sourceScope.GlobalPosition.Y == openLibrariesMenu.GlobalPosition.Y &&
                lastPage.GlobalPosition.Y >= rows.GetGlobalRect().End.Y,
                "a mounted library keeps the search controls aligned and pagination below the results at 200 percent");
            owner.DisplayScale.SetScale(1);
            window.Size = new(1440, 900);
            await Frames(30);
            rows.Selection.Select(local.Id);
            Choose(sortControl, (int)AssetSortField.Imported);
            await owner.PendingBrowse;
            Choose(sourceScope, 0);
            await owner.PendingBrowse;
            Check(rows.Count == 2 && sortControl.Selected == (int)AssetSortField.LibraryMix,
                "returning from a source to all libraries restores the mixed result without clearing search");
            Choose(sortControl, (int)AssetSortField.Imported);
            await owner.PendingBrowse;
            Check(sourceScope.Selected == 0 && sortControl.Selected == (int)AssetSortField.Imported,
                "explicit chronological sort remains available in the all-libraries scope");
            Choose(sourceScope, 0);
            await owner.PendingBrowse;
            Check(sourceScope.AllowReselect && sortControl.Selected == (int)AssetSortField.LibraryMix && rows.Row(0)?.Id == local.Id,
                "reselecting the already active all-libraries option returns to mixed sorting");
            await DragAsset(remoteKey);
            var crossComparison = preview.AssetComparison ?? throw new Exception("No cross-library comparison was shown.");
            stage.Timeline.Value = .4;
            Check(crossComparison.Labels.Any(label => label.Text.StartsWith(separate.Name)) &&
                crossComparison.Labels.Any(label => label.Text.StartsWith(working.Identity.Name)) &&
                Preview3DViewport.Descendants(crossComparison).OfType<AnimationPlayer>().Count(p => Math.Abs(p.CurrentAnimationPosition - .4) < .01) == 2,
                "library and asset names identify both animations and the same timeline seeks both clips");
            locatePreview.EmitSignal(BaseButton.SignalName.Pressed);
            Check(previewLocateMenu.Visible && previewLocateMenu.AllowSearch && previewLocateMenu.ItemCount == 2 &&
                previewLocateMenu.GetItemText(0).Contains(local.Name) && previewLocateMenu.GetItemText(1).Contains(remote.Name),
                "comparing animations opens a searchable locate menu with both asset names");
            previewLocateMenu.Hide();
            previewLocateMenu.EmitSignal(PopupMenu.SignalName.IndexPressed, 1L);
            await owner.PendingBrowse;
            Check(owner.SelectedAssetIds.SequenceEqual([remoteKey]) && rows.VisibleIndices().Contains(rows.IndexOf(remoteKey)) &&
                search.Text == "CrossLibraryCheck" && sourceScope.Selected == 0 && preview.AssetComparison == crossComparison &&
                Math.Abs(crossComparison.Playhead - .4) < .01,
                "choosing a named comparison asset reveals it in the mixed-library search without changing the comparison");

            var closeMounted = (Button)openLibrariesList.FindChild("CloseLibrary" + separate.Id, true, false)!;
            Check(closeMounted.TooltipText.Contains("移出浏览"), "the sidebar has a direct remove action beside each library");
            closeMounted.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            libraryDialog.Hide();
            Check(preview.AssetComparison == null && owner.Libraries.FindBrowseLibrary(separate.Id) == null && rows.Count == 1 &&
                !owner.SelectedAssetIds.Contains(remoteKey) && !openComparison.IsVisibleInTree(),
                "removing a library drops its asset from the comparison and returns to the open preview");
            Check(openLibrariesMenu.Text.Contains("1") && openLibrariesPopup.GetItemText(0) == working.Identity.Name &&
                openLibrariesPopup.GetItemTooltip(0).StartsWith(working.Identity.Name + "\n") &&
                OpenLibraryRows().Single().LabelText == working.Identity.Name,
                "removed libraries disappear from both open-library lists");
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            var closedCard = rows.Rows.Single(row => row.Id == remoteKey);
            Check(rows.Count == 2 && closedCard.ClosedLibraryId == separate.Id && closedCard.Name == remote.Name &&
                closedCard.LibraryName == separate.Name && !closedCard.ThumbnailPending,
                "a collection keeps an unopened source member visible with its name and a disabled preview");
            int closedIndex = rows.IndexOf(remoteKey);
            using (var doubleClick = new InputEventMouseButton { Position = rows.RowRect(closedIndex).End - new Vector2(10, 10),
                ButtonIndex = MouseButton.Left, Pressed = true, DoubleClick = true })
            {
                var priorPreview = owner.PendingAssetPreview;
                rows.ActiveView._GuiInput(doubleClick);
                Check(owner.PendingAssetPreview == priorPreview && owner.Libraries.FindOpenLibrary(separate.Id) == null,
                    "double-clicking the body of an unavailable member leaves the preview unchanged");
            }
            rows.SetMode(AssetBrowser.ViewMode.List);
            rows.ScrollToIndex(closedIndex);
            await Frames();
            await ClickRow(closedIndex, MouseButton.Left, at: rows.List.SourceButtonRect(closedIndex).GetCenter());
            await owner.PendingLibraryOperation;
            Check(sidebar.ActiveCategory == child.Id && owner.Libraries.FindBrowseLibrary(separate.Id) != null &&
                rows.Rows.Select(row => row.Id).ToHashSet().SetEquals([local.Id, remoteKey]) &&
                rows.Rows.All(row => row.ClosedLibraryId == null),
                "the inline source action adds the correct library to browsing and enables its collection member");
            rows.SetMode(AssetBrowser.ViewMode.Grid);
            rows.Selection.Select(remoteKey);
            var locate = (Button)owner.FindChild("LocateAssetInLibrary", true, false)!;
            locate.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(sidebar.ActiveCategory == "All" && search.Text.Length == 0 && sourceScope.Selected == remoteIndex &&
                rows.Row(rows.IndexOf(remoteKey))?.Id == remoteKey &&
                rows.VisibleIndices().Contains(rows.IndexOf(remoteKey)), "locating a collection member selects its source library and reveals the asset");
            search.Text = "CrossLibraryCheck";
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            Choose(sourceScope, 0);
            await owner.PendingBrowse;
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            CollectionAction(child.Id, CollectionMenuAction.Close);
            await owner.PendingBrowse;

            new WorkspacePreferences{Scale=2}.Save(Path.Combine(separateRoot,"workspace.json"));
            host.ShowPanel("bones");host.FocusPanel("bones");owner.DisplayScale.SetScale(1.25);await Frames();
            var activeTool=host.ActiveTool;
            var activeSize=window.Size;
            commands.LibraryOpen(separate.Id);await commands.PendingTask;
            Check(owner.Libraries.Active?.Identity.Id==separate.Id&&host.ActiveTool==activeTool&&host.FocusedPanel=="bones"&&
                owner.DisplayScale.RequestedScale==1.25&&window.Size==activeSize,"switching libraries preserves active tool, focused panel, window size and scale");
            await owner.PendingBrowse;
            Check(owner.Libraries.FindBrowseLibrary(working.Identity.Id) == working && rows.Count == api.Search().Total + 1,
                "promoting an already loaded library preserves the combined browser and changes the working library");
            Check(CollectionRow(child.Id) != null && collections.Read().Collections.Single(c => c.Id == child.Id)
                .Members.ToHashSet().SetEquals([localLink, link]), "switching the active library preserves the application collection");
            Check(openLibrariesPopup.GetItemText(0) == separate.Name &&
                openLibrariesPopup.GetItemTooltip(1).StartsWith(working.Identity.Name + "\n"),
                "the open-library menu updates after a console context change");

            SelectBrowseLibrary();
            Check(browseToggle.Text == "移出浏览" && !browseToggle.Disabled, "the manager can remove either open library");
            browseToggle.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            Check(owner.Libraries.FindOpenLibrary(separate.Id) == null && rows.Count == api.Search().Total &&
                libraryRows.GetSelected().GetText(1) == "已登记" && File.Exists(remoteFile),
                "removing the console's current library leaves the other library open and its own files registered");
            libraryDialog.Hide();
            var closeLast = (Button)openLibrariesList.FindChild("CloseLibrary" + working.Identity.Id, true, false)!;
            closeLast.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            Check(owner.Libraries.OpenLibraries().Length == 0 && rows.Count == 0 && owner.SelectedAssetIds.Length == 0 &&
                openLibrariesMenu.Text.Contains("0") && preview.Model == null, "removing the final library leaves a usable empty workspace");
            SelectBrowseLibrary();
            browseToggle.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            Check(owner.Libraries.FindOpenLibrary(separate.Id)?.Api.Find(remote.Id).Favorite == true && rows.Count == 1,
                "a removed library can be reopened with its saved asset metadata");

            libraryDialog.Hide();
            var pageDatabase = owner.Libraries.FindOpenLibrary(separate.Id)!.Api.Database;
            var secondCollection = collections.Create("Page return collection");
            for (int i = 0; i < 61; i++)
                pageDatabase.Insert(remote with
                {
                    Id = $"page-return-{i}", FileHash = $"page-return-{i}", Name = $"PageReturnFixture {i:D2}", Tags = ""
                });
            search.Text = "PageReturnFixture";
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            Choose(sortControl, (int)AssetSortField.Name);
            await owner.PendingBrowse;
            lastPage.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            var lastPageAsset = rows.Rows.Single().Id;
            var firstPageButton = (Button)assetPanel.FindChild("AssetPageFirst", true, false)!;
            Check(!firstPageButton.Disabled, "the filtered result is on its second page");
            ClickCard(lastPageAsset, doubleClick: true);
            await owner.PendingAssetPreview;
            firstPageButton.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(rows.IndexOf(lastPageAsset) < 0, "the open animation can be on a different results page");
            locatePreview.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(!firstPageButton.Disabled && rows.Rows.Single().Id == lastPageAsset && search.Text == "PageReturnFixture" &&
                owner.SelectedAssetIds.SequenceEqual([lastPageAsset]), "locating the open animation jumps to its page in the current search");
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            CollectionRow(secondCollection.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            CollectionAction(secondCollection.Id, CollectionMenuAction.Close);
            await owner.PendingBrowse;
            Check(sidebar.ActiveCategory == "All" && rows.Rows.Single().Id == lastPageAsset && !firstPageButton.Disabled,
                "closing after switching between collections restores the filtered result page");
            CollectionRow(child.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            all.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(rows.Rows.Single().Id == lastPageAsset && !firstPageButton.Disabled,
                "clicking All while in a collection returns to the previous page");

            collections.SetMembership(secondCollection.Id, pageDatabase.SearchAll("PageReturnFixture")
                .Select(asset => new CollectionAssetLink(separate.Id, asset.Id)).ToArray(), true);
            CollectionRow(secondCollection.Id).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            lastPage.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            var closePageSource = (Button)openLibrariesList.FindChild("CloseLibrary" + separate.Id, true, false)!;
            closePageSource.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            Check(rows.Rows.Single().ClosedLibraryId == separate.Id && !firstPageButton.Disabled &&
                sidebar.ActiveCategory == secondCollection.Id, "closing the last source preserves a collection page of disabled members");
            firstPageButton.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            Check(rows.Count == 60 && rows.Rows.All(row => row.ClosedLibraryId == separate.Id) && !lastPage.Disabled,
                "unavailable members use the same page size even with no libraries open");
            lastPage.EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingBrowse;
            rows.ScrollToIndex(0);
            await Frames();
            await ClickRow(0, MouseButton.Left, at: rows.Grid.SourceButtonRect(0).GetCenter());
            await owner.PendingLibraryOperation;
            Check(rows.Rows.Single().Name == "PageReturnFixture 60" && rows.Rows[0].ClosedLibraryId == null &&
                !firstPageButton.Disabled && owner.Libraries.FindOpenLibrary(separate.Id) != null,
                "opening the source from a grid card re-enables its preview while keeping the collection page");

            ((Button)openLibrariesList.FindChild("CloseLibrary" + separate.Id, true, false)!).EmitSignal(BaseButton.SignalName.Pressed);
            await owner.PendingLibraryOperation;
            string relocatedRoot = separateRoot + "-relocated";
            Directory.Move(separateRoot, relocatedRoot);
            search.Text = "";
            search.EmitSignal(LineEdit.SignalName.TextSubmitted, search.Text);
            await owner.PendingBrowse;
            Check(rows.Count == 60 && rows.Rows.All(row => row.ClosedLibraryId == separate.Id && row.LocateClosedLibrary),
                "members keep a disabled placeholder when the registered source directory moves");
            rows.ScrollToIndex(0);
            await Frames();
            await ClickRow(0, MouseButton.Left, at: rows.Grid.SourceButtonRect(0).GetCenter());
            var locateSource = owner.GetChildren().OfType<ConfirmationDialog>().Single(dialog => dialog.Visible && dialog.Title == "定位来源库");
            Preview3DViewport.Descendants(locateSource).OfType<LineEdit>().First().Text = relocatedRoot;
            locateSource.Hide();
            locateSource.EmitSignal(AcceptDialog.SignalName.Confirmed);
            await owner.PendingLibraryOperation;
            Check(owner.Libraries.FindOpenLibrary(separate.Id)?.Root == relocatedRoot &&
                owner.Libraries.Entries().Single(entry => entry.Id == separate.Id).Path == relocatedRoot &&
                sidebar.ActiveCategory == secondCollection.Id && rows.Rows.All(row => row.ClosedLibraryId == null),
                "locating a moved source updates its registration and restores the collection previews");

            var report=$"PASS: {checks.Count} interface checks\n"+string.Join("\n",checks)+"\nDisplay server: "+DisplayServer.GetName()+"\nDPI source: "+owner.DisplayScale.Source;
            File.WriteAllText(Path.Combine(root,"results.txt"),report);GD.Print(report);owner.GetTree().Quit(0);
        }
        catch(Exception ex){GD.PushError("INTERFACE TEST FAILED: "+ex);File.WriteAllText(Path.Combine(root,"results.txt"),$"{checks.Count} passed\n"+ex);owner.GetTree().Quit(1);}
    }
}
