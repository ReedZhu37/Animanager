using Godot;

namespace Animanager.Core;

/// <summary>Resolves raw files consumed by .NET and Blender outside the PCK.</summary>
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

    public static string Bundled(string relative)
    {
        if (OS.HasFeature("editor"))
            return ProjectSettings.GlobalizePath("res://" + relative);

        var executableDirectory = Path.GetDirectoryName(OS.GetExecutablePath())!;
        var bundleDirectory = OperatingSystem.IsMacOS()
            ? Path.Combine(executableDirectory, "..", "Resources")
            : executableDirectory;
        return Path.GetFullPath(Path.Combine(bundleDirectory, relative));
    }
    public static string Expand(string path)=>path.StartsWith("res://")?Bundled(path[6..])
        :path.StartsWith("user://")?ProjectSettings.GlobalizePath(path):Path.GetFullPath(path);
}
