// Most verbs move here starting in phase 3 (RESTRUCTURE.md section 4), once Aitm.Cli calls
// Aitm.Server over HTTP instead of the old aitm.cs. `hook` is the one exception, added early by slice
// 20 ("Hooks, part 1", RESTRUCTURE.md:499): aitm.cs and mcp.cs stay untouched for the rest of phase 2
// (section 0 rule 3), so the new `aitm hook <event>` verb has nowhere else to live that both keeps
// hooks.json pointing at a real command and never edits the old host. Phase 4 (RESTRUCTURE.md:531)
// then moves these same handlers to `type: http`, so this in-process call is transitional, not the
// pattern later CLI verbs will follow.
using Aitm.Brain.Data;
using Aitm.Cli.Tools;
using Aitm.Hooks.Tools;

namespace Aitm.Cli;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "hook")
        {
            RunHook(args[1]);
            return 0;
        }

        // The service starts on the first call and stops when idle, so no logon task is installed any more.
        // uninstall-logon stays, so an old install can remove the task it has.
        if (args.Length >= 1 && args[0] == "service")
            return ServiceCommand.RunDefault(args, Console.Out, Console.Error);

        // Slice P1: Claude Code's MCP server over stdio; forwards every tool call to the service.
        if (args.Length >= 1 && args[0] == "mcp") return McpBridge.RunDefault();

        if (args.Length >= 2 && args[0] == "server" && args[1] == "uninstall-logon")
        {
            int exitCode = ServerLogonCommand.Uninstall(new ProcessRunner(), out string error);
            if (error.Length > 0) Console.Error.WriteLine(error);
            return exitCode;
        }

        // Slice 29d: every other verb runs on Aitm.Server through POST /cli (ThinClient). The verbs above
        // stay local: hooks must answer with the server down.
        return ThinClient.RunDefault(args, Console.Out, Console.Error);
    }

    private static void RunHook(string eventName)
    {
        string stdin;
        try
        {
            stdin = Console.In.ReadToEnd();
        }
        catch
        {
            stdin = "";
        }

        string output;
        try
        {
            output = eventName switch
            {
                "PreCompact" => CompactBriefTool.Execute(stdin),
                "UserPromptSubmit" => CompactRestoreTool.Execute(stdin),
                // Slice 30: these handlers need Memory, Docs, Graph and Store, so the server runs them.
                _ when HookForwarder.Deadlines.ContainsKey(eventName) => HookForwarder.ForwardDefault(eventName, stdin),
                // Slice 28: start the server when /health does not answer; prints nothing, exits 0.
                "SessionStart" => RunSessionStart(),
                _ => "",
            };
        }
        catch
        {
            // Hooks must never block or crash a session.
            output = "";
        }

        if (output.Length > 0) Console.Out.Write(output);
    }

    private static string RunSessionStart()
    {
        SessionStartServerCheck.RunDefault();
        return "";
    }
}
