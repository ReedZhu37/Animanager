using System.Text.Json;

namespace Animanager.UI.Workspace;

public sealed class DockNode
{
    public string Id { get; set; }=Guid.NewGuid().ToString("N");
    public string Axis { get; set; }="";
    public float Ratio { get; set; }=.5f;
    public DockNode? First { get; set; }
    public DockNode? Second { get; set; }
    public List<string> Panels { get; set; }=[];
    public string Active { get; set; }="";
    public bool IsGroup=>Axis.Length==0;
    public static DockNode Group(params string[] panels)=>new(){Panels=panels.ToList(),Active=panels.FirstOrDefault()??""};
    public static DockNode Split(string axis,float ratio,DockNode first,DockNode second)=>new(){Axis=axis,Ratio=ratio,First=first,Second=second};
    public IEnumerable<DockNode> Groups()=>IsGroup?[this]:(First?.Groups()??[]).Concat(Second?.Groups()??[]);
    public IEnumerable<string> AllPanels()=>Groups().SelectMany(g=>g.Panels);
}

public enum DockEdge { Center, Left, Right, Top, Bottom }

/// <summary>UI-independent layout operations; invalid persisted trees are normalized before rendering.</summary>
public sealed class WorkspaceLayout
{
    public static readonly string[] PanelIds=["navigation","assets","preview","inspector","export","jobs","bones"];
    public int Version { get; set; }=1;
    public DockNode Root { get; set; }=DefaultRoot("browse");
    public List<string> Hidden { get; set; }=["jobs","bones"];
    public List<string> CompactOrder { get; set; }=PanelIds.ToList();
    public string CompactActive { get; set; }="assets";
    public static DockNode DefaultRoot(string preset)=>preset switch
    {
        "animation"=>DockNode.Split("h",.28f,DockNode.Group("bones","assets","navigation"),DockNode.Group("preview","inspector","export")),
        "preview"=>DockNode.Split("h",.25f,DockNode.Group("assets","navigation"),DockNode.Split("h",.74f,DockNode.Group("preview"),DockNode.Group("inspector","export"))),
        "batch"=>DockNode.Split("h",.5f,DockNode.Group("assets","navigation"),DockNode.Split("v",.5f,DockNode.Group("export","inspector"),DockNode.Group("jobs","preview"))),
        _=>DockNode.Split("h",.18f,DockNode.Group("navigation"),DockNode.Split("h",.49f,DockNode.Group("assets"),DockNode.Split("v",.65f,DockNode.Group("preview"),DockNode.Group("inspector","export"))))
    };
    public void Preset(string preset)
    {
        Root=DefaultRoot(preset);Hidden=PanelIds.Except(Root.AllPanels()).ToList();CompactActive=preset=="animation"?"bones":preset=="preview"?"preview":preset=="batch"?"export":"assets";
    }
    public DockNode? GroupFor(string panel)=>Root.Groups().FirstOrDefault(g=>g.Panels.Contains(panel));
    public void Select(string panel)
    {
        var group=GroupFor(panel);if(group!=null)group.Active=panel;CompactActive=panel;
    }
    public bool Hide(string panel)
    {
        if(!PanelIds.Contains(panel)||Root.AllPanels().Count()<=1)return false;
        Root=Remove(Root,panel)!;if(!Hidden.Contains(panel))Hidden.Add(panel);
        if(CompactActive==panel)CompactActive=Root.AllPanels().First();return true;
    }
    public void Show(string panel)
    {
        if(!PanelIds.Contains(panel))return;
        Hidden.Remove(panel);
        if(GroupFor(panel)==null){var group=GroupFor("inspector")??Root.Groups().Last();group.Panels.Add(panel);}
        Select(panel);
    }
    public bool Move(string panel,string targetGroup,DockEdge edge,int index=-1)
    {
        if(!PanelIds.Contains(panel))return false;
        var target=Root.Groups().FirstOrDefault(g=>g.Id==targetGroup);if(target==null)return false;
        var source=GroupFor(panel);
        if(source==target&&edge!=DockEdge.Center&&source.Panels.Count==1)return false;
        if(edge==DockEdge.Center)
        {
            if(source!=target)Root=Remove(Root,panel)??target;
            else target.Panels.Remove(panel);
            target.Panels.Insert(index<0?target.Panels.Count:Math.Clamp(index,0,target.Panels.Count),panel);target.Active=panel;
        }
        else
        {
            Root=Remove(Root,panel)!;
            var added=DockNode.Group(panel);var before=edge is DockEdge.Left or DockEdge.Top;
            var split=DockNode.Split(edge is DockEdge.Left or DockEdge.Right?"h":"v",.5f,before?added:target,before?target:added);
            Root=Replace(Root,target.Id,split);
        }
        Hidden.Remove(panel);CompactActive=panel;return true;
    }
    private static DockNode Replace(DockNode node,string id,DockNode replacement)
    {
        if(node.Id==id)return replacement;
        if(node.First!=null)node.First=Replace(node.First,id,replacement);
        if(node.Second!=null)node.Second=Replace(node.Second,id,replacement);return node;
    }
    private static DockNode? Remove(DockNode node,string panel)
    {
        if(node.IsGroup)
        {
            node.Panels.Remove(panel);if(node.Panels.Count==0)return null;
            if(!node.Panels.Contains(node.Active))node.Active=node.Panels[0];return node;
        }
        node.First=node.First==null?null:Remove(node.First,panel);node.Second=node.Second==null?null:Remove(node.Second,panel);
        return node.First==null?node.Second:node.Second==null?node.First:node;
    }
    public void Normalize()
    {
        var seen=new HashSet<string>();var nodeIds=new HashSet<string>();int count=0;
        DockNode? Clean(DockNode? n,int depth)
        {
            if(n==null||depth>12||++count>40)return null;
            if(string.IsNullOrEmpty(n.Id)||!nodeIds.Add(n.Id)){n.Id=Guid.NewGuid().ToString("N");nodeIds.Add(n.Id);}
            n.Ratio=float.IsFinite(n.Ratio)?Math.Clamp(n.Ratio,.1f,.9f):.5f;
            if(n.Axis is "h" or "v")
            {
                n.First=Clean(n.First,depth+1);n.Second=Clean(n.Second,depth+1);
                return n.First==null?n.Second:n.Second==null?n.First:n;
            }
            n.Axis="";n.First=null;n.Second=null;
            n.Panels=(n.Panels??[]).Where(p=>PanelIds.Contains(p)&&seen.Add(p)).ToList();
            if(n.Panels.Count==0)return null;
            if(!n.Panels.Contains(n.Active))n.Active=n.Panels[0];return n;
        }
        Root=Clean(Root,0)??DockNode.Group("assets");
        Hidden=PanelIds.Except(Root.AllPanels()).ToList();
        CompactOrder=(CompactOrder??[]).Where(PanelIds.Contains).Distinct().Concat(PanelIds).Distinct().ToList();
        if(!Root.AllPanels().Contains(CompactActive))CompactActive=Root.AllPanels().First();Version=1;
    }
    public WorkspaceLayout Clone()=>JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(this))!;
}

