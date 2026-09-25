using System.Xml.Linq;
using Aitm.Brain.Data;

namespace Aitm.Cli.Tools;

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
/// Builds the Task Scheduler definition for Aitm.Server. Runs only when the given user is logged on
/// (LogonType InteractiveToken), not elevated (RunLevel LeastPrivilege), ~30s after logon, restarting
/// up to 3 times 1 minute apart on failure, with no overall stop-after duration.
/// </summary>
public static class WindowsLogonTask
{
    public static WindowsLogonTaskDefinition BuildDefinition(string executablePath, string userName) =>
        new(
            TaskName: "Aitm.Server",
            UserName: userName,
            Delay: TimeSpan.FromSeconds(30),
            RunElevated: false,
            RestartCount: 3,
            RestartInterval: TimeSpan.FromMinutes(1),
            ExecutablePath: executablePath,
            WorkingDirectory: WindowsDirectoryOf(executablePath));

    // A Windows path, split the Windows way on any host: Path.GetDirectoryName on Linux does not treat
    // '\' as a separator and returns "" for "C:\Program Files\Aitm\Aitm.Server.exe".
    private static string WindowsDirectoryOf(string path)
    {
        int cut = path.LastIndexOfAny(['\\', '/']);
        if (cut <= 0) return "";
        string dir = path[..cut];
        return dir.EndsWith(':') ? dir + "\\" : dir; // "C:\a.exe" -> "C:\", as on Windows
    }

    /// <summary>The Task Scheduler XML (schema 1.2) for <paramref name="def"/>. A pure function: no
    /// file or process I/O.</summary>
    public static string BuildTaskXml(WindowsLogonTaskDefinition def)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XDocument doc = new(
            new XElement(ns + "Task",
                new XAttribute("version", "1.2"),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", def.UserName),
                        new XElement(ns + "Delay", $"PT{(int)def.Delay.TotalSeconds}S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", def.UserName),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", def.RunElevated ? "HighestAvailable" : "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", $"PT{(int)def.RestartInterval.TotalMinutes}M"),
                        new XElement(ns + "Count", def.RestartCount))),
                new XElement(ns + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", def.ExecutablePath),
                        new XElement(ns + "WorkingDirectory", def.WorkingDirectory)))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    public static IReadOnlyList<string> BuildCreateArgs(string taskName, string xmlPath) =>
        ["/Create", "/TN", taskName, "/XML", xmlPath, "/F"];

    public static IReadOnlyList<string> BuildDeleteArgs(string taskName) =>
        ["/Delete", "/TN", taskName, "/F"];
}

/// <summary>
/// Installs and uninstalls the Aitm.Server logon task. `schtasks` is only ever reached through the
/// injected <see cref="IProcessRunner"/>; nothing here calls the real Task Scheduler.
/// </summary>
public static class ServerLogonCommand
{
    public static string DefaultXmlPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aitm", "Aitm.Server.task.xml");

    public static string DefaultUserName => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public static int Install(string executablePath, string userName, string xmlPath, IProcessRunner runner) =>
        Install(executablePath, userName, xmlPath, runner, out _);

    public static int Install(string executablePath, string userName, string xmlPath, IProcessRunner runner, out string error)
    {
        WindowsLogonTaskDefinition def = WindowsLogonTask.BuildDefinition(executablePath, userName);
        string xml = WindowsLogonTask.BuildTaskXml(def);
        string? dir = Path.GetDirectoryName(xmlPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(xmlPath, xml);

        (string _, string stderr, int exitCode) = runner.Run("schtasks", WindowsLogonTask.BuildCreateArgs(def.TaskName, xmlPath));
        error = exitCode == 0 ? "" : $"schtasks failed to install the Aitm.Server logon task: {stderr.Trim()}";
        return exitCode == 0 ? 0 : 1;
    }

    public static int Uninstall(IProcessRunner runner) => Uninstall(runner, out _);

    public static int Uninstall(IProcessRunner runner, out string error)
    {
        (string _, string stderr, int exitCode) = runner.Run("schtasks", WindowsLogonTask.BuildDeleteArgs("Aitm.Server"));
        if (exitCode == 0 || stderr.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            error = "";
            return 0;
        }
        error = $"schtasks failed to remove the Aitm.Server logon task: {stderr.Trim()}";
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
                <string>tv.nomercy.aitm.server</string>
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
            Description=Aitm.Server

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
