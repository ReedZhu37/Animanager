using System.Diagnostics;

namespace Animanager.Core.Pipeline;

public sealed class BlenderPipeline
{
    public string Executable { get; private set; }
    public string ScriptPath { get; }
    public bool Available => File.Exists(Executable);
    public BlenderPipeline(string scriptPath)
    {
        ScriptPath=scriptPath;
        Executable=Environment.GetEnvironmentVariable("BLENDER_BIN")??"";
        if(!Available && File.Exists(@"D:\Blender\5.2\blender.exe"))Executable=@"D:\Blender\5.2\blender.exe";
    }
    public void Configure(string path)
    {
        if(!File.Exists(path))throw new FileNotFoundException("Blender executable not found",path);
        Executable=Path.GetFullPath(path);
    }
    public async Task ConvertAsync(string source,string destination,IProgress<string>? progress=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!Available)throw new InvalidOperationException("Blender 未配置；设置 BLENDER_BIN 或运行 pipeline blender <exe>。");
        if(!File.Exists(source))throw new FileNotFoundException("源资产不存在",source);
        if(File.Exists(destination))throw new IOException("目标文件已存在，拒绝覆盖："+destination);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var info=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"--background","--factory-startup","--python-exit-code","1","--python",ScriptPath,"--","--source",source,"--out",destination})info.ArgumentList.Add(arg);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        using var process=new Process{StartInfo=info};
        var output=new System.Collections.Concurrent.ConcurrentQueue<string>();
        process.Start();
        using var termination=linked.Token.Register(()=>{
            try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}
        });
        async Task Pump(StreamReader reader)
        {
            while(await reader.ReadLineAsync() is {} line)
            {
                output.Enqueue(line);while(output.Count>150)output.TryDequeue(out _);
                progress?.Report(line);
            }
        }
        var stdout=Pump(process.StandardOutput);var stderr=Pump(process.StandardError);
        try
        {
            await process.WaitForExitAsync(linked.Token);await Task.WhenAll(stdout,stderr);
            linked.Token.ThrowIfCancellationRequested();
            if(process.ExitCode!=0||!File.Exists(destination)||new FileInfo(destination).Length==0||!output.Any(l=>l.StartsWith("ANIMANAGER_CONVERT_OK ")))
                throw new IOException("Blender conversion failed: "+string.Join("\n",output.TakeLast(18)));
            if(Path.GetExtension(destination).Equals(".fbx",StringComparison.OrdinalIgnoreCase))
            {
                using var binary=File.OpenRead(destination);var header=new byte[23];
                if(binary.Read(header)!=23||System.Text.Encoding.ASCII.GetString(header)!="Kaydara FBX Binary  \0\x1a\0")throw new IOException("Blender output is not a valid binary FBX header.");
            }
        }
        catch(Exception error)
        {
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}
            await Task.WhenAll(stdout,stderr);
            if(File.Exists(destination))File.Delete(destination);
            if(error is OperationCanceledException&&!ct.IsCancellationRequested&&timeout.IsCancellationRequested)throw new TimeoutException("Blender conversion timed out after 8 minutes.",error);
            throw;
        }
    }
}