public sealed class WorkspacePreferences
{
    public int Version { get; set; }=1;
    public WorkspaceLayout Layout { get; set; }=new();
    public Dictionary<string,WorkspaceLayout> SavedLayouts { get; set; }=[];
    public double Scale { get; set; } // 0 = system; otherwise total UI scale.
    public bool Comfortable { get; set; }
    public int Width { get; set; }=1440;
    public int Height { get; set; }=900;
    public int X { get; set; }
    public int Y { get; set; }
    public bool HasPosition { get; set; }
    public bool Maximized { get; set; }
    public static WorkspacePreferences Load(string path,out string error)
    {
        error="";
        try
        {
            if(!File.Exists(path))return new();
            var p=JsonSerializer.Deserialize<WorkspacePreferences>(File.ReadAllText(path))??throw new IOException("Empty layout");
            if(p.Version!=1)throw new IOException("Unsupported workspace version");
            p.Layout??=new();p.Layout.Normalize();p.SavedLayouts??=[];
            foreach(var key in p.SavedLayouts.Keys.ToArray()){if(p.SavedLayouts[key]==null)p.SavedLayouts.Remove(key);else p.SavedLayouts[key].Normalize();}
            if(!double.IsFinite(p.Scale)||p.Scale<1||p.Scale>2)p.Scale=0;
            return p;
        }
        catch(Exception ex) when(ex is JsonException or IOException or UnauthorizedAccessException)
        {error="工作区配置无法读取，已恢复默认布局："+ex.Message;return new();}
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);var temp=path+".tmp";
        try{File.WriteAllText(temp,JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));File.Move(temp,path,true);}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
