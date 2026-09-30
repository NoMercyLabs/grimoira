using Xunit;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Grimora.Plugin.Tests;

// A throwaway plugin root (a copy of bootstrap.cs plus two tiny projects that stand in for the CLI and the
// server) and a throwaway data folder. The build is a real `dotnet publish`; the tiny CLI echoes its arguments
// and GRIMORA_PLUGIN_ROOT and exits 3, so a test sees exactly what the hook step ran and passed through.
public sealed class PluginFixture : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "grimora-plugin-" + Guid.NewGuid().ToString("N")[..8]);
    public string Root => Path.Combine(Dir, "plugin");
    public string Data => Path.Combine(Dir, "data");
    public string Current => Path.Combine(Data, "current");

    public PluginFixture(bool withData = true)
    {
        File.Copy(BootstrapPath(), Path.Combine(Directory.CreateDirectory(Root).FullName, "bootstrap.cs"));
        WriteProject("Grimora.Cli", "grimora",
            "System.Console.WriteLine(string.Join(\" \", args) + \" ROOT=\" + System.Environment.GetEnvironmentVariable(\"GRIMORA_PLUGIN_ROOT\"));\n"
            + "if (args.Length > 0 && args[0] == \"sleep\") { System.Threading.Thread.Sleep(60000); }\nreturn 3;");
        WriteProject("Grimora.Server", "Grimora.Server", "return 0;");
        File.WriteAllText(Path.Combine(Root, "Directory.Build.props"), "<Project />");
        if (withData)
        {
            Directory.CreateDirectory(Data);
        }
    }

    private static string BootstrapPath([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "bootstrap.cs"));

    private void WriteProject(string name, string assembly, string code)
    {
        string dir = Directory.CreateDirectory(Path.Combine(Root, "src", name)).FullName;
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>{assembly}</AssemblyName></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dir, "Program.cs"), code);
    }

    public void ChangeServerSource(string text) => File.WriteAllText(Path.Combine(Root, "src", "Grimora.Server", "Program.cs"), text);

    public ProcessStartInfo Start(bool pluginData = true)
    {
        ProcessStartInfo info = new("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        info.ArgumentList.Add(Path.Combine(Root, "bootstrap.cs"));
        if (pluginData)
        {
            info.Environment["CLAUDE_PLUGIN_DATA"] = Data;
        }
        else
        {
            info.Environment.Remove("CLAUDE_PLUGIN_DATA");
        }

        return info;
    }

    public (int Exit, string Out, string Err) Run(bool pluginData = true)
    {
        using Process p = Process.Start(Start(pluginData))!;
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim(), err.Result.Trim());
    }

    /// <summary>Runs bootstrap.cs with the given arguments in the foreground (a `--build` or `--point` verb).</summary>
    public (int Exit, string Out, string Err) RunVerb(params string[] args)
    {
        ProcessStartInfo info = Start();
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using Process p = Process.Start(info)!;
        p.StandardInput.Close();
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output.Trim(), err.Result.Trim());
    }

    /// <summary>Points `current` at a folder the way a finished build left it: a junction on Windows, a symlink elsewhere.</summary>
    public void LinkCurrentTo(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Current, target);
            return;
        }

        ProcessStartInfo info = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "/c", "mklink", "/J", Current, target })
        {
            info.ArgumentList.Add(a);
        }

        using Process mk = Process.Start(info)!;
        mk.StandardOutput.ReadToEnd();
        mk.StandardError.ReadToEnd();
        mk.WaitForExit();
        Assert.Equal(0, mk.ExitCode);
    }

    /// <summary>Waits until the detached build ended: the lock is gone.</summary>
    public void WaitForBuild()
    {
        DateTime until = DateTime.UtcNow.AddSeconds(120);
        Thread.Sleep(500);
        while (File.Exists(Path.Combine(Data, "build.lock")) && DateTime.UtcNow < until)
        {
            Thread.Sleep(200);
        }

        Assert.False(File.Exists(Path.Combine(Data, "build.lock")), "the build did not end in 120 s");
    }

    public string? CurrentTarget() =>
        Directory.Exists(Current) ? Directory.ResolveLinkTarget(Current, true)?.FullName : null;

    public string[] Builds() =>
        Directory.Exists(Path.Combine(Data, "builds")) ? [.. Directory.GetDirectories(Path.Combine(Data, "builds")).Order()] : [];

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Current))
            {
                Directory.Delete(Current, false); // the junction only, never its target
            }
        }
        catch { /* best effort */ }

        try { Directory.Delete(Dir, true); } catch { /* a held folder is left in temp */ }
    }
}
