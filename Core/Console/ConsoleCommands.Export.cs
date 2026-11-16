using System.Text.Json;
using Animanager.Core.Jobs;
using Animanager.Core.Pipeline;

namespace Animanager.Core.Console;

public partial class ConsoleCommands
{
    private const string ConfiguredExportHelp =
        "export asset <asset> <glb|res|fbx> <folder> <options>\n" +
        "export options_plan <query_or_*> <glb|res|fbx> <folder> <options>\n" +
        "batch export_with <query_or_*> <glb|res|fbx> <folder> <options>\n" +
        "Use default or compact JSON, for example {\"IncludeMesh\":false,\"Clips\":[\"Walk\"],\"StartSeconds\":0.2,\"EndSeconds\":0.7,\"SampleFps\":24}.";

    private void RegisterConfiguredExportCommands()
    {
        Bind("export asset", nameof(ExportAssetWithOptions), "Queue one asset: <asset> <glb|res|fbx> <folder> <default_or_compact_json>");
        Bind("export options_plan", nameof(PlanExportsWithOptions), "Plan all matches: <query_or_*> <glb|res|fbx> <folder> <default_or_compact_json>");
        Bind("batch export_with", nameof(BatchExportWithOptions), "Queue all matches: <query_or_*> <glb|res|fbx> <folder> <default_or_compact_json>");
    }

    public void ExportAssetWithOptions(string asset, string format, string folder, string options) => Guard(() =>
        StartPlan(_api.Planner.ExportSingle(asset, Expand(folder), ParseExportKind(format), ParseExportOptions(options))));

    public void PlanExportsWithOptions(string query, string format, string folder, string options) => Guard(() =>
        PrintPlan(_api.Planner.Export(query, Expand(folder), ParseExportKind(format), ParseExportOptions(options))));

    public void BatchExportWithOptions(string query, string format, string folder, string options) => Guard(() =>
        StartPlan(_api.Planner.Export(query, Expand(folder), ParseExportKind(format), ParseExportOptions(options))));

    private static JobKind ParseExportKind(string format) => format.TrimStart('.').ToLowerInvariant() switch
    {
        "fbx" => JobKind.ExportFbx,
        "glb" => JobKind.ExportGlb,
        "res" => JobKind.ExportRes,
        _ => throw new ArgumentException("Choose glb, res or fbx.")
    };

    private static ExportOptions ParseExportOptions(string options)
    {
        if (options.Equals("default", StringComparison.OrdinalIgnoreCase)) return new ExportOptions();
        return JsonSerializer.Deserialize<ExportOptions>(options, BatchPlanner.JsonOptions)
            ?? throw new ArgumentException("Use default or a compact JSON object for export options.");
    }

    private static string DescribeExportOptions(BatchPlan plan) => plan.ExportOptions is { } options
        ? "\nOptions: " + JsonSerializer.Serialize(options) : "";
}
