using System.Text.Json.Serialization;

namespace Animanager.Core.Jobs;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobKind { Import, ExportFbx, ImportUasset }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobState { Queued, Running, Completed, CompletedWithErrors, Cancelled, Interrupted, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ItemState { Pending, Running, Succeeded, Skipped, Failed, Cancelled, Interrupted }

public sealed record PlanItem
{
    public string AssetId { get; init; }="";
    public string Name { get; init; }="";
    public string Input { get; init; }="";
    public string Error { get; init; }="";
}
public sealed record BatchPlan
{
    public int Version { get; init; }=1;
    public string Id { get; init; }=Guid.NewGuid().ToString("N");
    public JobKind Kind { get; init; }
    public DateTime CreatedUtc { get; init; }=DateTime.UtcNow;
    public string Selection { get; init; }="";
    public string OutputFolder { get; init; }="";
    public int IgnoredFiles { get; init; }
    public List<PlanItem> Items { get; init; }=[];
}
public sealed class JobItem
{
    public PlanItem Source { get; set; }=new();
    public ItemState State { get; set; }
    public string Output { get; set; }="";
    public string Error { get; set; }="";
    public string Log { get; set; }="";
    public int ImportedAssets { get; set; }
    public int DuplicateAssets { get; set; }
    public int UnsupportedAssets { get; set; }
}
public sealed class JobRecord
{
    public int Version { get; set; }=1;
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string PlanId { get; set; }="";
    public string RetryOf { get; set; }="";
    public JobKind Kind { get; set; }
    public JobState State { get; set; }=JobState.Queued;
    public string OutputFolder { get; set; }="";
    public DateTime CreatedUtc { get; set; }=DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Message { get; set; }="Queued";
    public string Error { get; set; }="";
    public string ReportPath { get; set; }="";
    public int IgnoredFiles { get; set; }
    public List<JobItem> Items { get; set; }=[];
    public int Total=>Items.Count;
    public int Succeeded=>Items.Count(i=>i.State==ItemState.Succeeded);
    public int Skipped=>Items.Count(i=>i.State==ItemState.Skipped);
    public int Failed=>Items.Count(i=>i.State==ItemState.Failed);
    public int Cancelled=>Items.Count(i=>i.State==ItemState.Cancelled);
    public int Finished=>Items.Count(i=>i.State is not (ItemState.Pending or ItemState.Running));
}
