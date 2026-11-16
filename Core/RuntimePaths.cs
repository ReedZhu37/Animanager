using Godot;

namespace Animanager.Core;

/// <summary>Files consumed by .NET / Blender live beside the executable, outside the PCK.</summary>
public static class RuntimePaths
{
    public static string? ExplicitLibraryRoot()
    {
        var dataArg=OS.GetCmdlineUserArgs().FirstOrDefault(a=>a.StartsWith("--data-dir="));
        return dataArg!=null?Expand(dataArg[11..]):null;
    }

    public static string DefaultLibraryRoot()
    {
        if(OS.HasFeature("editor"))
        {
            string configured=ProjectSettings.GetSetting("animanager/development_library", "").AsString();
            if(!string.IsNullOrWhiteSpace(configured))
            {
                string root=Expand(configured);
                if(Storage.LibraryManager.HasLibrary(root))return root;
                GD.PushWarning($"Development library is missing: {root}. Using the user library.");
            }
        }
        return ProjectSettings.GlobalizePath("user://Library");
    }

    public static string Bundled(string relative)=>OS.HasFeature("editor")
        ?ProjectSettings.GlobalizePath("res://"+relative)
        :Path.GetFullPath(Path.Combine(Path.GetDirectoryName(OS.GetExecutablePath())!,relative));
    public static string Expand(string path)=>path.StartsWith("res://")?Bundled(path[6..])
        :path.StartsWith("user://")?ProjectSettings.GlobalizePath(path):Path.GetFullPath(path);
}
