using Grimoira.Brain.Data;

namespace Grimoira.Cli.Tools;

/// <summary>
/// RESTRUCTURE.md slice 27 ("Start the server at logon"): the task definition shared by the Windows
/// Task Scheduler entry (wired up below) and the macOS launchd / Linux systemd-user equivalents
/// (<see cref="LaunchdLogonAgent"/>, <see cref="SystemdUserLogonUnit"/> — builders only, no install
/// command yet). Pure data; no OS call lives here.
/// </summary>
public sealed record WindowsLogonTaskDefinition(
    string TaskName,
    string UserName,
    TimeSpan Delay,
    bool RunElevated,
    int RestartCount,
    TimeSpan RestartInterval,
    string ExecutablePath,
    string WorkingDirectory);

/// <summary>
/// The logon task definition for Grimoira.Server: the data the launchd and systemd builders below share, and the
/// task name `uninstall-logon` deletes.
/// </summary>
public static class WindowsLogonTask
{
    public static WindowsLogonTaskDefinition BuildDefinition(string executablePath, string userName) =>
        new(
            TaskName: "Grimoira.Server",
            UserName: userName,
            Delay: TimeSpan.FromSeconds(30),
            RunElevated: false,
            RestartCount: 3,
            RestartInterval: TimeSpan.FromMinutes(1),
            ExecutablePath: executablePath,
            WorkingDirectory: WindowsDirectoryOf(executablePath));

    // A Windows path, split the Windows way on any host: Path.GetDirectoryName on Linux does not treat
    // '\' as a separator and returns "" for "C:\Program Files\Grimoira\Grimoira.Server.exe".
    private static string WindowsDirectoryOf(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        if (cut <= 0) return "";
        string dir = path[..cut];
        return dir.EndsWith(':') ? dir + "\\" : dir; // "C:\a.exe" -> "C:\", as on Windows
    }

    public static IReadOnlyList<string> BuildDeleteArgs(string taskName) =>
        ["/Delete", "/TN", taskName, "/F"];
}

/// <summary>
/// Removes the Grimora.Server logon task an old install left (the service starts on demand now; nothing installs it). `schtasks` is only ever reached through the
/// injected <see cref="IProcessRunner"/>; nothing here calls the real Task Scheduler.
/// </summary>
public static class ServerLogonCommand
{
    public static int Uninstall(IProcessRunner runner) => Uninstall(runner, out _);

    public static int Uninstall(IProcessRunner runner, out string error)
    {
        (string _, string stderr, int exitCode) = runner.Run("schtasks", WindowsLogonTask.BuildDeleteArgs("Grimora.Server"));
        if (exitCode == 0 || stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            error = "";
            return 0;
        }
        error = $"schtasks failed to remove the Grimora.Server logon task: {stderr.Trim()}";
        return 1;
    }
}

/// <summary>
/// Pure builder for the macOS launchd LaunchAgent equivalent (RESTRUCTURE.md slice 27: written now,
/// wired up by a later macOS card). RunAtLoad plus a `sleep` before `exec` stands in for the ~30s logon
/// delay; KeepAlive.SuccessfulExit=false restarts it on failure with no overall stop-after limit.
/// </summary>
public static class LaunchdLogonAgent
{
    public static string BuildPlist(WindowsLogonTaskDefinition def)
    {
        int delaySeconds = (int)def.Delay.TotalSeconds;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>tv.nomercy.grimora.server</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/bin/sh</string>
                    <string>-c</string>
                    <string>sleep {delaySeconds} &amp;&amp; exec {def.ExecutablePath}</string>
                </array>
                <key>WorkingDirectory</key>
                <string>{def.WorkingDirectory}</string>
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <dict>
                    <key>SuccessfulExit</key>
                    <false/>
                </dict>
            </dict>
            </plist>
            """;
    }
}

/// <summary>
/// Pure builder for the Linux systemd --user unit equivalent (RESTRUCTURE.md slice 27: written now,
/// wired up by a later Linux card). `ExecStartPre` stands in for the ~30s logon delay; `Restart=on-failure`
/// with `StartLimitIntervalSec=0` restarts with no overall stop-after limit.
/// </summary>
public static class SystemdUserLogonUnit
{
    public static string Build(WindowsLogonTaskDefinition def)
    {
        int delaySeconds = (int)def.Delay.TotalSeconds;
        int restartIntervalSeconds = (int)def.RestartInterval.TotalSeconds;
        return $"""
            [Unit]
            Description=Grimoira.Server

            [Service]
            ExecStartPre=/bin/sleep {delaySeconds}
            ExecStart={def.ExecutablePath}
            WorkingDirectory={def.WorkingDirectory}
            Restart=on-failure
            RestartSec={restartIntervalSeconds}
            StartLimitIntervalSec=0

            [Install]
            WantedBy=default.target
            """;
    }
}
