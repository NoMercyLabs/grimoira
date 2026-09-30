// The SessionStart step of the installed plugin, a .NET file-based app: `dotnet ${CLAUDE_PLUGIN_ROOT}/bootstrap.cs`.
// It uses the BCL only, so the plugin needs nothing but `dotnet`. It replaces the earlier Node launch scripts (design in
// RESTRUCTURE.md slice 32a).
//
// Build output is gitignored and every plugin version installs into a new cache folder, so right after an
// install or update there is no published CLI. The published CLI and server live in CLAUDE_PLUGIN_DATA (kept
// across plugin updates), each build in <data>/builds/<first 12 hex of the stamp>/{bin-cli,bin-server}.
// <data>/current (a directory junction on Windows, a symlink elsewhere) points at the newest complete build.
// hooks.json and .mcp.json run `dotnet <data>/current/bin-cli/grimora.dll ...` directly.
//
// - Build current: run `dotnet <data>/current/bin-cli/grimora.dll hook SessionStart`; stdin, stdout and the
//   exit code pass straight through. GRIMORA_PLUGIN_ROOT is set, so a server it starts finds the plugin files.
// - Missing or stale: start `bootstrap.cs --build <data>` detached, print one line, exit 0. A lock file makes
//   sure two sessions never build at once. The session goes on without Grimora until the build is done.
// - `--build <data>`: the detached build. It never writes into a folder that may be in use; it moves `current`
//   only after a complete build and stamp; it keeps the previous build as the rollback and deletes older ones
//   only when a trial rename proves nothing holds them.
// - `--point <link> <target>`: the `current` swap on its own (a repair by hand, and the tests); the previous link
//   stays when it fails.
//
// With no CLAUDE_PLUGIN_DATA (a checkout) the checkout's own bin-cli/ and bin-server/ are used.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

const string Building = "Grimora is building its CLI and server in the background (first session after an install or update); it is ready in a few minutes.";
string[] buildInputs = ["src", "Directory.Build.props", "Directory.Packages.props", "global.json"];
TimeSpan lockExpiry = TimeSpan.FromMinutes(30);

string root = Path.GetDirectoryName(Path.GetFullPath((string?)AppContext.GetData("EntryPointFilePath") ?? "bootstrap.cs"))!;

if (args.Length == 2 && args[0] == "--build")
{
    return Build(args[1]);
}

if (args.Length == 3 && args[0] == "--point")
{
    return Point(args[1], args[2]);
}

string? pluginData = Environment.GetEnvironmentVariable("CLAUDE_PLUGIN_DATA");
bool installed = !string.IsNullOrEmpty(pluginData);
string dataDir = installed ? pluginData! : root;
string buildDir = installed ? Path.Combine(dataDir, "current") : dataDir;

if (installed)
{
    try { RecordPluginRoot(); } catch { /* best effort: the env var and the walk-up remain */ }
}

if (IsCurrent())
{
    return RunHook(Path.Combine(buildDir, "bin-cli", "grimora.dll"));
}

if (installed)
{
    try { WarnIfCurrentIsBroken(); } catch { /* best effort: the build below repairs it */ }
}

if (TakeLock())
{
    try { StartBuild(); } catch { ReleaseLock(); }
}

Console.Out.WriteLine(Building);
return 0;

// ---- SessionStart ----

bool IsCurrent()
{
    string cliDir = Path.Combine(buildDir, "bin-cli");
    if (!File.Exists(Path.Combine(cliDir, "grimora.dll")) || !File.Exists(Path.Combine(buildDir, "bin-server", "Grimora.Server.dll")))
    {
        return false;
    }

    return !installed || ReadStamp(cliDir) == TreeHash();
}

