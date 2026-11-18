using System.Text.Json;
using Animanager.Core.Retarget;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private void RegisterRetargetCommands()
    {
        Bind("retarget inspect", nameof(RetargetInspect), "Validate source rig: <asset> <ue4-mannequin|ue5-mannequin>");
        Bind("retarget preview", nameof(RetargetPreview), "Preview <asset> <target rig> using the shared bake evaluator");
        Bind("retarget presets", nameof(RetargetPresets), "List retarget presets in this library");
        Bind("retarget save", nameof(RetargetSave), "Save preset and asset association: <asset> <name> <compact options JSON>");
    }
    public void RetargetInspect(string asset, string target) => Guard(() => Info(JsonSerializer.Serialize(_api.InspectRetarget(asset, new() { TargetRig = target }))));
    public void RetargetPresets() => Guard(() => Info(JsonSerializer.Serialize(_api.RetargetPresets.List())));
    public void RetargetSave(string asset, string name, string json) => Guard(() =>
    {
        using var activity = _api.BeginActivity();
        var options = _api.InspectRetarget(asset, JsonSerializer.Deserialize<RetargetOptions>(json, Jobs.BatchPlanner.JsonOptions) ?? throw new ArgumentException("Invalid options."));
        var saved = _api.RetargetPresets.Save(name, options); _api.RetargetPresets.Associate(_api.Find(asset).Id, options);
        Info(JsonSerializer.Serialize(saved));
    });
    public void RetargetPreview(string asset, string target) => Job(async ct =>
    {
        var api = _api; var item = api.Find(asset); var options = api.InspectRetarget(asset, new() { TargetRig = target });
        var result = await api.PreviewRetargetAsync(item.Id, options, ct);
        _preview.AttachRetarget(result, options, api.Storage.Resolve(item.VirtualPath));
        if (item.Type != "Animation") { api.SetAssetType(item.Id, "Animation"); item.Type = "Animation"; }
        SelectedId = item.Id; _selection(item); Info($"Retarget {result.Source.Id} → {result.Target.Id}; {result.Target.Names.Length} bones; no contact IK");
    });
}
