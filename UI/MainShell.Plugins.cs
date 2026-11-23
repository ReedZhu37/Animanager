using Animanager.Core.Plugins;
using Animanager.UI.Controls;
using Godot;
using static Animanager.UI.Controls.AppControls;

namespace Animanager.UI;

public partial class MainShell
{
    private const int PluginSettingsWidth = 490;
    private readonly AnimationPlugins _animationPlugins = new();
    private readonly List<(IAnimationPlugin Plugin, Label Status)> _pluginStatusLabels = [];
    private AcceptDialog _pluginSettings = null!;
    private PopupMenu _pluginTargets = null!;
    private Button _pluginOutputButton = null!;
    private string _pluginOutput = "";
    private CancellationTokenSource? _pluginCancellation;
    private Task _pluginTask = Task.CompletedTask;

    private void BuildPluginSettings()
    {
        if (OperatingSystem.IsWindows()) _animationPlugins.Register(CascadeurPlugin.Create());

        _pluginSettings = new AcceptDialog { Name = "AnimationPluginSettings", Title = "插件", OkButtonText = "关闭" };
        AddChild(_pluginSettings);
        // Keep wrapped descriptions from contributing their full height to the dialog's minimum size.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        _pluginSettings.AddChild(scroll);
        var content = Column(scroll);
        content.CustomMaximumSize = new(PluginSettingsWidth, -1);
        content.PropagateMaximumSize = true;
        content.AddThemeConstantOverride("separation", 10);
        foreach (var plugin in _animationPlugins.All)
        {
            var header = AppControls.Row(content);
            header.AddThemeConstantOverride("separation", 12);
            var enabled = new CheckBox { Text = "启用 " + plugin.Name, ButtonPressed = PluginEnabled(plugin) };
            enabled.Toggled += value =>
            {
                if (value) _preferences.DisabledAnimationPlugins.Remove(plugin.Id);
                else _preferences.DisabledAnimationPlugins.Add(plugin.Id);
                SaveSoon();
                RefreshPluginSettings();
            };
            header.AddChild(enabled);
            var status = AppControls.Wrapped("");
            status.VerticalAlignment = VerticalAlignment.Center;
            header.AddChild(status);
            _pluginStatusLabels.Add((plugin, status));
            if (plugin.Setup is not { } setup) continue;

            content.AddChild(AppControls.Wrapped(setup.Instructions));
            var folder = Dialog(FileDialog.FileModeEnum.OpenDir, setup.DirectoryPrompt);
            folder.DirSelected += path =>
            {
                try
                {
                    setup.Install(path);
                    SetStatus(plugin.Name + " 接收脚本已安装。请在目标工具里启动连接。");
                    status.Text = "已安装接收脚本 · 等待连接";
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    _pluginSettings.Hide();
                    ShowError("插件安装失败", error.Message);
                }
            };
            content.AddChild(Button("安装接收脚本…", () =>
            {
                _pluginSettings.Hide();
                folder.PopupCenteredRatio(.65f);
            }));
        }

        if (_pluginStatusLabels.Count == 0) content.AddChild(AppControls.Wrapped("当前平台没有可用插件。"));
        var actions = AppControls.Row(content);
        actions.AddChild(Button("刷新连接", RefreshPluginSettings));
        _pluginOutputButton = Button("打开上次传输文件夹", () => OS.ShellOpen(Path.GetDirectoryName(_pluginOutput)!));
        actions.AddChild(_pluginOutputButton);
    }

    private bool PluginEnabled(IAnimationPlugin plugin) => !_preferences.DisabledAnimationPlugins.Contains(plugin.Id);

    private void ShowPluginSettings()
    {
        RefreshPluginSettings();
        PopupFit(_pluginSettings, new(PluginSettingsWidth, 245), keepFittedMinimum: true);
    }

    private void RefreshPluginSettings()
    {
        foreach (var (plugin, status) in _pluginStatusLabels)
        {
            try { status.Text = PluginEnabled(plugin) ? plugin.ConnectionStatus : "已停用"; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = error.Message; }
        }
        _pluginOutputButton.Disabled = !File.Exists(_pluginOutput);
    }