// A fresh install has no build yet and `current` is expected to be missing: that is the Building line. After a
// complete build a missing `current`, and at any time a dangling one, means every hook slot and the MCP server
// run against nothing (2026-09-30: a failed swap left it missing and nothing said so). One line: what is wrong,
// the log, the repair.
void WarnIfCurrentIsBroken()
{
    string? problem = null;
    if (!LinkExists(buildDir))
    {
        string buildsDir = Path.Combine(dataDir, "builds");
        bool anyCompleteBuild = Directory.Exists(buildsDir)
            && Directory.GetDirectories(buildsDir).Any(b => File.Exists(Path.Combine(b, "bin-cli", "build-stamp.txt")));
        if (anyCompleteBuild)
        {
            problem = $"{buildDir} is missing";
        }
    }
    else
    {
        // Directory.Exists is true for a dangling junction on Windows (it does not follow it) and false for a dangling
        // symlink elsewhere, so the end of the link chain is checked itself.
        FileSystemInfo? end = null;
        try { end = Directory.ResolveLinkTarget(buildDir, true); } catch { /* unreadable reparse data */ }
        if (end is not null ? !end.Exists : !Directory.Exists(buildDir))
        {
            problem = $"{buildDir} points at a missing folder ({end?.FullName ?? "unreadable target"})";
        }
    }

    if (problem is null)
    {
        return;
    }

    string repair = $"dotnet \"{Path.Combine(root, "bootstrap.cs")}\" --build \"{dataDir}\"";
    Console.Out.WriteLine($"Grimora: {problem}: no hook and no MCP server can run until it is repaired; see {Path.Combine(dataDir, "build.log")}; repair: {repair}");
}

bool TakeLock()
{
    Directory.CreateDirectory(dataDir); // the first session may be the first to use the data folder
    string lockFile = Path.Combine(dataDir, "build.lock");
    try
    {
        if (File.Exists(lockFile) && DateTime.UtcNow - File.GetLastWriteTimeUtc(lockFile) > lockExpiry)
        {
            File.Delete(lockFile); // longer than any real build: left by a build that died
        }
    }
    catch { /* no lock yet */ }

    try
    {
        using FileStream fs = new(lockFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        fs.Write(Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()));
        return true;
    }
    catch
    {
        return false;
    }
}

void ReleaseLock()
{
    try { File.Delete(Path.Combine(dataDir, "build.lock")); } catch { /* already gone */ }
}

// Written to a temp file and renamed so a reader never sees half a path. A server started without this hook
// (the logon task, a thin client of another slot) has no GRIMORA_PLUGIN_ROOT and finds the plugin files here.
void RecordPluginRoot()
{
    Directory.CreateDirectory(dataDir);
    string file = Path.Combine(dataDir, "plugin-root.txt");
    string temp = $"{file}.{Environment.ProcessId}.tmp";
    File.WriteAllText(temp, root);
    File.Move(temp, file, true);
}

int RunHook(string cli)
{
    try
    {
        ProcessStartInfo info = new("dotnet") { UseShellExecute = false };
        info.ArgumentList.Add(cli);
        info.ArgumentList.Add("hook");
        info.ArgumentList.Add("SessionStart");
        info.Environment["GRIMORA_PLUGIN_ROOT"] = root;
        using Process hook = Process.Start(info)!; // no redirects: stdin, stdout and stderr are inherited
        hook.WaitForExit();
        return hook.ExitCode;
    }
    catch
    {
        return 0; // a dotnet that cannot start fails open
    }
}

// The build must not hold the hook's stdout/stderr pipes: the hook runner reads them to the end, so a child
// that keeps them waits the session for the whole build. On Windows, .NET starts a redirected child with
// handle inheritance on, so the child would inherit the runner's pipes (slice 29d waited 380 s on this);
// ShellExecute starts it with none, in a hidden window. Elsewhere .NET marks its descriptors close-on-exec.
void StartBuild()
{
    ProcessStartInfo info = new("dotnet");
    if (OperatingSystem.IsWindows())
    {
        info.UseShellExecute = true;
        info.WindowStyle = ProcessWindowStyle.Hidden;
        info.Arguments = $"\"{Path.Combine(root, "bootstrap.cs")}\" --build \"{dataDir}\"";
    }
    else
    {
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.ArgumentList.Add(Path.Combine(root, "bootstrap.cs"));
        info.ArgumentList.Add("--build");
        info.ArgumentList.Add(dataDir);
    }

    using Process child = Process.Start(info)!;
    if (!OperatingSystem.IsWindows())
    {
        child.StandardInput.Close();
        child.StandardOutput.Close();
        child.StandardError.Close();
    }
}

// ---- the detached build ----

