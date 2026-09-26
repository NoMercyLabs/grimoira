using System.Text.Json;

namespace Aitm.Cli.Tools;

/// <summary>
/// `aitm service start|stop|status`: the service normally starts on the first call and exits when idle, so
/// these are for looking and for stopping it by hand. Plain output, one short line each.
/// </summary>
public static class ServiceCommand
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(20);

    public static int Run(string[] args, string dataDir, Func<bool> ensureServer, TextWriter stdout, TextWriter stderr)
    {
        switch (args.Length >= 2 ? args[1] : "")
        {
            case "start":
                if (!ensureServer())
                {
                    stderr.WriteLine("error: the aitm service did not start.");
                    return 1;
                }
                stdout.WriteLine("running");
                return 0;
            case "stop":
                return Stop(dataDir, stdout, stderr);
            case "status":
                stdout.WriteLine(Status(dataDir));
                return 0;
            default:
                stderr.WriteLine("usage: aitm service start|stop|status");
                return 2;
        }
    }

    public static int RunDefault(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, ServerAutoStart.DefaultDataDir(), ServerAutoStart.EnsureRunning, stdout, stderr);

    private static string Status(string dataDir)
    {
        try
        {
            using HttpClient client = PipeConnection.CreateClient(dataDir, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
            using HttpResponseMessage response = client.GetAsync("/health").GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode) return "stopped";
            using JsonDocument doc = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            JsonElement root = doc.RootElement;
            string version = root.TryGetProperty("version", out JsonElement v) ? v.GetString() ?? "?" : "?";
            string build = root.TryGetProperty("buildStamp", out JsonElement b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : "none";
            int seconds = root.TryGetProperty("idleExitSeconds", out JsonElement i) ? i.GetInt32() : ServerAddress.DefaultIdleMinutes * 60;
            string idle = seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
            return $"running (version {version}, build {build}, idle exit {idle})";
        }
        catch
        {
            return "stopped";
        }
    }

    private static int Stop(string dataDir, TextWriter stdout, TextWriter stderr)
    {
        if (!ServerAutoStart.IsHealthy(dataDir, TimeSpan.FromSeconds(1)))
        {
            stdout.WriteLine("stopped");
            return 0;
        }
        try
        {
            using HttpClient client = PipeConnection.CreateClient(dataDir, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
            using HttpResponseMessage response = client.PostAsync("/shutdown", null).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                stderr.WriteLine($"error: the aitm service refused to stop ({(int)response.StatusCode}).");
                return 1;
            }
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"error: could not ask the aitm service to stop ({ex.GetType().Name}).");
            return 1;
        }
        // The service finishes its calls in flight first, so this can take a while.
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < StopWait)
        {
            if (!ServerAutoStart.IsHealthy(dataDir, TimeSpan.FromSeconds(1)))
            {
                stdout.WriteLine("stopped");
                return 0;
            }
            Thread.Sleep(200);
        }
        stdout.WriteLine("stopping (a call is still finishing)");
        return 0;
    }
}