    private void BuildPluginAssetMenu()
    {
        _pluginTargets = new PopupMenu { Name = "AnimationPluginTargets" };
        _assetContextMenu.AddChild(_pluginTargets);
        _pluginTargets.IdPressed += id =>
        {
            int index = _pluginTargets.GetItemIndex((int)id);
            if (index < 0 || _assetContextKey is not { } assetKey) return;
            var plugin = _animationPlugins.Find(_pluginTargets.GetItemMetadata(index).AsString());
            if (plugin != null && PluginEnabled(plugin) && _pluginTask.IsCompleted)
                _pluginTask = SendToPluginAsync(plugin, assetKey);
        };
    }

    private void AddPluginAssetMenu(string assetKey, int index)
    {
        if (_browseRows[index].Asset is { Trashed: true } ||
            (_previewAssetId != assetKey || _preview.CurrentClip.Length == 0) && _browseRows[index].Asset.Type != "Animation") return;

        _pluginTargets.Clear();
        foreach (var plugin in _animationPlugins.All.Where(PluginEnabled))
        {
            _pluginTargets.AddItem(plugin.Name);
            _pluginTargets.SetItemMetadata(_pluginTargets.ItemCount - 1, plugin.Id);
        }
        if (_pluginTargets.ItemCount == 0) return;
        _assetContextMenu.AddSubmenuNodeItem("发送到", _pluginTargets, 18);
        _assetContextMenu.SetItemDisabled(_assetContextMenu.ItemCount - 1, _libraryLoading || !_pluginTask.IsCompleted);
    }

    private async Task SendToPluginAsync(IAnimationPlugin plugin, string assetKey)
    {
        if (_closing || _libraryLoading) return;
        using var cancellation = new CancellationTokenSource();
        _pluginCancellation = cancellation;
        try
        {
            var library = SelectionLibrary(assetKey) ?? throw new InvalidOperationException("动画来源库已关闭。");
            var api = library.Api;
            var asset = api.Database.Find(SelectionAssetId(assetKey));
            if (asset is null or { Trashed: true }) throw new InvalidOperationException("动画已不可用。");
            if (_selectedKey != assetKey || _previewAssetId != assetKey)
            {
                if (_selectedKey == assetKey && !PendingAssetPreview.IsCompleted) await PendingAssetPreview;
                else await PreviewAssetAsync(library, asset, cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            var clip = _preview.CurrentClip;
            if (_selectedKey != assetKey || _previewAssetId != assetKey || clip.Length == 0)
                throw new InvalidOperationException("无法确定要发送的片段，请打开动画预览后重试。");

            var options = new Core.Pipeline.ExportOptions
            {
                Clips = [clip], Retarget = _retargetEnabled.ButtonPressed ? ReadRetargetOptions() : null
            };
            var request = new AnimationPluginRequest(clip, (folder, kind, ct) =>
                api.ExportAsync(asset.Id, folder, kind, options, ct: ct));
            var progress = new Progress<string>(message =>
            {
                if (!_closing && _pluginCancellation == cancellation) SetStatus(message);
            });
            var result = plugin is CascadeurPlugin cascadeur
                ? await cascadeur.SendAsync(request, progress, cancellation.Token, (pose, ct) => SelectCascadeurOrientationAsync(pose, clip, ct))
                : await plugin.SendAsync(request, progress, cancellation.Token);
            if (_closing) return;
            if (result.OutputPath.Length > 0) _pluginOutput = result.OutputPath;
            SetStatus(result.Message);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing) ShowError("发送到 " + plugin.Name + " 失败", error.Message);
        }
        finally { _pluginCancellation = null; }
    }

    private async Task<System.Numerics.Quaternion?> SelectCascadeurOrientationAsync(CascadeurBindPose pose, string clip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var dialog = new Components.CascadeurSendDialog(pose, clip);
        AddChild(dialog);
        try
        {
            PopupFit(dialog, new(550, 355), keepFittedMinimum: true);
            return await dialog.Selection.WaitAsync(ct);
        }
        finally
        {
            dialog.Hide();
            dialog.QueueFree();
        }
    }
}