int Build(string data)
{
    Directory.CreateDirectory(data);
    using FileStream log = new(Path.Combine(data, "build.log"), FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
    try
    {
        string hash = TreeHash();
        string buildsDir = Path.Combine(data, "builds");
        string target = Path.Combine(buildsDir, hash[..12]);
        if (ReadStamp(Path.Combine(target, "bin-cli")) != hash)
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, true); // an unfinished earlier try: never current, never in use
            }

            Publish(Path.Combine(root, "src", "Grimora.Cli", "Grimora.Cli.csproj"), Path.Combine(target, "bin-cli"), log);
            Publish(Path.Combine(root, "src", "Grimora.Server", "Grimora.Server.csproj"), Path.Combine(target, "bin-server"), log);
            File.WriteAllText(Path.Combine(target, "bin-cli", "build-stamp.txt"), hash);
        }

        string link = Path.Combine(data, "current");
        string? previous = RealOrNull(link);
        PointCurrent(link, target);
        RemoveOldBuilds(buildsDir, [RealOrNull(target), target, previous]);
        return 0;
    }
    catch (Exception error)
    {
        // Started with no stdio, so the log is the only place a failure shows.
        byte[] text = Encoding.UTF8.GetBytes($"bootstrap: {error.Message}\n");
        log.Write(text);
        return 1;
    }
    finally
    {
        try { File.Delete(Path.Combine(data, "build.lock")); } catch { /* already gone */ }
    }
}

// The swap on its own, for the tests and for a repair by hand: exit 0 with `link` resolving to `target`, or exit 1
// with the reason on stderr and the previous `link` untouched.
int Point(string link, string target)
{
    try
    {
        PointCurrent(link, target);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"bootstrap: {error.Message}");
        return 1;
    }
}

void Publish(string project, string output, FileStream log)
{
    // PublishAot=false: the same choice as build-cli.ps1 and build-server.ps1.
    ProcessStartInfo info = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "publish", project, "-c", "Release", "-o", output, "-p:PublishAot=false" })
    {
        info.ArgumentList.Add(a);
    }

    using Process publish = Process.Start(info)!;
    Task copyOut = publish.StandardOutput.BaseStream.CopyToAsync(log);
    Task copyErr = publish.StandardError.BaseStream.CopyToAsync(log);
    publish.WaitForExit();
    Task.WaitAll(copyOut, copyErr);
    if (publish.ExitCode != 0)
    {
        throw new InvalidOperationException($"dotnet publish {project} failed (exit {publish.ExitCode}); see build.log");
    }
}

string? RealOrNull(string path)
{
    try
    {
        if (!Directory.Exists(path))
        {
            return null;
        }

        return Directory.ResolveLinkTarget(path, true)?.FullName ?? Path.GetFullPath(path);
    }
    catch
    {
        return null;
    }
}

// `current` must never be missing, not even for a failed swap: every hook slot and the MCP server run through it.
// The new link is built beside the old one as `current.next`, checked to resolve to the target, and only then
// moved over `current`. A Windows junction needs no admin rights (mklink /J; the BCL has no junction call) but
// cannot be renamed over an existing one, so there the old junction goes for the instant the rename takes and
// is put back if that rename fails. Elsewhere rename(2) replaces the symlink atomically. Paths are normalised
// first: mklink refuses a forward-slash path, which is what a Git Bash caller passes (2026-09-30).
void PointCurrent(string link, string target)
{
    link = Path.GetFullPath(link);
    target = Path.GetFullPath(target);
    string want = RealOrNull(target) ?? throw new DirectoryNotFoundException($"current cannot point at {target}: no such folder");
    string next = $"{link}.next";
    try
    {
        RemoveLink(next);
        CreateLink(next, target);
        string? resolved = RealOrNull(next);
        if (!PathsEqual(resolved, want))
        {
            throw new IOException($"{next} resolves to {resolved ?? "nothing"}, not {want}");
        }

        if (OperatingSystem.IsWindows())
        {
            // Neither the move nor the restore can be made to fail from outside (mklink /J accepts a missing target),
            // so the tests inject the failures here; unset everywhere else.
            string fault = Environment.GetEnvironmentVariable("GRIMORA_BOOTSTRAP_FAULT") ?? "";
            string? previous = RealOrNull(link);
            RemoveLink(link);
            try
            {
                if (fault.Contains("move", StringComparison.Ordinal))
                {
                    throw new IOException("injected move failure");
                }

                Directory.Move(next, link); // renames the junction itself; it never follows it
            }
            catch (Exception moveError)
            {
                string repair = $"repair: dotnet \"{Path.Combine(root, "bootstrap.cs")}\" --build \"{Path.GetDirectoryName(link)}\"";
                if (previous is null)
                {
                    throw new IOException($"move {next} to {link} failed ({moveError.Message}); there was no previous {link} to restore, so it is missing; {repair}");
                }

                try
                {
                    if (fault.Contains("restore", StringComparison.Ordinal))
                    {
                        throw new IOException("injected restore failure");
                    }

                    CreateLink(link, previous);
                }
                catch (Exception restoreError)
                {
                    throw new IOException($"move {next} to {link} failed ({moveError.Message}) and the restore of {link} -> {previous} failed too ({restoreError.Message}); {link} is missing; {repair}");
                }

                throw new IOException($"move {next} to {link} failed ({moveError.Message}); {link} was restored to {previous}");
            }

            return;
        }

        // File.Move cannot do this: .NET follows the link, sees a directory and refuses. rename(2) replaces atomically.
        if (Native.rename(next, link) != 0)
        {
            throw new IOException($"rename {next} to {link} failed (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
        }
    }
    finally
    {
        try { RemoveLink(next); } catch { /* a leftover temp link is harmless; the next swap removes it */ }
    }
}

bool PathsEqual(string? a, string? b) =>
    a is not null && b is not null && string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

// True for a folder, a file, or a link entry whose target is gone (Directory.Exists follows the link and says no).
bool LinkExists(string path)
{
    if (Directory.Exists(path) || File.Exists(path))
    {
        return true;
    }

    FileAttributes attributes = new DirectoryInfo(path).Attributes;
    return (int)attributes != -1 && attributes.HasFlag(FileAttributes.ReparsePoint);
}

// Removes the link entry only, never what it points at. A real folder in the way is an error, not something to delete.
void RemoveLink(string path)
{
    if (!LinkExists(path))
    {
        return;
    }

    if (File.Exists(path) && !new FileInfo(path).Attributes.HasFlag(FileAttributes.Directory))
    {
        File.Delete(path);
        return;
    }

    Directory.Delete(path, false);
}

void CreateLink(string link, string target)
{
    if (!OperatingSystem.IsWindows())
    {
        Directory.CreateSymbolicLink(link, target);
        return;
    }

    ProcessStartInfo info = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add("/c");
    info.ArgumentList.Add("mklink");
    info.ArgumentList.Add("/J");
    info.ArgumentList.Add(link);
    info.ArgumentList.Add(target);
    using Process mk = Process.Start(info)!;
    mk.StandardOutput.ReadToEnd();
    string error = mk.StandardError.ReadToEnd();
    mk.WaitForExit();
    if (mk.ExitCode != 0)
    {
        throw new InvalidOperationException($"mklink /J {link} failed (exit {mk.ExitCode}): {error.Trim()}");
    }
}

void RemoveOldBuilds(string buildsDir, string?[] keep)
{
    HashSet<string> kept = [.. keep.Where(p => p is not null).Select(p => Path.GetFullPath(p!).ToLowerInvariant())];
    foreach (string folder in Directory.GetDirectories(buildsDir))
    {
        if (kept.Contains(Path.GetFullPath(folder).ToLowerInvariant()))
        {
            continue;
        }

        try
        {
            string doomed = folder.EndsWith(".deleting", StringComparison.Ordinal) ? folder : $"{folder}.deleting";
            if (doomed != folder)
            {
                Directory.Move(folder, doomed);
            }

            Directory.Delete(doomed, true);
        }
        catch
        {
            // held by a running server or hook: kept until a later build
        }
    }
}

// ---- the build stamp ----

// Every file under the inputs, hashed with its relative path. A folder's bin/ and obj/ are build output, never
// source, so a publish never makes its own build stale. The same hash the Node step computed.
string TreeHash()
{
    List<string> files = [];
    void Walk(string path)
    {
        if (File.Exists(path))
        {
            files.Add(path);
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string entry in Directory.GetFileSystemEntries(path))
        {
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry) && (name == "bin" || name == "obj"))
            {
                continue;
            }

            Walk(entry);
        }
    }

    foreach (string input in buildInputs)
    {
        Walk(Path.Combine(root, input));
    }

    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    byte[] zero = [0];
    foreach (string file in files.Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal))
    {
        hash.AppendData(Encoding.UTF8.GetBytes(file));
        hash.AppendData(zero);
        hash.AppendData(File.ReadAllBytes(Path.Combine(root, file)));
        hash.AppendData(zero);
    }

    return Convert.ToHexStringLower(hash.GetHashAndReset());
}

string? ReadStamp(string binDir)
{
    string file = Path.Combine(binDir, "build-stamp.txt");
    return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    internal static extern int rename(string oldPath, string newPath);
}
